// Research: dedicated bidirectional channel on a PROCESS actor.
//
// serveProcess has no `onLink` (Web-Worker-only messageport mechanism), and
// spawnProcess does not expose the host transport. The channel therefore opens
// on the WORKER side (which has getActiveTransport()): the worker calls
// transport.openChannel() and hands the Mux token back to the host over RPC.
// The host then connects the token. Both directions carry custom frames.
import { serveProcess } from "@ghostflyby/worker-actor";
import { getActiveTransport } from "@ghostflyby/worker-actor/codec";

export const rpc = {
  /** Opens a fresh logical channel on the process transport; returns the Mux token for the host. */
  openDedicatedChannel(): { __mux: "open"; ch: number } {
    const transport = getActiveTransport();
    if (transport === undefined) throw new Error("no active transport");
    const { channel, token } = transport.openChannel();
    // The Mux handshake: the token must be sent on the main channel so the
    // peer's Mux opens its end and acks. The host receives it via RPC.
    transport.send(token);
    channel.onMessage((message) => {
      if (message === "__probe") channel.send("__probe-ack");
      else if (message === "__close") channel.close();
    });
    return token as { __mux: "open"; ch: number };
  },
};

serveProcess(rpc);
