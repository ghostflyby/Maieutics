/// <reference lib="deno.ns" />
import { assert, assertEquals } from "@std/assert";
import {
  buildRgArgs,
  collectProblems,
  defaultTreeSearchBounds,
  dirtyBufferAnswer,
  extractIdeRequest,
  ideCommandArgs,
  ideMethodCommands,
  isCoveredLocalTarget,
  isIdeMethodSupported,
  localFsEntriesAnswer,
  localFsError,
  mapLocalFileTypeKind,
  mapLocalFsErrorCode,
  normalizedHoverAnswer,
  normalizeHoverContents,
  parseLaunchConfigurations,
  parseRgMatchEvent,
  passesSearchFilters,
  searchTree,
  type SearchTreeFs,
  sha256Hex,
  transitionDebugSession,
} from "./ide.ts";

const progressFrame = (value: unknown) => ({
  type: "tool.progress",
  runId: "r1",
  sequence: 7,
  callId: "c1",
  content: { kind: "json", value },
});

Deno.test("extractIdeRequest pulls the payload out of a tool.progress frame", () => {
  const request = extractIdeRequest(progressFrame({
    ideRequest: {
      requestId: "ide-abc",
      method: "textDocument/hover",
      params: { uri: "file:///x" },
    },
  }));
  assert(request !== undefined);
  assertEquals(request.requestId, "ide-abc");
  assertEquals(request.method, "textDocument/hover");
  assertEquals(request.params, { uri: "file:///x" });
});

Deno.test("extractIdeRequest tolerates params absence and rejects wrong shapes", () => {
  const bare = extractIdeRequest(
    progressFrame({ ideRequest: { requestId: "i", method: "workspace/symbol" } }),
  );
  assert(bare !== undefined);
  assertEquals(bare.params, {});

  assertEquals(extractIdeRequest({ type: "tool.started" }), undefined);
  assertEquals(
    extractIdeRequest(progressFrame({ ideRequest: { requestId: 3, method: "x" } })),
    undefined,
  );
  assertEquals(extractIdeRequest(progressFrame({ other: true })), undefined);
});

Deno.test("the read catalog maps every method to a vscode command without writes", () => {
  assertEquals(ideMethodCommands["textDocument/hover"], "vscode.executeHoverProvider");
  assertEquals(ideMethodCommands["maieutics/problems"], undefined, "problems is special-cased");
  assert(isIdeMethodSupported("maieutics/problems"));
  assert(isIdeMethodSupported("textDocument/semanticTokens/full"));
  // Stage C: the write-group commands dispatch compute-only — this side
  // never applies; the kernel's gated write path is the only writer.
  assert(isIdeMethodSupported("textDocument/rename"));
  assert(isIdeMethodSupported("textDocument/codeAction"));
  assert(isIdeMethodSupported("textDocument/formatting"));
  assert(!isIdeMethodSupported("workspace/executeCommand"));
  assert(!isIdeMethodSupported("vscode.open"));
});

Deno.test("ideCommandArgs builds each provider command shape", () => {
  const params = {
    uri: "file:///a.ts",
    position: { line: 3, character: 7 },
    context: { includeDeclaration: false },
    range: { start: { line: 0, character: 0 }, end: { line: 1, character: 2 } },
    positions: [{ line: 1, character: 1 }],
    query: "Main",
    item: { uri: "file:///a.ts", name: "f" },
  };

  assertEquals(ideCommandArgs("textDocument/hover", params), ["file:///a.ts", {
    line: 3,
    character: 7,
  }]);
  assertEquals(ideCommandArgs("textDocument/references", params), [
    "file:///a.ts",
    { line: 3, character: 7 },
    { includeDeclaration: false },
  ]);
  assertEquals(
    ideCommandArgs("textDocument/references", {
      uri: "file:///a.ts",
      position: { line: 0, character: 0 },
    }),
    ["file:///a.ts", { line: 0, character: 0 }, { includeDeclaration: true }],
    "references defaults to including the declaration",
  );
  assertEquals(ideCommandArgs("textDocument/inlayHint", params), [
    "file:///a.ts",
    params.range,
  ]);
  assertEquals(ideCommandArgs("textDocument/selectionRange", params), [
    "file:///a.ts",
    [{ line: 1, character: 1 }],
  ]);
  assertEquals(ideCommandArgs("workspace/symbol", params), ["Main"]);
  assertEquals(ideCommandArgs("callHierarchy/incomingCalls", params), [params.item]);
  assertEquals(ideCommandArgs("textDocument/documentSymbol", { uri: "file:///a.ts" }), [
    "file:///a.ts",
  ]);
});

