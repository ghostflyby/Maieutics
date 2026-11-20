import { assert, assertEquals, assertRejects, assertThrows } from "@std/assert";
import {
  applyMutation,
  BrowserScenarioFailure,
  observeEvidence,
  type ScrollEvidence,
  scrollMutations,
  type SourceHashes,
  sourceHashes,
  verifyEvidence,
  verifyObservedEvidence,
} from "./evidence.ts";

const current = { "vue/history.ts": "a".repeat(64), "test/browser_conformance.ts": "b".repeat(64) };
function evidence(files: SourceHashes = current): ScrollEvidence {
  return {
    schema_version: 3,
    source: { kind: "worktree-snapshot", files_sha256: { ...files } },
    command: "deno task check:evidence",
    cases: scrollMutations.map((mutation) => ({ ...mutation, outcome: "failed" })),
    restored: { outcome: "passed", scenario_count: 2, scenarios: ["first", "second"] },
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

Deno.test("evidence rejects a different reproduction command with an otherwise valid schema", () => {
  assertThrows(
    () => verifyEvidence({ ...evidence(), command: "deno task test:browser" }, current),
    Error,
    "unsupported schema or command",
  );
});

Deno.test("evidence rejects drift in each recorded mutation field", () => {
  for (const field of ["file", "before", "after", "expected_matches"] as const) {
    const sample = evidence();
    const cases = sample.cases.map((entry, index) =>
      index === 0
        ? {
          ...entry,
          mutation: {
            ...entry.mutation,
            [field]: field === "expected_matches" ? 2 : `changed ${entry.mutation[field]}`,
          },
        }
        : entry
    );
    assertThrows(
      () => verifyEvidence({ ...sample, cases }, current),
      Error,
      "mutation definition changed",
    );
  }
});

Deno.test("evidence rejects malformed hashes, schema, cases and restored outcomes", () => {
  const invalid: unknown[] = [
    { ...evidence(), schema_version: 99 },
    {
      ...evidence(),
      source: {
        kind: "worktree-snapshot",
        files_sha256: { ...current, "vue/history.ts": "invalid" },
      },
    },
    { ...evidence(), cases: undefined },
    { ...evidence(), cases: [] },
    {
      ...evidence(),
      cases: evidence().cases.map((entry) => ({ ...entry, outcome: "passed" })),
    },
    {
      ...evidence(),
      restored: { outcome: "failed", scenario_count: 2, scenarios: ["first", "second"] },
    },
    {
      ...evidence(),
      restored: { ...evidence().restored, exit_code: 1 },
    },
    {
      ...evidence(),
      restored: { outcome: "passed", scenario_count: 3, scenarios: ["first", "second"] },
    },
    {
      ...evidence(),
      restored: { outcome: "passed", scenario_count: 2, scenarios: ["first", "first"] },
    },
  ];
  const { cases: _cases, ...missing } = evidence();
  invalid.push(missing);
  for (const sample of invalid) {
    assertThrows(() => verifyEvidence(sample, current), Error, "UI regression evidence");
  }
});

Deno.test("mutation matching rejects zero, duplicate and empty occurrences", () => {
  const mutation = scrollMutations[0].mutation;
  assertThrows(() => applyMutation("unrelated", mutation), Error, "matches once");
  assertThrows(
    () => applyMutation(mutation.before + mutation.before, mutation),
    Error,
    "matches once",
  );
  assertThrows(() => applyMutation("text", { ...mutation, before: "" }), Error, "matches once");
  assertEquals(
    applyMutation(`prefix ${mutation.before} suffix`, mutation),
    `prefix ${mutation.after} suffix`,
  );
});

Deno.test("mutation replacements preserve literal dollar sequences", () => {
  const after = ["$$", "$&", "$`", "$'", "$1", "${template}", "/\\$\\d+/"].join(" ");
  assertEquals(
    applyMutation("prefix TOKEN suffix", {
      ...scrollMutations[0].mutation,
      before: "TOKEN",
      after,
    }),
    "prefix " + after + " suffix",
  );
});

Deno.test("hash inventory includes every file format and hidden fixtures with explicit output exclusions", async () => {
  const directory = await Deno.makeTempDir({ prefix: "maieutics-evidence-inputs-" });
  const base = new URL("file:///");
  base.pathname = `${directory.replaceAll("\\", "/")}/`;
  const root = new URL("ui/", base);
  try {
    await Deno.mkdir(root);
    await Deno.writeTextFile(new URL("deno.json", base), "{}");
    await Deno.writeTextFile(new URL("deno.lock", base), "{}");
    const names = [
      "component.vue",
      "client.js",
      "image.svg",
      "fixture.bin",
      ".hidden",
      "README.md",
      "name #1?.txt",
    ];
    for (const name of names) {
      await Deno.writeFile(
        new URL(encodeURIComponent(name), root),
        new Uint8Array([0, 255, 128, 1]),
      );
    }
    for (const name of ["dist", "node_modules", "test/evidence", ".git"]) {
      const url = new URL(`${name}/`, root);
      await Deno.mkdir(url, { recursive: true });
      await Deno.writeTextFile(new URL("ignored.any", url), "generated");
    }
    await Deno.writeTextFile(new URL(".DS_Store", root), "generated");
    const hashes = await sourceHashes(root);
    assertEquals(Object.keys(hashes).sort(), [...names, "../deno.json", "../deno.lock"].sort());
    assert(/^[a-f0-9]{64}$/.test(hashes["fixture.bin"]));
    const before = hashes["fixture.bin"];
    await Deno.writeFile(new URL("fixture.bin", root), new Uint8Array([0, 255, 128, 2]));
    assert(
      (await sourceHashes(root))["fixture.bin"] !== before,
      "binary fixture drift was not hashed",
    );
  } finally {
    await Deno.remove(directory, { recursive: true });
  }
});

Deno.test("hash inventory rejects symbolic directory inputs instead of silently omitting them", async () => {
  const directory = await Deno.makeTempDir({ prefix: "maieutics-evidence-symlink-" });
  const base = new URL("file:///");
  base.pathname = `${directory.replaceAll("\\", "/")}/`;
  const root = new URL("ui/", base);
  try {
    await Deno.mkdir(root);
    await Deno.mkdir(new URL("target/", base));
    await Deno.writeTextFile(new URL("deno.json", base), "{}");
    await Deno.writeTextFile(new URL("deno.lock", base), "{}");
    // Windows directory junctions avoid requiring symlink-creation privileges.
    await Deno.symlink(new URL("target/", base), new URL("linked-input", root), {
      type: Deno.build.os === "windows" ? "junction" : "dir",
    });
    await assertRejects(
      () => sourceHashes(root),
      Error,
      "symbolic inputs are unsupported: linked-input",
    );
  } finally {
    await Deno.remove(directory, { recursive: true });
  }
});

Deno.test("executed mutation survival and unrelated failures reject even a matching static record", async () => {
  await assertRejects(
    () => observeEvidence(current, () => Promise.resolve(["first", "second"])),
    Error,
    "mutant survived",
  );
  await assertRejects(
    () => observeEvidence(current, () => Promise.reject(new Error("startup error"))),
    Error,
    "unexpected assertion",
  );
  await assertRejects(
    () => observeEvidence(current, () => Promise.reject(new Error(scrollMutations[0].assertion))),
    Error,
    "unexpected assertion",
  );
});

Deno.test("executed restored scenarios must match the record's names and count", async () => {
  const observed = await observeEvidence(current, (mutation) => {
    if (!mutation) return Promise.resolve(["first", "second"]);
    const expected = scrollMutations.find((entry) => entry.mutation === mutation);
    assert(expected);
    return Promise.reject(new BrowserScenarioFailure(expected.assertion));
  });
  verifyObservedEvidence(evidence(), observed);
  const forged = {
    ...evidence(),
    restored: { outcome: "passed", scenario_count: 3, scenarios: ["first", "second", "forged"] },
  };
  verifyEvidence(forged, current);
  assertThrows(() => verifyObservedEvidence(forged, observed), Error, "executed restored suite");
  assertThrows(
    () =>
      verifyObservedEvidence({
        ...evidence(),
        restored: { outcome: "passed", scenario_count: 2, scenarios: ["first", "different"] },
      }, observed),
    Error,
    "executed restored suite",
  );
});
