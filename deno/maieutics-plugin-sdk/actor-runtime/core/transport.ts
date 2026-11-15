/** Web-standard Actor transports. V8 byte framing lives in transport_v8.ts. */
import { type Channel, type ChannelOptions, wrapPort } from "./channel.ts";
import { protocolFailure } from "./validation.ts";
import {
  ActorTransportError,
  createLifetime,
  type Lifetime,
  positiveLimit,
  waitWithSignal,
} from "./lifetime.ts";

export type TransportKind = "messageport" | "framed" | "message";
export interface TransportMessage {
  data: unknown;
  ports: readonly MessagePort[];
}
export interface Transport extends Lifetime {
  readonly completion?: Promise<void>;
  readonly kind: TransportKind;
  send(frame: unknown, transfer?: Transferable[]): void;
  onMessage(handler: (ev: TransportMessage) => void): void;
  openChannel(options?: ChannelOptions): { channel: Channel; token: unknown };
  onChannel(handler: (channel: Channel) => void): void;
  claimOrphan?(ch: number): Channel | undefined;
}
export interface TransportOptions {
  onClosed?: (reason: unknown) => void;
  maxChannels?: number;
  maxBufferedMessages?: number;
}
export type MuxFrame =
  | { __mux: "open" | "close"; ch: number }
  | { __mux: "data"; ch: number; value: unknown };

/** The structured-clone surface shared by MessagePort, Worker and worker globals. */
export interface MessageEndpoint {
  onmessage: ((ev: MessageEvent<unknown>) => void) | null;
  onmessageerror: ((ev: MessageEvent<unknown>) => void) | null;
  postMessage(message: unknown, options?: { transfer?: Transferable[] }): void;
  close?(): void;
  start?(): void;
}

/** Native structured-clone transport; the owning host supplies peer-death detection. */
export function fromMessagePort(port: MessageEndpoint, options: TransportOptions = {}): Transport {
  const life = createLifetime();
  const channels = new Set<Channel>();
  const buffered: TransportMessage[] = [];
  const maxChannels = positiveLimit(options.maxChannels, 1024);
  const maxBuffered = positiveLimit(options.maxBufferedMessages, 128);
  let handler: ((ev: TransportMessage) => void) | undefined;
  let channelHandler: ((channel: Channel) => void) | undefined;
  const track = (channel: Channel): Channel => {
    if (channels.size >= maxChannels) {
      channel.close();
      const error = new ActorTransportError("capacity", "Actor channel budget exceeded");
      life.close(error);
      throw error;
    }
    channels.add(channel);
    channel.onClose(() => channels.delete(channel));
    return channel;
  };
  life.onClose((reason) => {
    port.onmessage = null;
    port.onmessageerror = null;
    const errors: unknown[] = [];
    for (
      const close of [
        () => port.close?.(),
        ...[...channels].map((channel) => () => channel.close(reason)),
        ...buffered.flatMap((frame) => frame.ports.map((peer) => () => peer.close())),
      ]
    ) {
      try {
        close();
      } catch (error) {
        errors.push(error);
      }
    }
    channels.clear();
    buffered.length = 0;
    if (errors.length) throw new AggregateError(errors, "Actor MessagePort cleanup failed");
  });
  if (options.onClosed) life.onClose(options.onClosed);
  port.onmessage = (ev: MessageEvent<unknown>) => {
    if (life.closed) return;
    try {
      if (ev.data == null && ev.ports.length === 1) {
        channelHandler?.(track(wrapPort(ev.ports[0])));
      } else if (handler) handler({ data: ev.data, ports: ev.ports });
      else {
        if (buffered.length >= maxBuffered) {
          throw new ActorTransportError("capacity", "Actor inbound mailbox exceeded");
        }
        buffered.push({ data: ev.data, ports: ev.ports });
      }
    } catch (error) {
      life.close(protocolFailure(error));
    }
  };
  port.onmessageerror = () =>
    life.close(new ActorTransportError("decode", "Actor MessagePort decoding failed"));
  port.start?.();
  return {
    kind: "messageport",
    get closed() {
      return life.closed;
    },
    get reason() {
      return life.reason;
    },
    onClose: life.onClose,
    close: life.close,
    send(frame, transfer) {
      if (life.closed) throw life.reason;
      try {
        port.postMessage(frame, transfer ? { transfer } : undefined);
      } catch (cause) {
        const error = new ActorTransportError("send", "Actor MessagePort send failed", { cause });
        life.close(error);
        throw error;
      }
    },
    onMessage(h) {
      handler = h;
      while (buffered.length && !life.closed) {
        const next = buffered.shift();
        if (next) {
          try {
            h(next);
          } catch (error) {
            life.close(protocolFailure(error));
          }
        }
      }
    },
    onChannel(h) {
      channelHandler = h;
    },
    openChannel(channelOptions) {
      if (life.closed) throw life.reason;
      const { port1, port2 } = new MessageChannel();
      return { channel: track(wrapPort(port1, channelOptions)), token: port2 };
    },
  };
}

