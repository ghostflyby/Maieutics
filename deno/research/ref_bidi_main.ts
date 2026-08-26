// Research main: bidirectional capability sharing between a "REPL" process and
// a "host" process via remoteRef (worker-actor 0.4.0, JSR).
import type * as ReplModule from "./ref_bidi_ref.ts";
import { spawnProcess } from "@ghostflyby/worker-actor";
import { remoteRef, remoteRefCodec } from "./ref_codec.ts";

// The "host" process's own surface.
const hostSurface = {
  ping(): string {
    return "host-pong";
  },
  tool(name: string): string {
    return `host tool: ${name}`;
  },
};

const repl = await spawnProcess<typeof ReplModule.rpc>(
  new URL("./ref_bidi_ref.ts", import.meta.url).pathname,
  { codecs: [remoteRefCodec], permissions: { read: true } },
);

try {
  // Direction 1: host → repl. Host hands its surface ref to REPL; REPL calls it.
  await repl.holdHostRef(remoteRef(hostSurface) as never);
  console.log("host→repl (repl calls host) =", await repl.callHeldHostRef());

  // Direction 2: repl → host. Host acquires REPL's surface ref and calls it.
  const replSurface = await repl.exposeReplSurface() as unknown as {
    execute(code: string): Promise<string>;
    status(): Promise<string>;
  };
  console.log(
    "repl→host (host calls repl) =",
    await replSurface.execute("1+1"),
  );
  console.log("repl→host status            =", await replSurface.status());
} finally {
  await repl.dispose();
}
console.log("ref_bidi OK");
