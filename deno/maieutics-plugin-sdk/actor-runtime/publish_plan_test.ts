import { assertEquals, assertThrows } from "@std/assert";
import { assertPublishPlan } from "./test_fixtures/publish_plan.ts";

const root = new URL("file:///workspace/with%20spaces/sdk/");
const required = [
  "actor-runtime/browser.ts",
  "actor-runtime/core/binary_graph.ts",
  "actor-runtime/spawn_process.ts",
  "actor-runtime/LICENSE",
  "actor-runtime/NOTICE",
];
const plan = (files: string[]) =>
  "Check browser.ts\r\nPublishing @maieutics/plugin-sdk\r\n" +
  files.map((file) => `   ${new URL(file, root).href} (1.25KB)\r\n`).join("") +
  "Success Dry run complete\r\n";

Deno.test("publish plan parses Deno's file-list output with encoded paths and CRLF", () => {
  assertEquals(assertPublishPlan(plan(required), root), required);
});

Deno.test("publish plan rejects format drift instead of passing an empty match", () => {
  assertThrows(() => assertPublishPlan("Success Dry run complete", root), Error, "omitted");
  assertThrows(
    () => assertPublishPlan(plan(required).replaceAll("file:", "changed:"), root),
    Error,
    "omitted",
  );
});

for (
  const leaked of [
    "actor-runtime/test_fixtures/worker.ts",
    "actor-runtime/codec_test.ts",
    "mod_test.ts",
  ]
) {
  Deno.test(`publish plan rejects leaked ${leaked}`, () => {
    assertThrows(
      () => assertPublishPlan(plan([...required, leaked]), root),
      Error,
      "workspace-only",
    );
  });
}

Deno.test("publish plan rejects files outside the package root", () => {
  assertThrows(
    () => assertPublishPlan(plan([...required, "../other.ts"]), root),
    Error,
    "foreign file",
  );
});
