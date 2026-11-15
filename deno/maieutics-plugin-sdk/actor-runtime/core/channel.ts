/**
 * High-level channel abstraction for codec authors.
 *
 * A Codec may need a dedicated cross-thread channel with its own wire protocol —
 * not just a cloneable value. Examples: streaming elements (iterable codec),
 * abort propagation (abort-signal codec), or a custom marshal-by-ref protocol
 * (see examples/remote_ref). This module is the high-level counterpart to raw
 * MessageChannel handling: it owns channel creation, port transfer, closure and
 * GC-based release, so a codec only supplies its frames.
 *
 * The library deliberately provides no automatic protocol on a channel: a codec
 * gets the channel and defines its own frames. What the library guarantees:
 *   - openChannel(): create a MessageChannel and automatically add the peer port
 *     to ctx.transfer (transferred with the placeholder).
 *   - connectChannel(): wrap a transferred port on the receiving side.
 *   - Both ends register with the registry (ctx.registry.registerChannel) so
 *     failAll() closes every open channel when the actor dies.
 *   - registerRelease(): FinalizationRegistry wrapper for GC-based release —
 *     the codec decides what releasing means (e.g. send a "release" frame and
 *     close), and unregistering on explicit close keeps release single.
 */

import type { EncodeContext } from "./codec.ts";
import type { Transport } from "./transport.ts";
import { ActorTransportError, createLifetime, type Lifetime } from "./lifetime.ts";
import { protocolFailure } from "./validation.ts";

export interface ChannelOptions {
  /** Called once when this endpoint closes: by close(), peer closure, or an error. */
  onClosed?: () => void;
  /** Called when a message fails to deserialize; the channel then closes. */
  onMessageError?: () => void;
}

export interface Channel extends Lifetime {
  readonly port: MessagePort;
  readonly kind?: "messageport" | "framed";
  send(message: unknown, transfer?: Transferable[]): void;
  onMessage(handler: (message: unknown) => void): void;
}

/**
 * The result of openChannel(): the local Channel plus how the peer end is
 * handed over. On a messageport context this is a transferable MessagePort
 * (`peerPort`); on a framed context `peerPort` is undefined and `token` is a
 * Mux value the peer uses to rebuild the channel. Existing codecs read
 * `peerPort` (messageport-only); transport-aware codecs branch on it.
 */
export interface ChannelPeer {
  channel: Channel;
  /** Transferable peer port (messageport transports); undefined on framed. */
  peerPort?: MessagePort;
  /** Mux token (framed transports); undefined on messageport. */
  token?: unknown;
}

/**
 * Open a new logical channel for a value crossing the boundary. On a
 * messageport context this creates a MessageChannel and transfers port2 with
 * the placeholder (current behavior). On a framed context it delegates to the
 * transport's openChannel() and returns the Mux token instead — the peer
 * rebuilds the channel from the token via onChannel.
 */
export function openChannel(
  ctx: EncodeContext,
  options?: ChannelOptions,
): ChannelPeer {
  const transport = ctx.transport;
  // Mux transports (framed byte streams and message channels) establish
  // logical channels via transport.openChannel(): the token is sent as a Mux
  // control frame on the main channel (the peer's onChannel fires and acks),
  // AND rides in the placeholder so the peer's decode can connect the token
  // to the inbound channel.
  if (transport.kind === "framed" || transport.kind === "message") {
    const opened = transport.openChannel(options);
    transport.send(opened.token);
    return { channel: opened.channel, token: opened.token };
  }
  const { port1, port2 } = new MessageChannel();
  ctx.transfer.push(port2);
  return { channel: wrapPort(port1, options), peerPort: port2 };
}

/** Wrap a transferred peer port as a Channel on the receiving side. */
export function connectChannel(
  port: MessagePort,
  options?: ChannelOptions,
): Channel {
  return wrapPort(port, options);
}

