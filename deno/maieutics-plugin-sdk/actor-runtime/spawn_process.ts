import { connectProcessActor } from "./core/process_actor.ts";
import { aggregateFailures } from "./core/failures.ts";
/** Host-only process creation; excluded from the browser module graph. */
import { type Codec, PayloadCodecRegistry } from "./core/codec.ts";
import { ActorDiedError, type Frame } from "./core/protocol.ts";
import { fromNodeIpc, type Transport } from "./core/transport.ts";
import { createRpcProxy, requestFrame, type RpcProxy } from "./core/rpc.ts";
import { handshakeError } from "./core/handshake.ts";
import { iterableCodec } from "./core/codecs/iterable.ts";
import { errorCodec } from "./core/codecs/error.ts";
import { abortSignalCodec } from "./core/codecs/abort_signal.ts";
import { callbackCodec } from "./core/codecs/callback.ts";
import type { TransferArgs } from "./core/transfer.ts";
import type { CodecValueTypes } from "./core/type-utils.ts";
import type { ActorRegistry } from "./core/registry.ts";
import {
  type ActorHandle,
  attachLazyIterator,
  getActorRegistry,
  type Remote,
} from "./spawn_worker.ts";

import { frameRecord, isResponse } from "./core/validation.ts";
import { ActorTransportError } from "./core/lifetime.ts";

const actorRegistry = getActorRegistry();
export interface SpawnProcessOptions<
  C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
> {
  /** Extra codecs, matched before the built-ins (same semantics as SpawnOptions.codecs). */
  codecs?: C;
  /** Creation interruption (same semantics as SpawnOptions.signal). */
  signal?: AbortSignal | null;
  /** Fired when the process actor dies (crash / handshake failure; not dispose). */
  onDeath?: (reason: unknown) => void;
  /** Argument-side move policy for direct calls on the process transport. */
  transferArgs?: TransferArgs;
  /**
   * Deno permissions granted to the child process (passed to Deno.Command's
   * `permissions` option). The caller controls exactly what the actor process
   * can access. Omit for an allow-everything (-A) child.
   */
  permissions?: Deno.PermissionOptionsObject;
  /** Extra CLI args passed to the child `deno run` invocation (e.g. unstable flags). */
  denoArgs?: string[];
}

/**
 * Spawn an actor in a separate Deno process. The child runs the module at
 * `entrypoint` which must call serveProcess(rpc) at top level. Communication
 * uses node:child_process fork IPC (serialization 'advanced') — a dedicated
 * out-of-band channel that child stdout/stderr logging cannot pollute.
 *
 * The child's Deno permissions are controlled via `permissions`; the RPC
 * surface type is derived from `typeof module.rpc` on the child module.
 *
 *   // child.ts
 *   import { serveProcess } from ".../worker_runtime.ts";
 *   export const rpc = { add(a: number, b: number) { return a + b; } };
 *   serveProcess(rpc);
 *
 *   // main.ts
 *   import type * as ChildModule from "./child.ts";
 *   const actor = await spawnProcess<typeof ChildModule.rpc>("./child.ts", {
 *     permissions: { read: true },
 *   });
 *   await actor.add(1, 2);
 */
export async function spawnProcess<
  T,
  const C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
>(
  entrypoint: string,
  options: SpawnProcessOptions<C> = {},
): Promise<Remote<T, CodecValueTypes<C>> & ActorHandle> {
  return await connectProcessActor<T, C>(await startProcess(entrypoint, options), options);
}

