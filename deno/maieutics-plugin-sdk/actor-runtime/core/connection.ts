/** An explicitly owned, bidirectional Actor connection; no worker-global registry is used. */
import type { Remote } from "../spawn.ts";
import { type Codec, PayloadCodecRegistry } from "./codec.ts";
import { connectActor } from "./connect.ts";
import { type Channel, connectChannel, connectToken } from "./channel.ts";
import { iterableCodec } from "./codecs/iterable.ts";
import { errorCodec } from "./codecs/error.ts";
import { abortSignalCodec } from "./codecs/abort_signal.ts";
import { callbackCodec } from "./codecs/callback.ts";
import { attachLazyIterator } from "./lazy_iterator.ts";
import { ActorTransportError, positiveLimit } from "./lifetime.ts";
import { PROTOCOL_VERSION } from "./protocol.ts";
import {
  createRpcProxy,
  makeRpcHandler,
  requestFrame,
  type RpcApi,
  type RpcRequest,
  type RpcResponse,
  sendRpcResult,
} from "./rpc.ts";
import type { Transport } from "./transport.ts";

export interface ActorConnectionOptions {
  /** Only these named surfaces are callable by the peer. Authentication belongs to the transport's host. */
  actors?: Record<string, RpcApi>;
  codecs?: readonly Codec<unknown>[];
  signal?: AbortSignal;
  handshakeTimeoutMs?: number;
  maxPendingCalls?: number;
}
export interface ActorConnection<T extends { [K in keyof T]: RpcApi }> {
  readonly ready: Promise<void>;
  /** Resolves once the connection becomes terminal, carrying the one terminal cause. */
  readonly closed: Promise<unknown>;
  /** Joins owned RPC dispatches after closure; API implementations must cooperate with cancellation. */
  readonly completion: Promise<void>;
  readonly signal: AbortSignal;
  actor<K extends keyof T & string>(name: K): Remote<T[K]> & Disposable;
  close(reason?: unknown): void;
}
function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object";
}
function isRequest(value: unknown): value is RpcRequest {
  return record(value) && Number.isSafeInteger(value.id) && Number(value.id) > 0 &&
    typeof value.method === "string" && value.method.length <= 256 && Array.isArray(value.args);
}
function isResponse(value: unknown): value is RpcResponse {
  return record(value) && Number.isSafeInteger(value.id) && Number(value.id) > 0 &&
    (value.ok === true || value.ok === false && record(value.error) &&
        typeof value.error.name === "string" && typeof value.error.message === "string");
}

