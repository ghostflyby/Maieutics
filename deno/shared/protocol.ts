/**
 * Control channel wire contract shared by the REPL client and the plugin host.
 * The kernel's `ReplControlJsonContext` mirrors this shape; keep both sides in
 * lockstep.
 */

/** Current envelope version; the kernel rejects other versions at the handshake. */
export const ENVELOPE_VERSION = 1;

/** Versioned message envelope shared by every control channel bus message. */
export interface ReplEnvelope {
  version: number;
  type: string;
  correlationId?: string;
  payload?: unknown;
  buffers?: string[];
}

/** HttpClient created by `Deno.createHttpClient` for unix socket proxying. */
export type HttpClient = ReturnType<typeof Deno.createHttpClient>;
