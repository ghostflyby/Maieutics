/**
 * Unit tests for the recursive Worker patch that can run without spawning
 * workers: specifier resolution, the classic-unsupported contract, and the
 * wrapper-URL builder round trip.
 */

import { assertEquals, assertThrows } from "@std/assert";
import { BOOTSTRAP_VERSION, buildWrapperUrl, readBootstrapMetadata } from "./bootstrap_contract.ts";
import { fileUrlFromStackLine, resolveWorkerSpecifier } from "./worker_patch.ts";

const BASE = "file:///materialized/maieutics-plugin-host/worker_entry.ts";

Deno.test("resolveWorkerSpecifier resolves relative specifiers against the caller base", () => {
  assertEquals(
    resolveWorkerSpecifier("./mod.ts", "file:///plugin/dir/"),
    "file:///plugin/dir/mod.ts",
  );
  assertEquals(
    resolveWorkerSpecifier("sub/worker.ts", "file:///plugin/dir/main.ts"),
    "file:///plugin/dir/sub/worker.ts",
  );
});

Deno.test("resolveWorkerSpecifier passes absolute and URL specifiers through unchanged", () => {
  const absolute = "file:///plugin/dir/worker.ts";
  assertEquals(resolveWorkerSpecifier(absolute, BASE), absolute);
  const asUrl = new URL("file:///plugin/dir/worker.ts");
  assertEquals(resolveWorkerSpecifier(asUrl, BASE), asUrl.href);
});

Deno.test("resolveWorkerSpecifier falls back to the wrapper directory without a caller base", () => {
  // The wrapper lives in maieutics-runtime/; the fallback is wrapper-relative.
  const resolved = resolveWorkerSpecifier("./mod.ts", undefined);
  assertEquals(resolved.endsWith("/maieutics-runtime/mod.ts"), true, resolved);
});

Deno.test("resolveWorkerSpecifier throws a typed error for specifiers the URL parser rejects", () => {
  assertThrows(
    () => resolveWorkerSpecifier("http://exa mple", "file:///x/"),
    TypeError,
  );
  assertThrows(
    () => resolveWorkerSpecifier("http://[bad", "file:///x/"),
    TypeError,
  );
  // Without a caller base the fallback resolves against the wrapper directory;
  // a specifier the parser rejects still throws.
  assertThrows(
    () => resolveWorkerSpecifier("http://[bad", undefined),
    TypeError,
  );
});

Deno.test("fileUrlFromStackLine strips V8 line/column and keeps Windows drive colons", () => {
  assertEquals(
    fileUrlFromStackLine("    at file:///Users/ghost/worker.ts:130:3"),
    "file:///Users/ghost/worker.ts",
  );
  // A Windows drive colon must survive the match: a `:`-excluding character
  // class would truncate the URL to `file:///C` and break nested Worker
  // specifier resolution on Windows.
  assertEquals(
    fileUrlFromStackLine("    at file:///C:/Users/ghost/worker.ts:130:3"),
    "file:///C:/Users/ghost/worker.ts",
  );
  assertEquals(
    fileUrlFromStackLine("    at fn (file:///C:/x/a.ts:1:2)"),
    "file:///C:/x/a.ts",
  );
  assertEquals(
    fileUrlFromStackLine("    at async file:///Users/x/b.ts:4:5"),
    "file:///Users/x/b.ts",
  );
  assertEquals(fileUrlFromStackLine("    at eval"), undefined);
  assertEquals(fileUrlFromStackLine("    at <anonymous>"), undefined);
});

Deno.test("buildWrapperUrl and readBootstrapMetadata round-trip the target descriptor", () => {
  const target = "file:///app/target.ts";
  const wrapper = buildWrapperUrl(
    "file:///runtime/worker_bootstrap.ts",
    target,
    "plugin",
    BOOTSTRAP_VERSION,
  );
  assertEquals(wrapper.searchParams.get("maieuticsTarget"), target);
  const metadata = readBootstrapMetadata(wrapper);
  assertEquals(metadata, {
    targetUrl: target,
    version: BOOTSTRAP_VERSION,
    profile: "plugin",
  });
});

Deno.test("readBootstrapMetadata returns null for non-wrapper URLs", () => {
  assertEquals(readBootstrapMetadata("file:///runtime/worker_bootstrap.ts"), null);
  assertEquals(readBootstrapMetadata("file:///x.ts?maieuticsTarget="), null);
  assertEquals(
    readBootstrapMetadata("file:///x.ts?maieuticsTarget=file:///t&maieuticsVersion=nope"),
    null,
  );
  assertEquals(
    readBootstrapMetadata("file:///x.ts?maieuticsTarget=file:///t&maieuticsVersion=1"),
    null,
  );
});

Deno.test("the wrapper URL preserves the target and carries only non-sensitive markers", () => {
  const target = "file:///app/target.ts";
  const wrapper = buildWrapperUrl("file:///runtime/worker_bootstrap.ts", target, "repl");
  const query = wrapper.searchParams;
  assertEquals(query.size, 3);
  assertEquals(query.get("maieuticsTarget"), target);
  assertEquals(query.get("maieuticsVersion"), String(BOOTSTRAP_VERSION));
  assertEquals(query.get("maieuticsProfile"), "repl");
});