/** Connect symmetric peers over an already established, host-authorized transport. */
export function createActorConnection<T extends { [K in keyof T]: RpcApi }>(
  transport: Transport,
  options: ActorConnectionOptions = {},
): ActorConnection<T> {
  const registry = new PayloadCodecRegistry();
  for (const codec of options.codecs ?? []) registry.register(codec);
  for (const codec of [iterableCodec, errorCodec, abortSignalCodec, callbackCodec]) {
    if (!registry.has(codec.tag)) registry.register(codec);
  }
  const maxPending = positiveLimit(options.maxPendingCalls, 1024);
  const budget = positiveLimit(options.handshakeTimeoutMs, 10_000);
  const ownedActors = new Map(Object.entries(options.actors ?? {}));
  if (
    ownedActors.size > 256 ||
    [...ownedActors.keys()].some((name) => !name.length || name.length > 256)
  ) {
    throw new RangeError("Actor advertisement budget or name length exceeded");
  }
  const remoteActors = new Set<string>();
  const actorRefs = new Map<string, Remote<object> & Disposable>();
  const channels = new Set<Channel>();
  const dispatches = new Set<Promise<void>>();
  const controller = new AbortController();
  const readiness = Promise.withResolvers<void>();
  const terminal = Promise.withResolvers<unknown>();
  // Callers still observe this rejection through ready; startup failure is also owned here.
  void readiness.promise.catch(() => {});
  let ready = false;
  let closed = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let stopObserving = () => {};
  const onAbort = () => close(options.signal?.reason);
  const close = (
    reason: unknown = new ActorTransportError("closed", "Actor connection closed"),
  ): void => {
    if (closed) return;
    closed = true;
    clearTimeout(timer);
    stopObserving();
    options.signal?.removeEventListener("abort", onAbort);
    controller.abort(reason);
    readiness.reject(reason);
    const errors: unknown[] = [];
    try {
      registry.failAll();
    } catch (error) {
      errors.push(error);
    }
    for (const channel of [...channels]) {
      try {
        channel.close(reason);
      } catch (error) {
        errors.push(error);
      }
    }
    channels.clear();
    actorRefs.clear();
    try {
      transport.close(reason);
    } catch (error) {
      errors.push(error);
    }
    terminal.resolve(
      errors.length
        ? new AggregateError([reason, ...errors], "Actor connection cleanup failed")
        : reason,
    );
  };
  const track = (channel: Channel): void => {
    channels.add(channel);
    channel.onClose(() => channels.delete(channel));
  };
  const observe = (task: Promise<void>): void => {
    const observed = task.catch(close).finally(() => dispatches.delete(observed));
    dispatches.add(observed);
  };
  const serve = (name: string, channel: Channel): void => {
    const api = ownedActors.get(name);
    if (!api) {
      channel.close(new ActorTransportError("protocol", "Actor surface is not available"));
      return;
    }
    track(channel);
    const handler = makeRpcHandler(api, registry, transport);
    channel.onMessage((frame) => {
      if (!record(frame)) {
        close(new ActorTransportError("protocol", "Invalid Actor request frame"));
        return;
      }
      if (frame.type === "dispose") {
        channel.close();
        return;
      }
      if (frame.type !== "request") return; // future control frames are tolerated
      if (!isRequest(frame) || dispatches.size >= maxPending) {
        close(
          new ActorTransportError("protocol", "Invalid Actor request or dispatch budget exceeded"),
        );
        return;
      }
      observe(
        handler(frame).then((result) => {
          if (!closed && !channel.closed) {
            sendRpcResult(result, (value, transfer) => channel.send(value, transfer));
          }
        }),
      );
    });
  };
  stopObserving = transport.onClose(close);
  transport.onMessage(({ data }) => {
    if (closed) return;
    try {
      if (!record(data)) {
        throw new ActorTransportError("protocol", "Invalid Actor connection frame");
      }
      if (data.type === "actor:hello") {
        if (
          ready || data.version !== PROTOCOL_VERSION || data.kind !== transport.kind ||
          !Array.isArray(data.codecs) || data.codecs.length !== registry.tags.length ||
          !data.codecs.every((tag, i) => tag === registry.tags[i]) ||
          !Array.isArray(data.actors) || data.actors.length > 256 ||
          !data.actors.every((name) =>
            typeof name === "string" && name.length > 0 && name.length <= 256
          )
        ) {
          throw new ActorTransportError(
            "protocol",
            "Actor version, codec or surface handshake mismatch",
          );
        }
        for (const name of data.actors) remoteActors.add(name);
        ready = true;
        clearTimeout(timer);
        readiness.resolve();
        return;
      }
      if (data.type === "dispose") {
        close(new ActorTransportError("closed", "Actor peer disposed its connection"));
        return;
      }
      if (data.type === "__open-actor") {
        if (!ready || typeof data.name !== "string") {
          throw new ActorTransportError("protocol", "Actor opened before handshake");
        }
        const channel = data.port instanceof MessagePort ? connectChannel(data.port) : connectToken(
          transport,
          data.token as { __mux: "open"; ch: number },
        );
        serve(data.name, channel);
      }
    } catch (error) {
      close(error);
    }
  });
  if (options.signal?.aborted) close(options.signal.reason);
  else if (!closed) {
    options.signal?.addEventListener("abort", onAbort, { once: true });
    if (!ready) {
      timer = setTimeout(
        () => close(new ActorTransportError("protocol", "Actor handshake timed out")),
        budget,
      );
    }
    try {
      transport.send({
        type: "actor:hello",
        version: PROTOCOL_VERSION,
        kind: transport.kind,
        codecs: registry.tags,
        actors: [...ownedActors.keys()],
      });
    } catch (error) {
      close(error);
    }
  }
  const completion = terminal.promise.then(async () => {
    await Promise.all([...dispatches]);
    await registry.join();
    await transport.completion;
  });
  // The owner can await completion after handling closed without an interim unhandled rejection.
  void completion.catch(() => {});
  return {
    ready: readiness.promise,
    closed: terminal.promise,
    completion,
    signal: controller.signal,
    actor(name) {
      if (closed) throw controller.signal.reason;
      if (!ready || !remoteActors.has(name)) {
        throw new ActorTransportError("protocol", "Actor surface was not advertised by the peer");
      }
      const cached = actorRefs.get(name);
      if (cached) return cached as Remote<T[typeof name]> & Disposable;
      const { channel } = connectActor(transport, name);
      track(channel);
      const proxy = createRpcProxy(registry, {
        transport,
        maxPendingCalls: maxPending,
        send: (request, transfer) => channel.send(requestFrame(request), transfer),
        isDead: () => closed || channel.closed,
        deadReason: () => new ActorTransportError("closed", "Actor reference is closed"),
      });
      channel.onClose((reason) => {
        proxy.rejectAll(reason);
        actorRefs.delete(name);
      });
      channel.onMessage((frame) => {
        if (!record(frame) || frame.type !== "response") return;
        try {
          if (!isResponse(frame)) {
            throw new ActorTransportError("protocol", "Invalid Actor response frame");
          }
          proxy.deliver(frame);
        } catch (error) {
          close(error);
        }
      });
      const reference = new Proxy({} as Remote<T[typeof name]> & Disposable, {
        get(_target, prop) {
          if (prop === "then") return undefined;
          if (prop === Symbol.dispose) {
            return () => {
              channel.send({ type: "dispose" });
              channel.close();
            };
          }
          if (typeof prop === "string") {
            return (...args: unknown[]) => {
              const result = proxy.call(prop, args);
              attachLazyIterator(result);
              return result;
            };
          }
          return undefined;
        },
      });
      actorRefs.set(name, reference as Remote<object> & Disposable);
      return reference;
    },
    close(reason) {
      if (!closed && !transport.closed) {
        try {
          transport.send({ type: "dispose" });
        } catch (error) {
          close(error);
        }
      }
      close(reason);
    },
  };
}
