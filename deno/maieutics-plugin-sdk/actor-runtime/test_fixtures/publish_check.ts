/** Assert the actual Deno publish plan, rather than duplicating its glob rules. */
import { assertPublishPlan } from "./publish_plan.ts";
const root = new URL("../../", import.meta.url);
const output = await new Deno.Command(Deno.execPath(), {
  args: ["publish", "--dry-run", "--allow-dirty", "--no-provenance"],
  cwd: root,
  stdout: "piped",
  stderr: "piped",
  env: { NO_COLOR: "1" },
}).output();
const diagnostics = new TextDecoder().decode(output.stdout) +
  new TextDecoder().decode(output.stderr);
if (!output.success) throw new Error(diagnostics);
const files = assertPublishPlan(diagnostics, root);
console.log(
  `SDK publish dry-run verified ${files.length} files; no Actor fixtures or tests included.`,
);