async function startProcess(entrypoint: string, options: SpawnProcessOptions) {
  const { spawn: cpSpawn } = await import("node:child_process");
  const args = ["run", ...permissionArgs(options.permissions), entrypoint];
  if (options.denoArgs) args.splice(1, 0, ...options.denoArgs);
  const child = cpSpawn(Deno.execPath(), args, {
    stdio: ["ipc", "pipe", "pipe"],
    serialization: "advanced",
  } as never);
  child.stdout?.resume();
  child.stderr?.resume();
  const exited = Promise.withResolvers<void>();
  const finished = Promise.withResolvers<void>();
  void finished.promise.catch(() => {});
  let stopped = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let deadline: ReturnType<typeof setTimeout> | undefined;
  const transport = fromNodeIpc((frame) => child.send(frame as never));
  const stop = (): Promise<void> => {
    if (stopped) return finished.promise;
    stopped = true;
    deadline = setTimeout(
      () => finished.reject(new Error("Actor process shutdown budget exceeded")),
      5000,
    );
    try {
      if (child.exitCode === null && child.signalCode === null) child.kill();
      timer = setTimeout(() => {
        try {
          child.kill("SIGKILL");
        } catch (error) {
          finished.reject(error);
        }
      }, 1000);
    } catch (error) {
      finished.reject(error);
    }
    void exited.promise.then(() => {
      clearTimeout(timer);
      clearTimeout(deadline);
      finished.resolve();
    });
    return finished.promise;
  };
  transport.onClose(() => {
    void stop();
  });
  child.on("message", (frame) => transport.deliver(frame));
  child.once("exit", () => {
    exited.resolve();
    transport.close(new Error("Actor process exited"));
  });
  child.once("error", (error) => {
    if (!child.pid) exited.resolve();
    transport.close(error);
  });
  return { child, transport, stop, completion: finished.promise };
}

/** Map a Deno PermissionOptionsObject to `--allow-*` CLI flags for the child. */
function permissionArgs(permissions?: Deno.PermissionOptionsObject): string[] {
  if (!permissions) return ["--allow-all"];
  const out: string[] = [];
  const kinds: (keyof Deno.PermissionOptionsObject)[] = [
    "read",
    "write",
    "net",
    "env",
    "run",
    "sys",
    "ffi",
  ];
  for (const kind of kinds) {
    const value = permissions[kind];
    if (value === true) out.push(`--allow-${kind}`);
    else if (typeof value === "string" || Array.isArray(value)) {
      const list = Array.isArray(value) ? value : [value];
      out.push(`--allow-${kind}=${list.join(",")}`);
    }
    // undefined / false: deny (no flag)
  }
  return out;
}

export interface SpawnNodeOptions<
  C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
> {
  /**
   * Extra codecs to register, matched before the built-ins (same semantics as
   * SpawnOptions.codecs). The node process must register the same set via
   * serveNode's `codecs` option; the handshake validates the tag lists and a
   * mismatch kills the connection.
   */
  codecs?: C;
  /** Creation interruption (same semantics as SpawnOptions.signal). */
  signal?: AbortSignal | null;
  /** Fired when the node process dies (crash / handshake failure; not dispose). */
  onDeath?: (reason: unknown) => void;
  /** Argument-side move policy for direct calls on node actor channels. */
  transferArgs?: TransferArgs;
  /** Deno permissions granted to the child node process. */
  permissions?: Deno.PermissionOptionsObject;
  /** Extra CLI args for the child `deno run` invocation. */
  denoArgs?: string[];
}

export type SpawnedNode<T extends Record<string, object>, Pass = never> =
  & {
    [K in keyof T]: Remote<T[K], Pass>;
  }
  & {
    dispose(): Promise<void>;
    readonly completion: Promise<void>;
    /**
     * The node's underlying transport: open additional actor channels directly
     * via connectActor, and route reference-acquire frames.
     */
    readonly transport: Transport;
    /** Registry entry for this node connection (refIds of refs its actors own). */
    readonly registryId: string;
    /** The coordinator registry (transport id → Transport) for advanced routing. */
    getRegistry(): ActorRegistry;
  };

/**
 * Spawn a multi-actor node process (model B): the child module calls
 * serveNode(actors) and announces its actor names; this returns a
 * { [name]: Remote } surface where each actor runs on its own logical
 * channel over the fork IPC connection.
 *
 *   // node.ts
 *   import { serveNode } from ".../worker_runtime.ts";
 *   export const actors = { counter: { inc(n) { return n + 1; } } };
 *   serveNode(actors);
 *
 *   // main.ts
 *   import type * as NodeModule from "./node.ts";
 *   const node = await spawnNode<typeof NodeModule.actors>("./node.ts");
 *   await node.counter.inc(1);
 *   await node.dispose();
 */
export async function spawnNode<
  T extends Record<string, object>,
  const C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
