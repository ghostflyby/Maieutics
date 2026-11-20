/** Portable evidence identifies input bytes, without claiming an unmeasured Git revision. */
export type SourceHashes = Readonly<Record<string, string>>;

export const scrollMutations = [
  {
    name: "following-scroll-disabled",
    mutation: {
      file: "vue/history.ts",
      before: "if (following.value && viewport.value && !frozenLayout.value)",
      after: "if (false && following.value && viewport.value && !frozenLayout.value)",
      expected_matches: 1,
    },
    assertion: "following mode left a measured growing tail below the viewport",
  },
  {
    name: "anchor-write-omitted-acknowledgement-retained",
    mutation: {
      file: "vue/history.ts",
      before: "virtual.value.scrollToOffset(position[0] + inside);",
      after: "void position; // Mutation: retain acknowledgement but omit the anchor scroll write.",
      expected_matches: 1,
    },
    assertion: "non-frozen page replacement moved the history anchor",
  },
  {
    name: "unloaded-gesture-offset-zeroed",
    mutation: {
      file: "vue/history.ts",
      before: "offset: anchor.inside,",
      after: "offset: 0,",
      expected_matches: 1,
    },
    assertion: "unloaded gesture acknowledgement lost its pixel offset",
  },
  {
    name: "loaded-gesture-offset-zeroed",
    mutation: {
      file: "vue/history.ts",
      before: "offset: anchor.inside,",
      after: 'offset: target.type === "entry" ? 0 : anchor.inside,',
      expected_matches: 1,
    },
    assertion: "loaded gesture acknowledgement lost its pixel offset",
  },
  {
    name: "projection-ordinal-zeroed",
    mutation: {
      file: "vue/history.ts",
      before: "ordinal: target.ordinal,",
      after: "ordinal: 0,",
      expected_matches: 1,
    },
    assertion: "projection acknowledgement lost its logical ordinal",
  },
  {
    name: "projection-offset-zeroed",
    mutation: {
      file: "vue/history.ts",
      before: "offset: inside",
      after: "offset: 0",
      expected_matches: 1,
    },
    assertion: "projection acknowledgement lost its pixel offset",
  },
] as const;

export interface ScrollMutation {
  readonly file: "vue/history.ts";
  readonly before: string;
  readonly after: string;
  readonly expected_matches: number;
}

/** Only a verdict received from the owned browser can be an expected negative scenario. */
export class BrowserScenarioFailure extends Error {
  override name = "BrowserScenarioFailure";
}

export interface ScrollEvidence {
  readonly schema_version: 3;
  readonly source: { readonly kind: "worktree-snapshot"; readonly files_sha256: SourceHashes };
  readonly command: "deno task check:evidence";
  readonly cases: readonly {
    readonly name: string;
    readonly mutation: {
      readonly file: string;
      readonly before: string;
      readonly after: string;
      readonly expected_matches: number;
    };
    readonly outcome: "failed";
    readonly assertion: string;
  }[];
  readonly restored: {
    readonly outcome: "passed";
    readonly scenario_count: number;
    readonly scenarios: readonly string[];
  };
}

function require(condition: unknown, explanation: string): asserts condition {
  if (!condition) throw new Error(`UI regression evidence: ${explanation}`);
}
function object(value: unknown, keys: readonly string[]): Record<string, unknown> {
  require(typeof value === "object" && value !== null && !Array.isArray(value), "invalid record");
  const record = value as Record<string, unknown>;
  require(
    Object.keys(record).length === keys.length && keys.every((key) => Object.hasOwn(record, key)),
    "unexpected fields; record a worktree snapshot without Git or environment metadata",
  );
  return record;
}

