import { ReplClient } from "./repl_client.ts";
import { bootstrapWindowsCredential } from "../maieutics-repl-client/windows_bootstrap.ts";

const IPC_ENV = "MAIEUTICS_REPL_IPC";
const SESSION_ENV = "MAIEUTICS_REPL_SESSION";
const GENERATION_ENV = "MAIEUTICS_REPL_GENERATION";
const CLIENT_ENV = "MAIEUTICS_REPL_CLIENT";
const CREDENTIAL_ENV = "MAIEUTICS_REPL_CREDENTIAL";

export async function runDenoReplFromEnvironment(): Promise<void> {
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
      const pipeName = requireEnvironment("MAIEUTICS_REPL_PIPE");
      const bootstrap = bootstrapWindowsCredential(pipeName);
      if (bootstrap.sessionId !== sessionId) {
        throw new Error("The Windows bootstrap credential belongs to another REPL session.");
      }
      credential = bootstrap.credential;
    }
  }
  await new ReplClient({
    address,
    sessionId,
    generation,
    ...(credential === undefined ? {} : { credential }),
  }).run();
}

function requireEnvironment(name: string): string {
  const value = Deno.env.get(name);
  if (value === undefined || value.length === 0) {
    throw new Error(`Missing ${name} environment variable.`);
  }
  return value;
}

if (import.meta.main) {
  try {
    await runDenoReplFromEnvironment();
  } catch (error) {
    console.error(error instanceof Error ? error.stack ?? error.message : String(error));
    Deno.exit(1);
  }
}
