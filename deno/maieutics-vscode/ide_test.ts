/// <reference lib="deno.ns" />
import { assert, assertEquals } from "@std/assert";
import {
  collectProblems,
  extractIdeRequest,
  ideCommandArgs,
  ideMethodCommands,
  isIdeMethodSupported,
  normalizedHoverAnswer,
  normalizeHoverContents,
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