/** Wrap a local/transferred MessagePort as a Channel (exported for transport adapters). */
export function wrapPort(port: MessagePort, options: ChannelOptions = {}): Channel {
  const life = createLifetime();
  let handler: ((message: unknown) => void) | undefined;
  life.onClose(() => {
    port.onmessage = null;
    port.onmessageerror = null;
    port.close();
  });
  if (options.onClosed) life.onClose(options.onClosed);
  port.onmessage = (ev: MessageEvent<unknown>) => {
    if (life.closed) return;
    try {
      handler?.(ev.data);
    } catch (error) {
      life.close(protocolFailure(error));
    }
  };
  port.onmessageerror = () => {
    try {
      options.onMessageError?.();
    } finally {
      life.close(new ActorTransportError("decode", "Actor channel decoding failed"));
    }
  };
  port.start?.();
  return {
    get closed() {
      return life.closed;
    },
    get reason() {
      return life.reason;
    },
    get port() {
      return port;
    },
    kind: "messageport",
    onClose: life.onClose,
    close: life.close,
    send(message, transfer) {
      if (life.closed) return;
      try {
        port.postMessage(message, transfer ? { transfer } : undefined);
      } catch (cause) {
        const error = new ActorTransportError("send", "Actor channel send failed", { cause });
        life.close(error);
        throw error;
      }
    },
    onMessage(h) {
      handler = h;
    },
  };
}

type TokenTransport = Pick<
  Transport,
  "onChannel" | "claimOrphan" | "onClose" | "close" | "closed" | "reason"
>;
interface PendingToken {
  channel?: Channel;
  bind(channel: Channel): void;
  proxy: Channel;
}
const dispatchers = new WeakMap<TokenTransport, Map<number, PendingToken>>();

/** Resolve a channel token without dropping sends or closure before the peer arrives. */
export function connectToken(
  transport: TokenTransport,
  token: { __mux: "open"; ch: number },
): Channel {
  if (!token || token.__mux !== "open" || !Number.isSafeInteger(token.ch) || token.ch < 0) {
    throw new ActorTransportError("protocol", "Invalid Actor channel token");
  }
  let pending = dispatchers.get(transport);
  if (!pending) {
    pending = new Map();
    dispatchers.set(transport, pending);
    const entries = pending;
    transport.onChannel((channel) => {
      const id = (channel as Channel & { _ch?: number })._ch;
      if (id !== undefined) {
        const entry = entries.get(id);
        if (entry) {
          entries.delete(id);
          entry.bind(channel);
        }
      }
    });
    transport.onClose((reason) => {
      for (const entry of entries.values()) entry.proxy.close(reason);
      entries.clear();
    });
  }
  const orphan = transport.claimOrphan?.(token.ch);
  if (orphan) return orphan;
  const existing = pending.get(token.ch);
  if (existing) return existing.proxy;
  if (pending.size >= 1024) {
    throw new ActorTransportError("capacity", "Actor token wait budget exceeded");
  }
  const life = createLifetime();
  const messages: Array<{ value: unknown; transfer?: Transferable[] }> = [];
  let inbound: ((value: unknown) => void) | undefined;
  let real: Channel | undefined;
  const entries = pending;
  life.onClose((reason) => {
    entries.delete(token.ch);
    messages.length = 0;
    real?.close(reason);
  });
  const proxy: Channel = {
    get closed() {
      return life.closed;
    },
    get reason() {
      return life.reason;
    },
    get port(): MessagePort {
      throw new Error("Mux channels have no MessagePort");
    },
    kind: "framed",
    onClose: life.onClose,
    close: life.close,
    send(value, transfer) {
      if (life.closed) return;
      if (real) real.send(value, transfer);
      else {
        if (messages.length >= 128) {
          const error = new ActorTransportError(
            "capacity",
            "Actor pending channel mailbox exceeded",
          );
          transport.close(error);
          throw error;
        }
        messages.push({ value, transfer });
      }
    },
    onMessage(h) {
      inbound = h;
      real?.onMessage(h);
    },
  };
  const entry: PendingToken = {
    proxy,
    bind(channel) {
      if (life.closed) {
        channel.close(life.reason);
        return;
      }
      real = channel;
      channel.onClose(life.close);
      if (inbound) channel.onMessage(inbound);
      for (const message of messages.splice(0)) {
        if (!life.closed) channel.send(message.value, message.transfer);
      }
    },
  };
  pending.set(token.ch, entry);
  if (transport.closed) life.close(transport.reason);
  return proxy;
}

/** GC-based release: see the module docs. Returns an unregister function (call on explicit close). */
export function registerRelease(
  target: object,
  onReleased: () => void,
): () => void {
  const token = {};
  releaseRegistry.register(target, onReleased, token);
  return () => releaseRegistry.unregister(token);
}

/**
 * Finalizers are best-effort by spec: timing is uncontrollable and engines may
 * skip them. Explicit release paths (done/error/return/dispose/failAll) are the
 * deterministic ones; this registry is a safety net. The held callback captures
 * only onReleased — never the target — so registration can't keep it alive.
 */
const releaseRegistry = new FinalizationRegistry<() => void>((fn) => fn());
