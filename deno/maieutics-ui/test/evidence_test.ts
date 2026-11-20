import { assertThrows } from "@std/assert";
import {
  type ScrollEvidence,
  scrollMutations,
  type SourceHashes,
  verifyEvidence,
} from "./evidence.ts";

const current = { "vue/history.ts": "a".repeat(64), "test/browser_conformance.ts": "b".repeat(64) };
function evidence(files: SourceHashes = current): ScrollEvidence {
  return {
    schema_version: 2,
    source: { kind: "worktree-snapshot", files_sha256: { ...files } },
    command: "deno task test:browser",
    cases: scrollMutations.map((mutation) => ({ ...mutation, exit_code: 1 })),
    restored: { exit_code: 0, scenario_count: 28 },
  };
}

Deno.test("evidence accepts exact measured worktree inputs without asserting a Git tree", () => {
  verifyEvidence(evidence(), current);
});
Deno.test("evidence rejects changed source or test bytes", () => {
  for (const name of Object.keys(current)) {
    assertThrows(
      () => verifyEvidence(evidence(), { ...current, [name]: "c".repeat(64) }),
      Error,
      `stale source ${name}`,
    );
  }
});
Deno.test("evidence rejects new or removed input files", () => {
  assertThrows(
    () => verifyEvidence(evidence(), { ...current, "test/new_case.ts": "c".repeat(64) }),
    Error,
    "inventory changed",
  );
  assertThrows(
    () => verifyEvidence(evidence(), { "vue/history.ts": current["vue/history.ts"] }),
    Error,
    "inventory changed",
  );
});
Deno.test("evidence rejects legacy commit claims and environment noise", () => {
  assertThrows(
    () => verifyEvidence({ ...evidence(), base_commit: "unmeasured" }, current),
    Error,
    "unexpected fields",
  );
  assertThrows(
    () => verifyEvidence({ ...evidence(), toolchain: { platform: "local" } }, current),
    Error,
    "unexpected fields",
  );
});
Deno.test("evidence rejects a failure at a different assertion", () => {
  const sample = evidence();
  const cases = sample.cases.map((entry, index) =>
    index === 0 ? { ...entry, assertion: "browser startup failed" } : entry
  );
  assertThrows(() => verifyEvidence({ ...sample, cases }, current), Error, "intended assertion");
});