Deno.test("normalizeHoverContents flattens every VS Code contents shape", () => {
  assertEquals(normalizeHoverContents("plain"), { kind: "plaintext", value: "plain" });
  assertEquals(normalizeHoverContents({ value: "doc" }), { kind: "markdown", value: "doc" });
  assertEquals(normalizeHoverContents({ language: "ts", value: "const x" }), {
    kind: "markdown",
    value: "```ts\nconst x\n```",
  });
  assertEquals(normalizeHoverContents([{ value: "a" }, "b", { language: "cs", value: "int" }]), {
    kind: "markdown",
    value: "a\n\n```cs\nint\n```",
  });
  assertEquals(normalizeHoverContents(undefined), { kind: "plaintext", value: "" });
});

Deno.test("normalizedHoverAnswer folds the provider command's Hover[] into one answer", () => {
  // The real vscode.executeHoverProvider envelope: one Hover per provider.
  assertEquals(
    normalizedHoverAnswer([
      { contents: { value: "const answer: number" }, range: {} },
      { contents: "second provider" },
    ]),
    { contents: { kind: "markdown", value: "const answer: number" }, range: {} },
  );
  // A lone hover-shaped object normalizes the same way.
  assertEquals(
    normalizedHoverAnswer({ contents: [{ value: "a" }, { language: "ts", value: "x" }] }),
    { contents: { kind: "markdown", value: "a\n\n```ts\nx\n```" } },
  );
  // A range-absent hover keeps the answer range-absent.
  const ranged = normalizedHoverAnswer([{ contents: "plain text" }]);
  assertEquals(ranged, { contents: { kind: "plaintext", value: "plain text" } });
  assertEquals("range" in (ranged ?? {}), false);
});

Deno.test("normalizedHoverAnswer stays undefined until a hover carries contents", () => {
  // The shapes a polling caller sees before the language service answers.
  assertEquals(normalizedHoverAnswer([]), undefined);
  assertEquals(normalizedHoverAnswer(undefined), undefined);
  assertEquals(normalizedHoverAnswer(null), undefined);
  assertEquals(normalizedHoverAnswer([42, "no contents here"]), undefined);
});

Deno.test("collectProblems filters to the workspace and counts tracked documents", () => {
  const uriOf = (path: string) => ({ fsPath: path, toString: () => `file://${path}` });
  const entries = [
    [uriOf("/w/repo/a.ts"), [{
      severity: 0,
      range: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } },
      message: "e",
      source: "ts",
    }]],
    [uriOf("/outside/b.ts"), [{
      severity: 1,
      range: { start: { line: 1, character: 0 }, end: { line: 1, character: 1 } },
      message: "w",
    }]],
    [uriOf("/w/repo/clean.ts"), []],
  ] as unknown as Parameters<typeof collectProblems>[0];

  const answer = collectProblems(entries, undefined, ["/w/repo"]);
  assertEquals(answer.analyzedDocuments, 3);
  assertEquals(answer.documents.length, 2, "the out-of-workspace document is dropped");
  assertEquals(answer.documents[0].uri, "file:///w/repo/a.ts");
  assertEquals(answer.documents[0].diagnostics.length, 1);
  assertEquals(answer.documents[0].diagnostics[0].code, undefined);

  const one = collectProblems(entries, "file:///w/repo/a.ts", ["/w/repo"]);
  assertEquals(one.documents.length, 1);
  assertEquals(one.documents[0].diagnostics[0].message, "e");
});

Deno.test("extractIdeRequest reads the standalone script/plugin frame", () => {
  const request = extractIdeRequest(
    {
      type: "ide.request",
      data: { requestId: "ide-xyz", method: "textDocument/hover", params: { uri: "file:///x" } },
    } as unknown as Parameters<typeof extractIdeRequest>[0],
  );
  assert(request !== undefined);
  assertEquals(request.requestId, "ide-xyz");
  assertEquals(request.method, "textDocument/hover");
  assertEquals(request.params, { uri: "file:///x" });
});

// ---------- stage F: workspace file-system forwarding (decision 12) ----------

Deno.test("mapLocalFileTypeKind resolves directory and symlink bits like the local listing", () => {
  assertEquals(mapLocalFileTypeKind(1), "file");
  assertEquals(mapLocalFileTypeKind(2), "directory");
  assertEquals(mapLocalFileTypeKind(64), "symbolic_link");
  assertEquals(
    mapLocalFileTypeKind(1 | 64),
    "symbolic_link",
    "a symlinked file keeps the link kind",
  );
  assertEquals(
    mapLocalFileTypeKind(2 | 64),
    "directory",
    "a symlinked directory lists as a directory",
  );
  assertEquals(mapLocalFileTypeKind(0), "other");
});

