import { assert, assertEquals } from "@std/assert";
import plugin from "./lint-plugin.ts";

// The plugin locates maieutics.json by walking up from the linted file, so
// every test fixture materializes a temp project with its own maieutics.json.
function tempProject(entrypoints: Record<string, string[]>): string {
  const dir = Deno.makeTempDirSync({ prefix: "maieutics-lint-" });
  Deno.writeTextFileSync(
    `${dir}/maieutics.json`,
    JSON.stringify({ entrypoints }, null, 2),
  );
  return dir;
}

interface Diagnostic {
  id: string;
  message: string;
  range: [number, number];
  fix?: unknown[];
}

async function lint(
  dir: string,
  filename: string,
  source: string,
): Promise<Diagnostic[]> {
  return await Deno.lint.runPlugin(
    { name: plugin.name, rules: plugin.rules },
    `${dir}/${filename}`,
    source,
  ) as Diagnostic[];
}

const SDK_IMPORT = 'import { defineActor } from "@maieutics/plugin-sdk";\n';

Deno.test("entrypoint-registered: a defineActor export outside entrypoints is reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(
    dir,
    "orphan.ts",
    SDK_IMPORT + "export const a = defineActor({ x() { return 1; } });\n",
  );
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-registered");
  assertEquals(hit.length, 1);
  assert(hit[0].message.includes("maieutics.json"));
});

Deno.test("entrypoint-registered: a defineActor export inside entrypoints is silent", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(
    dir,
    "mod.ts",
    SDK_IMPORT + "export const a = defineActor({ x() { return 1; } });\n",
  );
  assertEquals(diags.filter((d) => d.id === "maieutics/entrypoint-registered").length, 0);
});

Deno.test("entrypoint-registered: no maieutics.json is silent", async () => {
  const dir = Deno.makeTempDirSync({ prefix: "maieutics-lint-noconf-" });
  const diags = await lint(
    dir,
    "orphan.ts",
    SDK_IMPORT + "export const a = defineActor({ x() { return 1; } });\n",
  );
  assertEquals(diags.filter((d) => d.id === "maieutics/entrypoint-registered").length, 0);
});

Deno.test("entrypoint-exports: bare object literal export gets a defineActor fix", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const source = SDK_IMPORT + 'export const api = { hello() { return "hi"; } };\n';
  const diags = await lint(dir, "mod.ts", source);
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-exports");
  assertEquals(hit.length, 1);
  assert((hit[0].fix?.length ?? 0) > 0, "expected a fix for a function-only object literal");
});

Deno.test("entrypoint-exports: bare constant export is reported without a fix", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(dir, "mod.ts", "export const plain = 42;\n");
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-exports");
  assertEquals(hit.length, 1);
  assertEquals(hit[0].fix?.length ?? 0, 0, "a constant cannot be safely auto-wrapped");
});

Deno.test("entrypoint-exports: bare function export is reported with a defineActor fix", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(
    dir,
    "mod.ts",
    SDK_IMPORT + "export function helper() { return 1; }\n",
  );
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-exports");
  assertEquals(hit.length, 1);
  assert((hit[0].fix?.length ?? 0) > 0, "expected a fix wrapping the function in defineActor");
});

Deno.test("entrypoint-exports: a defineActor-wrapped export passes", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(
    dir,
    "mod.ts",
    SDK_IMPORT + "export const math = defineActor({ double(n: number) { return n * 2; } });\n",
  );
  assertEquals(diags.filter((d) => d.id === "maieutics/entrypoint-exports").length, 0);
});

Deno.test("entrypoint-exports: non-entrypoint files are not checked", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(dir, "helper.ts", "export const plain = 42;\n");
  assertEquals(diags.filter((d) => d.id === "maieutics/entrypoint-exports").length, 0);
});

Deno.test("entrypoint-exports: function fix preserves annotations and async/generator", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = SDK_IMPORT +
    "export async function fetchIt(url: string): Promise<string> { return url; }\n";
  const diags = await lint(dir, "mod.ts", src);
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-exports");
  assertEquals(hit.length, 1);
  // The fix text must contain the full function (annotations + async preserved).
  const fixText = JSON.stringify(hit[0].fix);
  assert(fixText.includes("async function fetchIt(url: string): Promise<string>"));
});

// —— provide-once ——

const REACTIVE_IMPORT = 'import { provide, signal } from "@maieutics/plugin-sdk";\n';

Deno.test("provide-once: the same signal provided twice is reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = REACTIVE_IMPORT +
    'export const ep = defineExtensionPoint<number>("m");\n' +
    "const s = signal(1);\n" +
    "provide(ep, s);\n" +
    "provide(ep, s);\n";
  const diags = await lint(dir, "mod.ts", src);
  const hit = diags.filter((d) => d.id === "maieutics/provide-once");
  assertEquals(hit.length, 1);
  assert(hit[0].message.includes("s"));
});

Deno.test("provide-once: distinct signals are not reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = REACTIVE_IMPORT +
    'export const ep = defineExtensionPoint<number>("m");\n' +
    "const a = signal(1);\n" +
    "const b = signal(2);\n" +
    "provide(ep, a);\n" +
    "provide(ep, b);\n";
  const diags = await lint(dir, "mod.ts", src);
  assertEquals(diags.filter((d) => d.id === "maieutics/provide-once").length, 0);
});

