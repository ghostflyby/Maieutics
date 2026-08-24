// Research: bidirectional actor capability sharing across processes, using
// the library's remote-ref reference codec (same pattern as examples/remote_ref
// and the Maieutics plugin SDK's actor_ref).
//
// Model: a "REPL" process and a "host" process. Each owns a surface the other
// can acquire:
//   - REPL exports a surface (its execute capability);
//   - HOST exports a surface (its broker/tool capability);
//   - HOST hands its ref to REPL (REPL acquires it and calls it);
//   - REPL hands its ref to HOST (HOST acquires it and calls it).
// Both directions use the same remoteRef codec on both sides.
import { serveProcess } from "@ghostflyby/worker-actor";
import {
  remoteRef,
  remoteRefCodec,
  type RemoteRef,
} from "./ref_codec.ts";

const replSurface = {
  execute(code: string): string {
    return `repl ran: ${code}`;
  },
  status(): string {
    return "repl-ok";
  },
};

export const rpc = {
  // REFPL-OWNED capability: hand a ref to this process's surface.
  exposeReplSurface(): RemoteRef<typeof replSurface> {
    return remoteRef(replSurface);
  },
  // HOST-OWNED capability (arrives from host): hold + call.
  holdHostRef(ref: RemoteRef<{ ping(): string }>): string {
    heldHost = ref;
    return "held";
  },
  callHeldHostRef(): Promise<string> {
    return (heldHost as { ping(): Promise<string> }).ping();
  },
};

let heldHost: unknown;

serveProcess(rpc, { codecs: [remoteRefCodec] });
