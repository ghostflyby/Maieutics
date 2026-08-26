import { assertEquals, assertRejects } from "@std/assert";
import { createTsxTransform } from "./transform.ts";

Deno.test("tsx transform compiles JSX to the widget automatic-runtime ESM", async () => {
  const transform = createTsxTransform();
  const out = await transform(
    `const el = <div class="x">hello</div>;`,
    { loader: "ts", format: "esm" },
  );
  assertEquals(out.code.includes('from "maieutics-widgets/jsx-runtime"'), true);
  assertEquals(out.code.includes("jsx"), true);
});

Deno.test("tsx transform keeps type stripping", async () => {
  const transform = createTsxTransform();
  const out = await transform(
    `const n: number = 5; const s = <span>{n}</span>;`,
    { loader: "ts", format: "esm" },
  );
  assertEquals(out.code.includes(": number"), false);
  assertEquals(out.code.includes("n = 5"), true);
});

Deno.test("tsx transform is reusable across calls (single wasm service)", async () => {
  const transform = createTsxTransform();
  const first = await transform(`const a = 1;`, { loader: "ts", format: "esm" });
  const second = await transform(`const b = 2;`, { loader: "ts", format: "esm" });
  assertEquals(first.code.includes("a = 1"), true);
  assertEquals(second.code.includes("b = 2"), true);
});

Deno.test("tsx transform rejects malformed JSX with a typed error", async () => {
  const transform = createTsxTransform();
  await assertRejects(
    () => transform(`const x = <div>`, { loader: "ts", format: "esm" }),
  );
});
