/**
 * Sentinel for the runtime resolution paths a plugin worker depends on
 * (docs/plugin-import-resolution.md §5): with the SDK-shaped loader hooks
 * installed — pass-through `resolve` and `load` — bare aliases resolve through
 * the process import map and self-contained jsr:/npm: specifiers resolve
 * through the registry, for static and dynamic imports across version forms.
 *
 * The probe runs in a subprocess because workers inherit the process config:
 * the fixture aliases must come from a `--config` this test controls, matching
 * how the kernel materializes the host's root deno.json. The subprocess runs
 * with full permissions, so a failure here maps to the resolution paths in
 * §2/§11 of the design doc, not to plugin code — check the Deno version first.
 * (The real host topology additionally gates registry concretization on the
 * worker's `import` grant — §5.1 — which this probe deliberately does not
 * exercise; that gate is covered by the .NET readiness theory.)
 */

import Path from "node:path";
Deno.test("hooked resolution matrix: process-map aliases and direct jsr/npm specifiers", async () => {
  const dir = await Deno.makeTempDir({ prefix: "mc-hooks-matrix-" });
  try {
    await Deno.writeTextFile(
      Path.join(dir, "deno.json"),
      JSON.stringify({
        imports: {
          "@ghostflyby/worker-actor": "jsr:@ghostflyby/worker-actor@0.6.0",
          bytes10: "jsr:@std/bytes@1.0",
          btilde: "jsr:@std/bytes@~1.0",
          pathcare: "jsr:@std/path@^1",
        },
      }),
    );
    // The plugin-entry shape: bare alias imports INSIDE a dynamically imported
    // module — this is the edge that only reaches the resolve hook while a load
    // hook is installed (docs §6.2 invariant 1).
    await Deno.writeTextFile(
      Path.join(dir, "probe_static_aliases.ts"),
      [
        `import { concat } from "bytes10/concat";`,
        `import { basename } from "pathcare/basename";`,
        `console.log("static alias bytes@1.0:", typeof concat === "function" ? "OK" : "BROKEN");`,
        `console.log("static alias path@^1:", typeof basename === "function" ? "OK" : "BROKEN");`,
      ].join("\n"),
    );
    await Deno.writeTextFile(
      Path.join(dir, "probe_main.ts"),
      [
        `import { registerHooks } from "node:module";`,
        `registerHooks({`,
        `  resolve: (specifier, context, nextResolve) => nextResolve(specifier, context),`,
        `  load: (url, context, nextLoad) => nextLoad(url, context),`,
        `});`,
        `await import("./probe_static_aliases.ts");`,
        `const probes = [`,
        `  ["dynamic alias bytes@1.0", () => import("bytes10/concat")],`,
        `  ["dynamic alias bytes@~1.0", () => import("btilde/concat")],`,
        `  ["dynamic alias path@^1", () => import("pathcare/basename")],`,
        `  ["dynamic direct jsr:@std/bytes@^1/concat", () => import("jsr:@std/bytes@^1/concat")],`,
        `  ["static-in-module direct jsr:@std/bytes@1.0.6/concat", () => import("./probe_direct.ts")],`,
        `  ["dynamic npm:chalk@^5", () => import("npm:chalk@^5")],`,
        `] as const;`,
        `for (const [label, load] of probes) {`,
        `  try { await load(); console.log(label + ": OK"); }`,
        `  catch (error) { console.log(label + ": FAILED " + (error as Error).message.slice(0, 60)); }`,
        `}`,
      ].join("\n"),
    );
    await Deno.writeTextFile(
      Path.join(dir, "probe_direct.ts"),
      `export { concat } from "jsr:@std/bytes@1.0.6/concat";`,
    );

    const command = new Deno.Command(Deno.execPath(), {
      args: ["run", "-A", "--config", Path.join(dir, "deno.json"), Path.join(dir, "probe_main.ts")],
      stdout: "piped",
      stderr: "piped",
    });
    const output = await command.output();
    const stdout = new TextDecoder().decode(output.stdout);
    const labels = [
      "static alias bytes@1.0",
      "static alias path@^1",
      "dynamic alias bytes@1.0",
      "dynamic alias bytes@~1.0",
      "dynamic alias path@^1",
      "dynamic direct jsr:@std/bytes@^1/concat",
      "static-in-module direct jsr:@std/bytes@1.0.6/concat",
      "dynamic npm:chalk@^5",
    ];
    const failures = labels.filter((label) => !stdout.includes(`${label}: OK`));
    if (failures.length > 0 || !output.success) {
      throw new Error(
        `Hooked resolution matrix regressed (see docs/plugin-import-resolution.md §11):\n` +
          failures.map((label) => `- ${label}`).join("\n") +
          `\n--- probe stdout ---\n${stdout}` +
          `\n--- probe stderr ---\n${new TextDecoder().decode(output.stderr).slice(0, 800)}`,
      );
    }
  } finally {
    await Deno.remove(dir, { recursive: true });
  }
});