interface MuxChannel extends Channel {
  readonly _ch: number;
  deliver(value: unknown): void;
}
export interface Mux extends Lifetime {
  readonly kind: "framed";
  onMessage(handler: (ev: TransportMessage) => void): void;
  onChannel(handler: (channel: Channel) => void): void;
  openChannel(options?: ChannelOptions): { channel: Channel; token: unknown };
  send(frame: unknown): void;
  deliver(frame: unknown): void;
  claimOrphan(ch: number): Channel | undefined;
}

/** Bounded logical channels shared by message-oriented and byte-framed transports. */
export function createMux(write: (frame: unknown) => void, options: TransportOptions = {}): Mux {
  const life = createLifetime();
  const channels = new Map<number, MuxChannel>();
  const pending = new Set<number>();
  const orphaned = new Set<number>();
  const buffered: TransportMessage[] = [];
  let bufferedCount = 0;
  const maxChannels = positiveLimit(options.maxChannels, 1024);
  const maxBuffered = positiveLimit(options.maxBufferedMessages, 128);
  let nextCh = Math.floor(Math.random() * 1_000_000_000) * 1000;
  let handler: ((ev: TransportMessage) => void) | undefined;
  let channelHandler: ((channel: Channel) => void) | undefined;
  const send = (frame: unknown): void => {
    if (life.closed) return;
    try {
      write(frame);
    } catch (cause) {
      const error = cause instanceof ActorTransportError
        ? cause
        : new ActorTransportError("send", "Actor transport send failed", { cause });
      life.close(error);
      throw error;
    }
  };
  const makeChannel = (ch: number, channelOptions?: ChannelOptions): MuxChannel => {
    if (channels.size >= maxChannels) {
      throw new ActorTransportError("capacity", "Actor channel budget exceeded");
    }
    const channelLife = createLifetime();
    const messages: unknown[] = [];
    let inbound: ((value: unknown) => void) | undefined;
    channelLife.onClose(() => {
      channels.delete(ch);
      pending.delete(ch);
      orphaned.delete(ch);
      bufferedCount -= messages.length;
      messages.length = 0;
      if (!life.closed) send({ __mux: "close", ch } satisfies MuxFrame);
    });
    if (channelOptions?.onClosed) channelLife.onClose(channelOptions.onClosed);
    const channel: MuxChannel = {
      _ch: ch,
      kind: "framed",
      get closed() {
        return channelLife.closed;
      },
      get reason() {
        return channelLife.reason;
      },
      get port(): MessagePort {
        throw new Error("Mux channels have no MessagePort");
      },
      onClose: channelLife.onClose,
      close: channelLife.close,
      send(value) {
        if (!channelLife.closed) send({ __mux: "data", ch, value } satisfies MuxFrame);
      },
      onMessage(h) {
        inbound = h;
        while (messages.length && !channelLife.closed) {
          bufferedCount--;
          try {
            h(messages.shift());
          } catch (error) {
            life.close(protocolFailure(error));
          }
        }
      },
      deliver(value) {
        if (channelLife.closed) return;
        if (inbound) inbound(value);
        else {
          if (bufferedCount >= maxBuffered) {
            throw new ActorTransportError("capacity", "Actor inbound mailbox exceeded");
          }
          messages.push(value);
          bufferedCount++;
        }
      },
    };
    channels.set(ch, channel);
    return channel;
  };
  life.onClose((reason) => {
    const errors: unknown[] = [];
    for (const channel of [...channels.values()]) {
      try {
        channel.close(reason);
      } catch (error) {
        errors.push(error);
      }
    }
    channels.clear();
    pending.clear();
    orphaned.clear();
    buffered.length = 0;
    bufferedCount = 0;
    if (errors.length) throw new AggregateError(errors, "Actor Mux cleanup failed");
  });
  if (options.onClosed) life.onClose(options.onClosed);
  const deliver = (frame: unknown): void => {
    if (life.closed) return;
    try {
      if (typeof frame === "object" && frame !== null && "__mux" in frame) {
        const f = frame as MuxFrame;
        if (!Number.isSafeInteger(f.ch) || f.ch < 0) {
          throw new ActorTransportError("protocol", "Invalid Actor channel identity");
        }
        if (f.__mux === "open") {
          if (pending.delete(f.ch)) return;
          if (channels.has(f.ch)) {
            throw new ActorTransportError("protocol", "Duplicate Actor channel identity");
          }
          const channel = makeChannel(f.ch);
          orphaned.add(f.ch);
          send({ __mux: "open", ch: f.ch } satisfies MuxFrame);
          channelHandler?.(channel);
          return;
        }
        if (f.__mux === "close") {
          channels.get(f.ch)?.close();
          return;
        }
        if (f.__mux === "data") {
          // The peer may reply before its open acknowledgement is delivered.
          channels.get(f.ch)?.deliver(f.value);
          return;
        }
        throw new ActorTransportError("protocol", "Unknown Actor channel control");
      }
      if (handler) handler({ data: frame, ports: [] });
      else {
        if (bufferedCount >= maxBuffered) {
          throw new ActorTransportError("capacity", "Actor inbound mailbox exceeded");
        }
        buffered.push({ data: frame, ports: [] });
        bufferedCount++;
      }
    } catch (error) {
      life.close(protocolFailure(error));
    }
  };
  return {
    kind: "framed",
    get closed() {
      return life.closed;
    },
    get reason() {
      return life.reason;
    },
    onClose: life.onClose,
    close: life.close,
    send,
    deliver,
    onMessage(h) {
      handler = h;
      while (buffered.length && !life.closed) {
        const next = buffered.shift();
        if (next) {
          bufferedCount--;
          try {
            h(next);
          } catch (error) {
            life.close(protocolFailure(error));
          }
        }
      }
    },
    onChannel(h) {
      channelHandler = h;
    },
    openChannel(channelOptions) {
      if (life.closed) throw life.reason;
      try {
        const ch = nextCh++;
        const channel = makeChannel(ch, channelOptions);
        pending.add(ch);
        return { channel, token: { __mux: "open", ch } satisfies MuxFrame };
      } catch (error) {
        life.close(error);
        throw error;
      }
    },
    claimOrphan(ch) {
      if (!orphaned.delete(ch)) return undefined;
      return channels.get(ch);
    },
  };
}

