import { ReplClient } from "./repl_client.ts";
import { readReplProcessEnvironment } from "./repl_process_env.ts";

export async function runDenoReplFromEnvironment(): Promise<void> {
  const options = readReplProcessEnvironment();
  await new ReplClient(options).run();
}

if (import.meta.main) {
  try {
    await runDenoReplFromEnvironment();
  } catch (error) {
    console.error(error instanceof Error ? error.stack ?? error.message : String(error));
    Deno.exit(1);
  }
}