>(
  entrypoint: string,
  options: SpawnNodeOptions<C> = {},
): Promise<SpawnedNode<T, CodecValueTypes<C>>> {
  const process = await startProcess(entrypoint, options);
  const { transport } = process;
  let dead = false;
  const rpcProxies: RpcProxy[] = [];
  const registryId = actorRegistry.register(transport);
  // Codec registry: custom codecs first, then the built-ins — the same setup
  // as spawn(), validated against the node's handshake tag list below.
  const registry = new PayloadCodecRegistry();
  for (const codec of options.codecs ?? []) registry.register(codec);
  for (
    const codec of [iterableCodec, errorCodec, abortSignalCodec, callbackCodec]
  ) {
    if (!registry.has(codec.tag)) registry.register(codec);
  }

  const terminal = Promise.withResolvers<void>();
  const completion = terminal.promise.then(async () => {
    const results = await Promise.allSettled([registry.join(), process.completion]);
    const errors = results.flatMap((r) => r.status === "rejected" ? [r.reason] : []);
    if (errors.length) throw aggregateFailures(errors, "Actor node cleanup failed");
  });
  void completion.catch(() => {});
  function finish(reason: unknown, deliberate = false): void {
    if (dead) return;
    dead = true;
    for (const proxy of rpcProxies) proxy.rejectAll(reason);
    registry.failAll();
    actorRegistry.unregister(registryId);
    if (!deliberate) {
      try {
        options.onDeath?.(reason);
      } catch (error) {
        registry.trackTask(Promise.reject(error));
      }
    }
    try {
      transport.close(reason);
    } catch (error) {
      registry.trackTask(Promise.reject(error));
    } finally {
      void process.stop();
      terminal.resolve();
    }
  }
  transport.onClose((reason) => finish(reason));
  const startup = Promise.withResolvers<string[]>();
  const interrupt = options.signal === undefined ? AbortSignal.timeout(10_000) : options.signal;
  const cancel = () => {
    finish(interrupt?.reason);
    startup.reject(interrupt?.reason);
  };
  const stopObserving = transport.onClose(startup.reject);
  transport.onMessage((ev) => {
    const frame = frameRecord(ev.data) as unknown as Frame;
    if (frame.type !== "handshake") return;
    const mismatch = handshakeError(frame, registry, transport.kind);
    if (mismatch) {
      finish(mismatch);
      startup.reject(mismatch);
    } else startup.resolve(frame.actors ?? []);
  });
  if (interrupt?.aborted) cancel();
  else interrupt?.addEventListener("abort", cancel, { once: true });
  let actors: string[];
  try {
    actors = await startup.promise;
  } catch (error) {
    finish(error);
    await completion;
    throw error;
  } finally {
    stopObserving();
    interrupt?.removeEventListener("abort", cancel);
  }

  // Open one channel per actor; each runs standard RPC frames over it.
  const surface = {} as Record<string, unknown>;
  for (const name of actors) {
    const opened = transport.openChannel();
    // Tell the node which actor this channel serves: the open token rides as
    // a Mux control frame (the node's Mux opens the channel + acks), and the
    // __open-actor frame names it. Both go on the main channel in order.
    transport.send(opened.token);
    transport.send({ type: "__open-actor", name, token: opened.token });
    const channel = opened.channel;
    const proxy = createRpcProxy(registry, {
      send: (request, transfer) => channel.send(requestFrame(request), transfer),
      isDead: () => channel.closed,
      deadReason: () => new Error(`Actor "${name}" channel closed`),
      transport,
      transferArgs: options.transferArgs,
    });
    rpcProxies.push(proxy);
    channel.onClose(proxy.rejectAll);
    channel.onMessage((message) => {
      const frame = frameRecord(message);
      if (frame.type === "response") {
        if (!isResponse(frame)) {
          throw new ActorTransportError("protocol", "Invalid Actor node response");
        }
        proxy.deliver(frame);
      }
    });
    surface[name] = new Proxy({} as object, {
      get(_t, prop) {
        if (prop === "then") return undefined;
        if (typeof prop === "string") {
          return (...args: unknown[]) => {
            const p = proxy.call(prop, args);
            attachLazyIterator(p);
            return p;
          };
        }
        return undefined;
      },
    });
  }

  const dispose = (): Promise<void> => {
    if (!dead) {
      try {
        transport.send({ type: "dispose" } satisfies Frame);
      } catch (error) {
        registry.trackTask(Promise.reject(error));
      } finally {
        finish(new ActorDiedError(), true);
      }
    }
    return completion;
  };

  return Object.assign(surface, {
    dispose,
    completion,
    transport,
    registryId,
    getRegistry: () => actorRegistry,
  }) as never;
}
