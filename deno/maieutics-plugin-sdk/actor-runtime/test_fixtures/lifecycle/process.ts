import { serveProcess } from "../../worker_runtime.ts";
import { throwingCleanupCodec } from "./codec.ts";

export const rpc = { pid: () => Deno.pid };
serveProcess(rpc, { codecs: [throwingCleanupCodec] });
