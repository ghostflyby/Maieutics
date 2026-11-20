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
] as const;

export interface ScrollEvidence {
  readonly schema_version: 2;
  readonly source: { readonly kind: "worktree-snapshot"; readonly files_sha256: SourceHashes };
  readonly command: "deno task test:browser";
  readonly cases: readonly {
    readonly name: string;
    readonly mutation: {
      readonly file: string;
      readonly before: string;
      readonly after: string;
      readonly expected_matches: number;
    };
    readonly exit_code: 1;
    readonly assertion: string;
  }[];
  readonly restored: { readonly exit_code: 0; readonly scenario_count: number };
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
    record.schema_version === 2 && record.command === "deno task test:browser",
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
    "source inventory changed; rerun both mutations and the restored suite",
  );
  for (const name of names) {
    require(
      typeof hashes[name] === "string" && /^[a-f0-9]{64}$/.test(hashes[name]),
      `invalid hash for ${name}`,
    );
    require(
      hashes[name] === current[name],
      `stale source ${name}; rerun both mutations and the restored suite`,
    );
  }
  require(
    Array.isArray(record.cases) && record.cases.length === scrollMutations.length,
    "missing mutation results",
  );
  for (const [index, expected] of scrollMutations.entries()) {
    const result = object(record.cases[index], ["name", "mutation", "exit_code", "assertion"]);
    require(
      result.name === expected.name && result.exit_code === 1 &&
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
  const restored = object(record.restored, ["exit_code", "scenario_count"]);
  require(
    restored.exit_code === 0 && typeof restored.scenario_count === "number" &&
      Number.isSafeInteger(restored.scenario_count) && restored.scenario_count > 0,
    "restored suite did not pass",
  );
}

/** Enumerate source/test inputs so additions and deletions also invalidate recorded evidence. */
export async function sourceHashes(root: URL): Promise<SourceHashes> {
  const names: string[] = [];
  const walk = async (directory: URL, prefix: string): Promise<void> => {
    for await (const entry of Deno.readDir(directory)) {
      const name = prefix + entry.name;
      if (
        entry.name.startsWith(".") || name === "dist" || name === "node_modules" ||
        name === "test/evidence"
      ) continue;
      if (entry.isDirectory) await walk(new URL(`${entry.name}/`, directory), `${name}/`);
      else if (entry.isFile && /\.(ts|json|css|html)$/.test(name)) names.push(name);
    }
  };
  await walk(root, "");
  names.push("../deno.json", "../deno.lock");
  const hashes = await Promise.all(
    names.sort().map(async (name) => {
      const bytes = await Deno.readFile(new URL(name, root));
      const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
      return [
        name,
        [...digest].map((byte) => byte.toString(16).padStart(2, "0")).join(""),
      ] as const;
    }),
  );
  return Object.fromEntries(hashes);
}