Deno.test("provide-once: namespace import form is tracked", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = 'import * as sdk from "@maieutics/plugin-sdk";\n' +
    'export const ep = sdk.defineExtensionPoint<number>("m");\n' +
    "const s = sdk.signal(1);\n" +
    "sdk.provide(ep, s);\n" +
    "sdk.provide(ep, s);\n";
  const diags = await lint(dir, "mod.ts", src);
  const hit = diags.filter((d) => d.id === "maieutics/provide-once");
  assertEquals(hit.length, 1);
});

Deno.test("provide-once: an imported alias of provide is tracked", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = 'import { provide as contribute, signal } from "@maieutics/plugin-sdk";\n' +
    'export const ep = defineExtensionPoint<number>("m");\n' +
    "const s = signal(1);\n" +
    "contribute(ep, s);\n" +
    "contribute(ep, s);\n";
  const diags = await lint(dir, "mod.ts", src);
  const hit = diags.filter((d) => d.id === "maieutics/provide-once");
  assertEquals(hit.length, 1);
});

Deno.test("provide-once: a non-identifier signal argument is not tracked", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = REACTIVE_IMPORT +
    'export const ep = defineExtensionPoint<number>("m");\n' +
    "provide(ep, signal(1));\n" +
    "provide(ep, signal(2));\n";
  const diags = await lint(dir, "mod.ts", src);
  assertEquals(diags.filter((d) => d.id === "maieutics/provide-once").length, 0);
});

Deno.test("entrypoint-exports: export default is reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(dir, "mod.ts", "export default function main() { return 1; }\n");
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-exports");
  assertEquals(hit.length, 1);
});

Deno.test("entrypoint-exports: export * from is reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(dir, "mod.ts", 'export * from "./other.ts";\n');
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-exports");
  assertEquals(hit.length, 1);
});

Deno.test("entrypoint-exports: multi-declarator exports are each checked", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(dir, "mod.ts", "export const c = 42, d = 43;\n");
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-exports");
  assertEquals(hit.length, 2);
});

Deno.test("entrypoint-exports: namespace import defineActor call passes", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(
    dir,
    "mod.ts",
    'import * as sdk from "@maieutics/plugin-sdk";\n' +
      "export const a = sdk.defineActor({ x() { return 1; } });\n",
  );
  assertEquals(diags.filter((d) => d.id === "maieutics/entrypoint-exports").length, 0);
});

Deno.test("entrypoint-exports: type exports are exempt", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(
    dir,
    "mod.ts",
    "export interface Shape { a: number; }\nexport type N = number;\n",
  );
  assertEquals(diags.filter((d) => d.id === "maieutics/entrypoint-exports").length, 0);
});

Deno.test("entrypoint-exports: no defineActor import means no fix is emitted", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(dir, "mod.ts", 'export const api = { hello() { return "hi"; } };\n');
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-exports");
  assertEquals(hit.length, 1);
  assertEquals(hit[0].fix?.length ?? 0, 0, "no import of defineActor -> no fix");
});

Deno.test("entrypoint-registered: namespace import defineActor export outside entrypoints is reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(
    dir,
    "orphan.ts",
    'import * as sdk from "@maieutics/plugin-sdk";\n' +
      "export const a = sdk.defineActor({ x() { return 1; } });\n",
  );
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-registered");
  assertEquals(hit.length, 1);
});

Deno.test("entrypoint-registered: alias import defineActor export outside entrypoints is reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const diags = await lint(
    dir,
    "orphan.ts",
    'import { defineActor as da } from "@maieutics/plugin-sdk";\n' +
      "export const a = da({ x() { return 1; } });\n",
  );
  const hit = diags.filter((d) => d.id === "maieutics/entrypoint-registered");
  assertEquals(hit.length, 1);
});

// —— provide-top-level ——

Deno.test("provide-top-level: a top-level provide is silent", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = REACTIVE_IMPORT +
    'export const ep = defineExtensionPoint<number>("m");\n' +
    "const s = signal(1);\n" +
    "provide(ep, s);\n";
  const diags = await lint(dir, "mod.ts", src);
  assertEquals(diags.filter((d) => d.id === "maieutics/provide-top-level").length, 0);
});

Deno.test("provide-top-level: a provide inside a function is reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = REACTIVE_IMPORT +
    'export const ep = defineExtensionPoint<number>("m");\n' +
    "const s = signal(1);\n" +
    "function setup() { provide(ep, s); }\n" +
    "setup();\n";
  const diags = await lint(dir, "mod.ts", src);
  const hit = diags.filter((d) => d.id === "maieutics/provide-top-level");
  assertEquals(hit.length, 1);
  assert(hit[0].message.includes("top level"));
});

Deno.test("provide-top-level: arrow and method bodies are reported", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = REACTIVE_IMPORT +
    'export const ep = defineExtensionPoint<number>("m");\n' +
    "const s = signal(1);\n" +
    "const start = () => { provide(ep, s); };\n" +
    "const obj = { run() { provide(ep, s); } };\n";
  const diags = await lint(dir, "mod.ts", src);
  const hit = diags.filter((d) => d.id === "maieutics/provide-top-level");
  assertEquals(hit.length, 2);
});

Deno.test("provide-top-level: a conditional top-level provide is silent", async () => {
  const dir = tempProject({ main: ["./mod.ts"] });
  const src = REACTIVE_IMPORT +
    'export const ep = defineExtensionPoint<number>("m");\n' +
    "const s = signal(1);\n" +
    "if (isProd) { provide(ep, s); }\n";
  const diags = await lint(dir, "mod.ts", src);
  assertEquals(diags.filter((d) => d.id === "maieutics/provide-top-level").length, 0);
});