/** Reject stale, incomplete or non-portable evidence before running the browser job. */
export function verifyEvidence(value: unknown, current: SourceHashes): void {
  const record = object(value, ["schema_version", "source", "command", "cases", "restored"]);
  require(
    record.schema_version === 3 && record.command === "deno task check:evidence",
    "unsupported schema or command",
  );
  const source = object(record.source, ["kind", "files_sha256"]);
  require(source.kind === "worktree-snapshot", "source must identify the measured working tree");
  require(
    typeof source.files_sha256 === "object" && source.files_sha256 !== null &&
      !Array.isArray(source.files_sha256),
    "missing source hashes",
  );
  const hashes = source.files_sha256 as Record<string, unknown>;
  const names = Object.keys(current);
  require(
    names.length > 0 && names.length === Object.keys(hashes).length &&
      names.every((name) => Object.hasOwn(hashes, name)),
    "source inventory changed; rerun all mutations and the restored suite",
  );
  for (const name of names) {
    require(
      typeof hashes[name] === "string" && /^[a-f0-9]{64}$/.test(hashes[name]),
      `invalid hash for ${name}`,
    );
    require(
      hashes[name] === current[name],
      `stale source ${name}; rerun all mutations and the restored suite`,
    );
  }
  require(
    Array.isArray(record.cases) && record.cases.length === scrollMutations.length,
    "missing mutation results",
  );
  for (const [index, expected] of scrollMutations.entries()) {
    const result = object(record.cases[index], ["name", "mutation", "outcome", "assertion"]);
    require(
      result.name === expected.name && result.outcome === "failed" &&
        result.assertion === expected.assertion,
      "mutation must fail at its intended assertion, without environment-dependent logs",
    );
    const mutation = object(result.mutation, ["file", "before", "after", "expected_matches"]);
    for (const key of ["file", "before", "after", "expected_matches"] as const) {
      require(
        mutation[key] === expected.mutation[key],
        `mutation definition changed: ${expected.name}`,
      );
    }
  }
  const restored = object(record.restored, ["outcome", "scenario_count", "scenarios"]);
  require(
    restored.outcome === "passed" && typeof restored.scenario_count === "number" &&
      Number.isSafeInteger(restored.scenario_count) && restored.scenario_count > 0 &&
      Array.isArray(restored.scenarios) && restored.scenarios.length === restored.scenario_count &&
      restored.scenarios.every((name) => typeof name === "string" && name.length > 0) &&
      new Set(restored.scenarios).size === restored.scenarios.length,
    "restored suite did not pass",
  );
}

/** Enumerate source/test inputs so additions and deletions also invalidate recorded evidence. */
export async function sourceHashes(root: URL): Promise<SourceHashes> {
  const files: { name: string; url: URL }[] = [];
  const walk = async (directory: URL, prefix: string): Promise<void> => {
    for await (const entry of Deno.readDir(directory)) {
      const name = prefix + entry.name;
      if (
        name === ".git" || name === "dist" || name === "node_modules" ||
        name === "test/evidence" || entry.name === ".DS_Store"
      ) continue;
      require(!entry.isSymlink, `symbolic inputs are unsupported: ${name}`);
      const url = new URL(
        encodeURIComponent(entry.name) + (entry.isDirectory ? "/" : ""),
        directory,
      );
      if (entry.isDirectory) await walk(url, `${name}/`);
      else if (entry.isFile) files.push({ name, url });
    }
  };
  await walk(root, "");
  for (const name of ["../deno.json", "../deno.lock"]) {
    files.push({ name, url: new URL(name, root) });
  }
  const hashes = await Promise.all(
    files.sort((a, b) => a.name.localeCompare(b.name, "en")).map(async ({ name, url }) => {
      const bytes = await Deno.readFile(url);
      const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
      return [
        name,
        [...digest].map((byte) => byte.toString(16).padStart(2, "0")).join(""),
      ] as const;
    }),
  );
  return Object.fromEntries(hashes);
}

/** Apply exactly the intended single mutation; missing/duplicate matches cannot count as evidence. */
export function applyMutation(source: string, mutation: ScrollMutation): string {
  require(
    mutation.before.length > 0 && mutation.expected_matches === 1 &&
      source.split(mutation.before).length - 1 === mutation.expected_matches,
    "mutation no longer matches once",
  );
  return source.replace(mutation.before, () => mutation.after);
}

/** Execute each negative case and a restored suite; static JSON claims cannot satisfy this gate. */
export async function observeEvidence(
  files: SourceHashes,
  run: (mutation?: ScrollMutation) => Promise<string[]>,
): Promise<ScrollEvidence> {
  const cases: ScrollEvidence["cases"][number][] = [];
  for (const expected of scrollMutations) {
    let failure: unknown;
    try {
      await run(expected.mutation);
    } catch (error) {
      failure = error;
    }
    require(failure !== undefined, `mutant survived: ${expected.name}`);
    if (
      !(failure instanceof BrowserScenarioFailure && failure.message.includes(expected.assertion))
    ) {
      throw new Error(
        `UI regression evidence: mutant failed at an unexpected assertion: ${expected.name}`,
        { cause: failure },
      );
    }
    cases.push({ ...expected, outcome: "failed" });
  }
  const scenarios = await run();
  const result: ScrollEvidence = {
    schema_version: 3,
    source: { kind: "worktree-snapshot", files_sha256: files },
    command: "deno task check:evidence",
    cases,
    restored: { outcome: "passed", scenario_count: scenarios.length, scenarios },
  };
  verifyEvidence(result, files);
  return result;
}

/** Compare the recorded successful scenarios with today's executed browser result, including names. */
export function verifyObservedEvidence(record: unknown, observed: ScrollEvidence): void {
  verifyEvidence(record, observed.source.files_sha256);
  const restored = object(
    object(record, ["schema_version", "source", "command", "cases", "restored"]).restored,
    ["outcome", "scenario_count", "scenarios"],
  );
  require(
    restored.scenario_count === observed.restored.scenario_count &&
      JSON.stringify(restored.scenarios) === JSON.stringify(observed.restored.scenarios),
    "recorded scenarios differ from the executed restored suite",
  );
}
