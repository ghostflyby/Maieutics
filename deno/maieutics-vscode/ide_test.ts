/// <reference lib="deno.ns" />
import { assert, assertEquals } from "@std/assert";
import {
  collectProblems,
  extractIdeRequest,
  ideCommandArgs,
  ideMethodCommands,
  isIdeMethodSupported,
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
  assert(!isIdeMethodSupported("textDocument/rename"));
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
