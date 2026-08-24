/**
 * REPL process environment parsing shared by the two REPL process entries.
 *
 * The kernel-derived entry (`main.ts`) and the host-derived entry
 * (`process_main.ts`) both boot the same WebSocket REPL client
 * (`repl_client.ts`), so they share the env contract and the Windows
 * credential bootstrap. The kernel writes the complete child environment
 * (MAIEUTICS_REPL_IPC / SESSION / GENERATION / CLIENT and, on Windows,
 * MAIEUTICS_REPL_PIPE); the host-derived child receives the same kernel env
 * through the `host.repl.derive` payload (B5a), so one parser covers both
 * paths.
 */

import { bootstrapWindowsCredential } from "../maieutics-repl-client/windows_bootstrap.ts";

export const IPC_ENV = "MAIEUTICS_REPL_IPC";
export const SESSION_ENV = "MAIEUTICS_REPL_SESSION";
export const GENERATION_ENV = "MAIEUTICS_REPL_GENERATION";
export const CLIENT_ENV = "MAIEUTICS_REPL_CLIENT";
export const CREDENTIAL_ENV = "MAIEUTICS_REPL_CREDENTIAL";
export const PIPE_ENV = "MAIEUTICS_REPL_PIPE";

/** The REPL client options a process entry needs to boot the WebSocket client. */
export interface ReplProcessEnvOptions {
  address: string;
  sessionId: string;
  generation: number;
  credential?: string;
}

/** Parses the shared REPL process environment and bootstraps the Windows
 * credential when the kernel did not pre-inject one. Throws when a required
 * variable is missing or the generation is not a non-negative integer. */
export function readReplProcessEnvironment(): ReplProcessEnvOptions {
  const address = requireEnvironment(IPC_ENV);
  const sessionId = requireEnvironment(SESSION_ENV);
  const rawGeneration = requireEnvironment(GENERATION_ENV);
  requireEnvironment(CLIENT_ENV);
  const generation = Number(rawGeneration);
  if (!Number.isSafeInteger(generation) || generation < 0) {
    throw new Error(`${GENERATION_ENV} must be a non-negative integer.`);
  }
  let credential: string | undefined;
  if (Deno.build.os === "windows") {
    credential = Deno.env.get(CREDENTIAL_ENV);
    if (credential === undefined) {
      const pipeName = requireEnvironment(PIPE_ENV);
      const bootstrap = bootstrapWindowsCredential(pipeName);
      if (bootstrap.sessionId !== sessionId) {
        throw new Error("The Windows bootstrap credential belongs to another REPL session.");
      }
      credential = bootstrap.credential;
    }
  }
  return {
    address,
    sessionId,
    generation,
    ...(credential === undefined ? {} : { credential }),
  };
}

function requireEnvironment(name: string): string {
  const value = Deno.env.get(name);
  if (value === undefined || value.length === 0) {
    throw new Error(`Missing ${name} environment variable.`);
  }
  return value;
}