Deno.test("localFsEntriesAnswer sorts, bounds, and reports truncation", () => {
  const answer = localFsEntriesAnswer(
    [["z.ts", 1], ["a", 2], ["m.md", 1], ["link", 64]],
    3,
  );
  assertEquals(answer.truncated, true);
  assertEquals(answer.entries.length, 3);
  assertEquals(answer.entries.map((entry) => entry.name), ["a", "link", "m.md"]);
  assertEquals(answer.entries[0].kind, "directory");
  assertEquals(answer.entries[1].kind, "symbolic_link");

  const exact = localFsEntriesAnswer([["a", 1]], 3);
  assertEquals(exact.truncated, false);
  assertEquals(exact.entries.length, 1);

  const empty = localFsEntriesAnswer([], 0);
  assertEquals(empty.truncated, false);
  assertEquals(empty.entries, []);
});

Deno.test("mapLocalFsErrorCode covers both fs families' error surfaces", () => {
  assertEquals(mapLocalFsErrorCode("FileNotFound"), "fs_not_found");
  assertEquals(mapLocalFsErrorCode("FileIsADirectory"), "fs_is_directory");
  assertEquals(mapLocalFsErrorCode("FileNotADirectory"), "fs_not_directory");
  assertEquals(mapLocalFsErrorCode("FileExists"), "fs_exists");
  assertEquals(mapLocalFsErrorCode("NoPermissions"), "fs_access_denied");
  assertEquals(mapLocalFsErrorCode("Unavailable"), "fs_scheme_unreadable");
  assertEquals(mapLocalFsErrorCode(undefined), "fs_failed");
});

Deno.test("localFsError strips the physical target from the message", () => {
  const error = localFsError(
    Object.assign(new Error("ENOENT: /home/user/secret/a.ts"), { code: "FileNotFound" }),
  );
  assertEquals((error as { code?: string }).code, "fs_not_found");
  assert(!error.message.includes("/home/user"), "the physical target never crosses");
});

Deno.test("sha256Hex matches the known SHA-256 vectors the write precondition hashes", async () => {
  assertEquals(
    await sha256Hex(""),
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  );
  assertEquals(
    await sha256Hex("class C {}\n"),
    "8a7eaee3a2ed1ded2f10c111143f7629820f78b4e63210aebe6b9986850db4ac",
  );
  assertEquals(
    await sha256Hex("hello"),
    "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
  );
});

Deno.test("isCoveredLocalTarget gates the local fs family to the window's own folders", () => {
  const roots = ["/home/user/ws"];
  assertEquals(isCoveredLocalTarget("/home/user/ws/a.ts", roots), true);
  assertEquals(
    isCoveredLocalTarget("/home/user/ws", roots),
    true,
    "the folder root itself is covered",
  );
  assertEquals(isCoveredLocalTarget("/home/user/ws/sub/deep/b.ts", roots), true);
  assertEquals(
    isCoveredLocalTarget("/home/user/wsthing/a.ts", roots),
    false,
    "prefix collisions fail",
  );
  assertEquals(isCoveredLocalTarget("/home/user/ws../a.ts", roots), false);
  assertEquals(isCoveredLocalTarget("/elsewhere/a.ts", roots), false);
  assertEquals(
    isCoveredLocalTarget("/home/user/WS/A.TS", roots),
    true,
    "case mismatches over-match harmlessly",
  );
  assertEquals(
    isCoveredLocalTarget("/home/user/ws/a.ts", []),
    false,
    "a window with no folders covers nothing",
  );
  assertEquals(isCoveredLocalTarget("/home/user/ws/a.ts", ["C:\\other\\proj"]), false);
});

