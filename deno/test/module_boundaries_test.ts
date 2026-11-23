/// <reference lib="deno.ns" />
import { assert, assertEquals } from "@std/assert";

const workspace = new URL("../", import.meta.url);
const workspaceTypes = "npm:/@types/node@";

const host = "maieutics-plugin-host/";
const repl = "maieutics-repl-client/";
const workerDependencies = [
  "maieutics-plugin-sdk/",
  "maieutics-runtime/",
  "node:child_process",
  "node:v8",
  "npm:/@vue/reactivity@",
  "npm:/@vue/shared@",
];

for (
  const [entry, allowed] of [
    [repl + "mod.ts", [repl, "shared/", "maieutics-deno-repl/comm.ts"]],
    ["shared/bus.ts", ["shared/"]],
    [host + "worker_entry.ts", [host + "worker_entry.ts", ...workerDependencies]],
    [host + "mod.ts", [host, "shared/", "node:sqlite", ...workerDependencies]],
  ] as const
) {
  Deno.test(`embedded module graph respects its dependency boundary: ${entry}`, async () => {
    const result = await new Deno.Command(Deno.execPath(), {
      args: ["info", "--json", new URL(entry, workspace).href],
      signal: AbortSignal.timeout(15_000),
    }).output();
    assert(result.success, new TextDecoder().decode(result.stderr));
    const graph = JSON.parse(new TextDecoder().decode(result.stdout)) as {
      modules: { specifier: string; error?: string }[];
    };
    assert(graph.modules.length > 1, "the inspected entry must resolve its dependencies");
    for (const module of graph.modules) {
      assertEquals(module.error, undefined, module.specifier);
      const specifier = module.specifier.startsWith(workspace.href)
        ? module.specifier.slice(workspace.href.length)
        : module.specifier;
      assert(
        [workspaceTypes, ...allowed].some((scope) =>
          scope.endsWith("/") || scope.endsWith("@")
            ? specifier.startsWith(scope)
            : specifier === scope
        ),
        `${entry} resolved an unexpected dependency: ${specifier}`,
      );
    }
  });
}
