/**
 * Actor pool: a lightweight combinator over a homogeneous set of Workers.
 *
 * All members spawn from the same factory and serve the same RPC surface, so
 * the pool exposes a single typed call surface (`Remote<T>`) that routes each
 * call to a member. The pool is deliberately NOT a task scheduler: no queue,
 * no work-stealing — it only picks a member, calls its proxy (reusing the
 * exact per-member `createRpcProxy` path that spawn already uses), and tracks
 * liveness.
 *
 * Routing:
 *   - "round-robin" (default): a counter over live members.
 *   - "least-busy": the live member with the fewest in-flight calls.
 *   - a custom function (method, args) => index; the index is validated and
 *     the member must be live, otherwise the call rejects.
 *   - all members dead → calls reject with ActorDiedError.
 *
 * Member lifecycle:
 *   - spawn's onDeath (crash/handshake failure; NOT dispose) marks the member
 *     dead, drops it from routing, fires onMemberDead, and — when `replace`
 *     is enabled — rebuilds it from the factory.
 *   - dispose() terminates every member (idempotent); afterwards calls reject.
 *
 * Member-bound payloads (documented constraint): plain data routes freely; a
 * main-thread-created callback routes to any member (it executes on the main
 * thread regardless of holder); object references (remote-ref) fresh tokens
 * route anywhere, but a refId-only (moved) token must not cross members — use
 * invokeOn(index) to pin the owning member; streams bind to the producing
 * member's lifetime and are consumed by the caller, never re-routed.
 */

import { ActorDiedError } from "./core/protocol.ts";
import type { Codec } from "./core/codec.ts";
import type { Transport } from "./core/transport.ts";
import { attachLazyIterator, spawn } from "./spawn_worker.ts";
import type { ActorHandle, Remote } from "./spawn_worker.ts";
import type { TransferArgs } from "./core/transfer.ts";
import type { CodecValueTypes } from "./core/type-utils.ts";

export interface ActorPoolOptions<
  C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
> {
  /** Number of members. Default 1. */
  size?: number;
  /** Factory for a member's Worker or Transport; called once per member (and per replace). */
  spawnWorker: () => Worker | Transport;
  /** Codecs passed to every member's spawn (per-member handshake fingerprint).
   *  An `as const` tuple also drives the type projection of each member's
   *  Remote<T> surface, exactly like spawn's codecs option. */
  codecs?: C;
  /** Creation interruption passed to every member's spawn. */
  signal?: AbortSignal | null;
  /** Argument-side move policy passed to every member's spawn. */
  transferArgs?: TransferArgs;
  /** Member selection strategy. Default "round-robin". */
  routing?:
    | "round-robin"
    | "least-busy"
    | ((method: string, args: unknown[]) => number);
  /** Auto-rebuild a member that died (crash/handshake failure). Default false. */
  replace?: boolean | (() => Worker | Transport);
  /** Fired when a member dies (crash/handshake failure; not dispose). */
  onMemberDead?: (index: number, reason: unknown) => void;
}

export type ActorPool<T, Pass extends unknown = never> = Remote<T, Pass> & {
  /** Terminate every member; idempotent. Calls after dispose reject. */
  dispose(): Promise<void>;
  /** Number of live members. */
  readonly size: number;
  /**
   * Escape hatch for member-bound payloads (references, callbacks, streams):
   * invoke a specific member by index, bypassing routing. The member must be
   * live or the call rejects.
   */
  invokeOn(index: number, method: string, args: unknown[]): Promise<unknown>;
};

interface Member<T, Pass extends unknown = never> {
  /** The spawned actor; undefined until spawn() resolves. */
  actor?: Remote<T, Pass> & ActorHandle;
  /** Resolves when this slot's spawn() settles (success or failure). */
  ready: Promise<void>;
  stop: AbortController;
  inFlight: number;
  /** Live = not dead AND not disposed (a slot still spawning counts as live). */
  dead: boolean;
}

const EMPTY: unique symbol = Symbol("pool-empty");

export function createActorPool<
  T,
  const C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