Deno.test("dirtyBufferAnswer serves the virtual family's editor semantics", () => {
  const documents = [
    {
      uriString: "vscode-remote://ssh-remote+host/home/u/proj/a.ts",
      isDirty: true,
      text: "unsaved remote edit",
    },
    {
      uriString: "vscode-remote://ssh-remote+host/home/u/proj/b.ts",
      isDirty: false,
      text: "clean model text",
    },
    { uriString: "untitled:Untitled-1", isDirty: true, text: "never addressable" },
  ];

  assertEquals(
    dirtyBufferAnswer(documents, "vscode-remote://ssh-remote+host/home/u/proj/a.ts"),
    "unsaved remote edit",
    "a dirty remote document answers its buffer",
  );
  assertEquals(
    dirtyBufferAnswer(documents, "vscode-remote://ssh-remote+host/home/u/proj/b.ts"),
    undefined,
    "a clean document falls through to the provider bytes",
  );
  assertEquals(
    dirtyBufferAnswer(documents, "vscode-remote://ssh-remote+host/home/u/proj/missing.ts"),
    undefined,
  );
  // The untitled document in the list can never answer: targets are always
  // folder-derived URIs, so that entry matches nothing above.
});

Deno.test("searchTree walks the provider tree with the search_text contract", async () => {
  const tree: Record<string, Array<[string, number]>> = {
    "root://src": [["a.ts", 1], ["nested", 2], ["skip.log", 1]],
    "root://src/nested": [["b.ts", 1], ["link", 64]],
  };
  const files: Record<string, string> = {
    "root://src/a.ts": "alpha needle beta\nplain line\n",
    "root://src/nested/b.ts": "x\nNEEDLE here\n",
    "root://src/skip.log": "no match here\n",
  };
  const fs: SearchTreeFs = {
    list: (path) => Promise.resolve(tree[path] ?? []),
    read: (path) => Promise.resolve(new TextEncoder().encode(files[path] ?? "")),
    join: (base, segments) => `${base}/${segments.join("/")}`,
  };

  const literal = await searchTree(
    fs,
    "root://src",
    defaultTreeSearchBounds("needle", false, true, 200),
  );
  assertEquals(literal.truncated, false);
  assertEquals(literal.matches.length, 1, "case-sensitive literal matches only the exact case");
  assertEquals(literal.matches[0], {
    path: "a.ts",
    line: 1,
    column: 7,
    preview: "alpha needle beta",
  });

  const insensitive = await searchTree(
    fs,
    "root://src",
    defaultTreeSearchBounds("needle", false, false, 200),
  );
  assertEquals(insensitive.matches.length, 2, "case folding finds the folded buffer");

  const regex = await searchTree(
    fs,
    "root://src",
    defaultTreeSearchBounds("n[e]ed\\w+", true, true, 200),
  );
  assertEquals(regex.matches.length, 1, "the regex path rides the same contract");
});

Deno.test("searchTree bounds turn into truncation and skip binary files", async () => {
  const tree: Record<string, Array<[string, number]>> = {
    "root://w": [["bin.dat", 1], ["big.ts", 1], ["hit.ts", 1], ["oversize.ts", 1]],
  };
  const files: Record<string, string> = {
    "root://w/bin.dat": "needle",
    "root://w/big.ts": "needle\n",
    "root://w/hit.ts": "needle\n",
    "root://w/oversize.ts": "needle and far past the per-file cap\n",
  };
  const fs: SearchTreeFs = {
    list: (path) => Promise.resolve(tree[path] ?? []),
    read: (path) => {
      if (path.endsWith("bin.dat")) {
        return Promise.resolve(new Uint8Array([0x00, 0x01, 0x02]));
      }
      return Promise.resolve(new TextEncoder().encode(files[path] ?? ""));
    },
    join: (base, segments) => `${base}/${segments.join("/")}`,
  };

  const bounded = await searchTree(fs, "root://w", {
    query: "needle",
    regex: false,
    caseSensitive: true,
    maximumMatches: 10,
    maximumFileBytes: 16,
    maximumBytes: 1_000_000,
    maximumFiles: 100,
    maximumEntries: 100,
    budgetMs: 10_000,
  });
  assertEquals(bounded.truncated, true, "the per-file cap on oversize.ts truncates");
  assertEquals(
    bounded.matches.map((match) => match.path),
    ["big.ts", "hit.ts"],
    "the binary file and the oversize file are skipped, the scan continues",
  );

  const empty = await searchTree(
    fs,
    "root://missing",
    defaultTreeSearchBounds("x", false, true, 200),
  );
  assertEquals(empty.matches, []);
  assertEquals(empty.truncated, false, "an unreadable root scans to nothing, not a truncation");
});

