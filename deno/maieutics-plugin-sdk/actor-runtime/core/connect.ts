/**
 * Cross-connection actor addressing: target a specific actor on a Transport,
 * without a spawn() call. This is the counterpart to serveNode's
 * `__open-actor` protocol frame — the peer names which served api the channel
 * should run, and the node serves that api over the channel.
 *
 *   connectActor(transport)      — single-actor transports (serveWorker /
 *                                  serveProcess): the actor IS the main
 *                                  channel, so the returned Channel rides the
 *                                  transport itself and NO sub-channel is
 *                                  opened. It sees every main-channel frame
 *                                  (handshake, control frames included) —
 *                                  filter for the RPC envelopes you care
 *                                  about; close() closes the whole transport;
 *                                  and it takes over the transport's inbound
 *                                  handler, so use it on a transport you are
 *                                  not already driving.
 *   connectActor(transport, name) — a node (serveNode): opens a fresh
 *                                  sub-channel (Mux token or transferred
 *                                  port) and names the served api; the node
 *                                  serves that api over it.
 *
 * The channel carries the same RPC frames as spawn's main channel; run it
 * through createRpcProxy to call the actor, or makeRpcHandler to serve it.
 * Returns { channel, transport } so both the channel frames and the transport
 * (for Mux-aware codec values / sub-channel establishment) are available.
 */

import type { Channel } from "./channel.ts";
import type { Transport } from "./transport.ts";
import { createLifetime } from "./lifetime.ts";

export interface ConnectedActor {
  /** The logical channel opened on the transport. */
  channel: Channel;
  /** The transport the channel rides on (codec encode/decode needs it). */
  transport: Transport;
}

/**
 * Wrap a transport's main channel as a Channel — the single-actor address.
 * Frames flow on the transport itself: send goes to transport.send, inbound
 * transport messages feed the channel's handler (last handler wins).
 */
function mainChannel(transport: Transport): Channel {
  const life = createLifetime();
  transport.onClose(life.close);
  let closed = false;
  let handler: ((message: unknown) => void) | undefined;
  transport.onMessage((ev) => {
    if (!closed) handler?.(ev.data);
  });
  return {
    completion: life.completion,
    get closed(): boolean {
      return closed || life.closed;
    },
    get reason() {
      return life.reason;
    },
    onClose: life.onClose,
    get port(): MessagePort {
      throw new Error("The main channel has no MessagePort");
    },
    send(message, transfer) {
      if (closed) return;
      transport.send(message, transfer);
    },
    onMessage(h) {
      handler = h;
    },
    close() {
      if (closed) return;
      closed = true;
      life.close();
      transport.close();
    },
  };
}

/** Open an actor channel on a transport, naming the served api on a node. */
export function connectActor(
  transport: Transport,
  name?: string,
): ConnectedActor {
  if (name === undefined) {
    return { channel: mainChannel(transport), transport };
  }
  const opened = transport.openChannel();
  if (transport.kind === "messageport") {
    transport.send({ type: "__open-actor", name, port: opened.token }, [
      opened.token as MessagePort,
    ]);
    return { channel: opened.channel, transport };
  }
  // Mux handshake: the token is a control frame on the main channel — the
  // peer's Mux opens its end + acks (and the channel may carry data before the
  // __open-actor frame is processed, so send token first, name second).
  transport.send(opened.token);
  transport.send({ type: "__open-actor", name, token: opened.token });
  return { channel: opened.channel, transport };
}