>(
  options: ActorPoolOptions<C>,
): ActorPool<T, CodecValueTypes<C>> {
  const size = options.size ?? 1;
  // Every index keeps a stable slot; an actor arrives asynchronously via
  // spawn(). A slot that is still spawning (actor undefined) is treated as
  // live-but-not-ready: routing skips it; once ready it joins routing.
  const members: (Member<T, CodecValueTypes<C>> | typeof EMPTY)[] = Array(size)
    .fill(EMPTY);
  let disposed = false;
  let disposal: Promise<void> | undefined;
  const spawns = new Set<Promise<void>>();
  let rrCursor = 0;

  const spawnMember = (index: number, factory = options.spawnWorker): void => {
    const slot: Member<T, CodecValueTypes<C>> = {
      inFlight: 0,
      dead: false,
      ready: Promise.resolve(),
      stop: new AbortController(),
    };
    members[index] = slot;
    const lifetime = options.signal === null ? slot.stop.signal : AbortSignal.any([
      slot.stop.signal,
      options.signal ?? AbortSignal.timeout(10_000),
    ]);
    const ready = Promise.resolve().then(() =>
      spawn<T, C>(factory(), {
        codecs: options.codecs,
        signal: lifetime,
        transferArgs: options.transferArgs,
        onDeath: (reason) => {
          if (disposed || slot.dead) return;
          slot.dead = true;
          options.onMemberDead?.(index, reason);
          if (options.replace && !disposed) {
            spawnMember(
              index,
              typeof options.replace === "function" ? options.replace : options.spawnWorker,
            );
          }
        },
      })
    );
    slot.ready = ready.then(
      async (actor) => {
        if (disposed) {
          await actor.dispose();
          return;
        }
        slot.actor = actor;
      },
      (error) => {
        if (!slot.dead && !disposed) {
          slot.dead = true;
          options.onMemberDead?.(index, error);
        }
      },
    );
    spawns.add(slot.ready);
    void slot.ready.then(() => spawns.delete(slot.ready), () => {});
  };

  for (let i = 0; i < size; i++) spawnMember(i);

  const isLive = (m: Member<T>): boolean => !disposed && !m.dead && m.actor !== undefined;

  const liveMembers = (): Member<T>[] =>
    members.filter((m): m is Member<T> => m !== EMPTY && isLive(m));

  const pickMember = (
    method: string,
    args: unknown[],
  ): Member<T> | undefined => {
    const strategy = options.routing ?? "round-robin";
    const live = liveMembers();
    if (live.length === 0) return undefined;
    if (typeof strategy === "function") {
      const idx = strategy(method, args);
      if (!Number.isInteger(idx) || idx < 0 || idx >= size) {
        throw new RangeError(
          `pool routing function returned invalid member index ${idx}`,
        );
      }
      const m = members[idx];
      return m !== EMPTY && isLive(m) ? m : undefined;
    }
    if (strategy === "least-busy") {
      return live.reduce((a, b) => (a.inFlight <= b.inFlight ? a : b));
    }
    // round-robin
    const start = rrCursor % size;
    for (let i = 0; i < size; i++) {
      const idx = (start + i) % size;
      const m = members[idx];
      if (m !== EMPTY && isLive(m)) {
        rrCursor = idx + 1;
        return m;
      }
    }
    return undefined;
  };

  /**
   * Wait until at least one member is ready (spawn resolved), then re-pick.
   * Returns undefined if the pool is disposed or every member is dead.
   */
  const waitForReady = async (): Promise<void> => {
    const pending = members.filter((m): m is Member<T> =>
      m !== EMPTY && !m.dead && m.actor === undefined
    );
    if (pending.length === 0) return;
    await Promise.race(pending.map((m) => m.ready));
  };

  const call = (method: string, args: unknown[]): Promise<unknown> => {
    if (disposed) return Promise.reject(new ActorDiedError());
    // Resolve the member synchronously when possible; otherwise wait for the
    // first slot to finish spawning, then re-pick. The returned promise is a
    // single flattened chain: once a member is chosen, the member's call
    // promise is returned through .then (flattened), so a stream-returning
    // method resolves the caller's promise into the AsyncIterable — and
    // attachLazyIterator below is applied to that exact promise.
    let member = pickMember(method, args);
    const ready = member ? Promise.resolve() : waitForReady().then(() => {
      member = pickMember(method, args);
    });
    const out = ready.then(() => {
      if (disposed || !member) throw new ActorDiedError();
      member.inFlight++;
      const p = (member.actor![method as keyof Remote<T>] as unknown as (
        ...a: unknown[]
      ) => Promise<unknown>)(...args);
      p.finally(() => member!.inFlight--).catch(() => {});
      return p; // flattened: `out` resolves with the member's result
    });
    attachLazyIterator(out);
    return out;
  };

  const invokeOn = (
    index: number,
    method: string,
    args: unknown[],
  ): Promise<unknown> => {
    if (disposed) return Promise.reject(new ActorDiedError());
    let member = members[index];
    const ready = member !== EMPTY && isLive(member)
      ? Promise.resolve()
      : (member !== EMPTY && !member.dead ? member.ready : Promise.resolve())
        .then(() => {
          member = members[index];
        });
    const out = ready.then(() => {
      if (disposed || member === EMPTY || !isLive(member)) throw new ActorDiedError();
      const m = member;
      m.inFlight++;
      const p = (m.actor![method as keyof Remote<T>] as unknown as (
        ...a: unknown[]
      ) => Promise<unknown>)(...args);
      p.finally(() => m.inFlight--).catch(() => {});
      return p;
    });
    attachLazyIterator(out);
    return out;
  };

  const dispose = (): Promise<void> => {
    if (disposal) return disposal;
    disposed = true;
    const cleanup = members.flatMap((member) => {
      if (member === EMPTY) return [];
      member.dead = true;
      member.stop.abort(new ActorDiedError());
      return member.actor ? [member.actor.dispose()] : [];
    });
    disposal = Promise.allSettled([...spawns, ...cleanup]).then((results) => {
      const errors = results.flatMap((r) => r.status === "rejected" ? [r.reason] : []);
      if (errors.length) throw new AggregateError(errors, "Actor pool cleanup failed");
    });
    void disposal.catch(() => {});
    return disposal;
  };

  const pool = new Proxy({} as ActorPool<T>, {
    get(_target, prop) {
      if (prop === "dispose") return dispose;
      if (prop === Symbol.dispose) return () => void dispose();
      if (prop === "size") return liveMembers().length;
      if (prop === "invokeOn") return invokeOn;
      // The proxy must not be detected as a thenable, or await behavior breaks.
      if (prop === "then") return undefined;
      if (typeof prop === "string") {
        return (...args: unknown[]) => call(prop, args);
      }
      return undefined;
    },
  });

  return pool;
}