export interface MessageTransport extends Transport {
  readonly completion: Promise<void>;
  deliver(message: unknown): void;
}
export interface MessageTransportOptions extends TransportOptions {
  send: (message: unknown) => void;
  decode?: (message: unknown, signal: AbortSignal) => unknown | Promise<unknown>;
  maxPendingMessages?: number;
  maxPendingBytes?: number;
  messageSize?: (message: unknown) => number;
}

/** Ordered asynchronous decoding with a bounded admission queue and fatal decode errors. */
export function messageTransport(options: MessageTransportOptions): MessageTransport {
  const mux = createMux(options.send, options);
  const maxPending = positiveLimit(options.maxPendingMessages, 128);
  const maxBytes = positiveLimit(options.maxPendingBytes, 4 * 1024 * 1024);
  const queue: Array<{ value: unknown; size: number }> = [];
  let pendingBytes = 0;
  let processing = false;
  let task = Promise.resolve();
  const controller = new AbortController();
  const terminal = Promise.withResolvers<void>();
  mux.onClose((reason) => {
    queue.length = 0;
    pendingBytes = 0;
    controller.abort(reason);
    terminal.resolve();
  });
  const drain = async (): Promise<void> => {
    try {
      while (queue.length && !mux.closed) {
        const message = queue.shift();
        if (!message) continue;
        try {
          const decoded = options.decode
            ? await waitWithSignal(
              options.decode(message.value, controller.signal),
              controller.signal,
            )
            : message.value;
          if (!mux.closed) mux.deliver(decoded);
        } finally {
          pendingBytes = Math.max(0, pendingBytes - message.size);
        }
      }
    } catch (error) {
      mux.close(
        new ActorTransportError("decode", "Actor message decoding failed", { cause: error }),
      );
    } finally {
      processing = false;
    }
  };
  return {
    kind: "message",
    completion: terminal.promise.then(() => task),
    get closed() {
      return mux.closed;
    },
    get reason() {
      return mux.reason;
    },
    onClose: mux.onClose,
    close: mux.close,
    send: mux.send,
    onMessage: mux.onMessage,
    openChannel: mux.openChannel,
    onChannel: mux.onChannel,
    claimOrphan: mux.claimOrphan,
    deliver(message) {
      if (mux.closed) return;
      const size = options.messageSize?.(message) ?? 0;
      if (
        !Number.isSafeInteger(size) || size < 0 || pendingBytes + size > maxBytes ||
        queue.length + Number(processing) >= maxPending
      ) {
        mux.close(new ActorTransportError("capacity", "Actor decoding queue exceeded"));
        return;
      }
      pendingBytes += size;
      queue.push({ value: message, size });
      if (!processing) {
        processing = true;
        task = drain();
        void task.catch((error) => {
          if (!mux.closed) mux.close(error);
        });
      }
    },
  };
}

/** Node fork IPC preserves values natively; event and process ownership remain with the caller. */
export function fromNodeIpc(
  send: (message: unknown, handle?: unknown) => boolean,
  options: TransportOptions = {},
): MessageTransport {
  return messageTransport({
    ...options,
    send(message) {
      if (!send(message)) {
        throw new ActorTransportError("send", "Actor IPC send failed or exceeded its queue budget");
      }
    },
  });
}