Deno.test("parseRgMatchEvent maps rg's byte offsets onto character columns", () => {
  // "const é = 1; needle" — the needle starts at byte 14 but character 12.
  const line = "const é = 1; needle\n";
  const lineBytes = new TextEncoder().encode(line);
  const needle = new TextEncoder().encode("needle");
  let needleByteOffset = -1;
  for (let index = 0; index + needle.length <= lineBytes.length; index += 1) {
    if (needle.every((byte, position) => lineBytes[index + position] === byte)) {
      needleByteOffset = index;
      break;
    }
  }
  if (needleByteOffset < 0) throw new Error("fixture: the needle is not in the line");
  const event = JSON.stringify({
    type: "match",
    data: {
      path: { text: "./src/é.ts" },
      lines: { text: line },
      line_number: 3,
      submatches: [{
        match: { text: "needle" },
        start: needleByteOffset,
        end: needleByteOffset + 5,
      }],
    },
  });
  const match = parseRgMatchEvent(event);
  assertEquals(match, {
    path: "src/é.ts",
    line: 3,
    column: 14,
    preview: "const é = 1; needle",
  });

  assertEquals(parseRgMatchEvent('{"type":"begin","data":{}}'), null);
  assertEquals(parseRgMatchEvent("not json"), null);
});

Deno.test("buildRgArgs composes the default posture and the caller's filters", () => {
  assertEquals(
    buildRgArgs("needle", false, true, {}),
    ["--json", "--hidden", "-g", "!.git", "--fixed-strings", "-e", "needle", "."],
  );
  assertEquals(
    buildRgArgs("foo\\d+", true, false, {
      respectIgnore: false,
      include: ["**/*.ts"],
      exclude: ["generated/**"],
    }),
    [
      "--json",
      "--hidden",
      "-g",
      "!.git",
      "--no-ignore",
      "-i",
      "--regexp",
      "-e",
      "foo\\d+",
      "-g",
      "**/*.ts",
      "-g",
      "!generated/**",
      ".",
    ],
  );
});

Deno.test("passesSearchFilters applies the gitignore naming rule", () => {
  const filter = { include: ["**/*.ts"], exclude: ["generated/**"] };
  assertEquals(passesSearchFilters("src/a.ts", filter), true);
  assertEquals(passesSearchFilters("src/gen/b.ts", filter), true, "exclude needs the /** tail");
  assertEquals(passesSearchFilters("generated/x.ts", filter), false);
  assertEquals(passesSearchFilters("src/a.js", filter), false, "include is a whitelist");

  const name = { exclude: ["node_modules"] };
  assertEquals(
    passesSearchFilters("a/node_modules/x.js", name),
    false,
    "a slash-free glob matches the basename at any depth",
  );
  assertEquals(passesSearchFilters("a/modules/x.js", name), true);

  assertEquals(passesSearchFilters("anything.go", {}), true, "no filters passes everything");
});

Deno.test("stripJsonComments survives strings and trailing commas", () => {
  const jsonc = `{
    // launch config
    "version": "0.2.0",
    "configurations": [
      { "name": "launch: http://x", "type": "node", "request": "launch", /* inline */ },
      { "name": "attach", "type": "node", "request": "attach", },
    ],
  }`;
  const configs = parseLaunchConfigurations(jsonc);
  assertEquals(configs.length, 2);
  assertEquals(configs[0].name, "launch: http://x", "a `//` inside a string survives");
  assertEquals(configs[1].request, "attach");
  assertEquals(configs[0].configuration.type, "node", "the full object rides along");
});

Deno.test("parseLaunchConfigurations degrades to empty on garbage", () => {
  assertEquals(parseLaunchConfigurations("not json {"), []);
  assertEquals(parseLaunchConfigurations('{"version": "0.2.0"}'), []);
  assertEquals(
    parseLaunchConfigurations('{"configurations": [{"name": 1}]}'),
    [],
    "entries missing the required string fields drop",
  );
});

Deno.test("transitionDebugSession follows the debug state machine", () => {
  let session = transitionDebugSession(undefined, { kind: "start" });
  assertEquals(session.state, "running");
  const runningVersion = session.version;

  session = transitionDebugSession(session, {
    kind: "stopped",
    reason: "breakpoint",
    threadId: 7,
  });
  assertEquals(session.state, "paused");
  assertEquals(session.reason, "breakpoint");
  assertEquals(session.threadId, 7);
  if (session.version <= runningVersion) throw new Error("version must advance");

  session = transitionDebugSession(session, { kind: "resumed" });
  assertEquals(session.state, "running");

  session = transitionDebugSession(session, { kind: "terminated" });
  assertEquals(session.state, "terminated");

  const first = transitionDebugSession(undefined, { kind: "start" });
  assertEquals(
    transitionDebugSession(first, { kind: "resumed" as never }).version,
    first.version + 1,
    "every transition bumps the version so waits wake",
  );
});
