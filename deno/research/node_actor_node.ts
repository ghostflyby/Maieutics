// Research: multi-actor node (serveNode). One process serves two named actors
// over the fork IPC transport; the host connects each and can also open
// dedicated channels via the node transport (openChannel + token handoff).
import { serveNode } from "@ghostflyby/worker-actor";
import { getActiveTransport } from "@ghostflyby/worker-actor/codec";

export const actors = {
  repl: {
    initialize(): string {
      return "repl initialized";
    },
    execute(code: string): { ok: boolean; value: string } {
      return { ok: true, value: `eval: ${code}` };
    },
    /** Opens a dedicated channel on the node transport; returns the Mux token. */
    openDedicatedChannel(): { __mux: "open"; ch: number } {
      const transport = getActiveTransport();
      if (transport === undefined) throw new Error("no active transport");
      const { channel, token } = transport.openChannel();
      transport.send(token);
      channel.onMessage((message) => {
        if (message === "__probe") channel.send("__probe-ack");
        else if (message === "__close") channel.close();
      });
      return token as { __mux: "open"; ch: number };
    },
  },
  broker: {
    ping(): string {
      return "pong";
    },
  },
};

serveNode(actors);
