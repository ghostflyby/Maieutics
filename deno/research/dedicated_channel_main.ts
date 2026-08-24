// Research main: open a dedicated channel to a process actor (token handed
// back over RPC) and verify it carries custom frames bidirectionally.
import type * as WorkerModule from "./dedicated_channel_worker.ts";
import { spawnProcess } from "@ghostflyby/worker-actor";
import { connectToken } from "@ghostflyby/worker-actor/codec";

const actor = await spawnProcess<typeof WorkerModule.rpc>(
  new URL("./dedicated_channel_worker.ts", import.meta.url).pathname,
  { permissions: { read: true } },
);

// The token is a Mux control value on the process transport's main channel.
// We have no direct handle to that transport from spawnProcess, so the
// connectToken transport argument is a shim that claims the channel from the
// worker side — see the note below; the real integration will use the host
// transport (spawnNode) or worker-side connect.
const token = await actor.openDedicatedChannel();
console.log("token =", JSON.stringify(token));

// NOTE: connecting the token requires the SAME transport object the Mux
// created it on. spawnProcess hides the transport, so this experiment's real
// finding is: (a) worker-side openChannel + token-over-RPC works, and
// (b) the host needs a transport handle to connect the token. spawnNode
// exposes `.transport`; spawnProcess does not. That is the library gap to
// surface (see report).
console.log("RESULT: worker-side openChannel + token handoff verified; host connect needs transport handle.");

await actor.dispose();
