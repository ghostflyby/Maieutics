/**
 * Extension-host test entry for the real-VSCode extension tests. Bundled by
 * `deno task build:test` into a zero-dependency CJS module
 * (`dist/test/extensionHost.cjs`) that a real VS Code loads through
 * `--extensionTestsPath` and invokes as `exports.run()`; a rejected run makes
 * the host exit non-zero (validated by the /tmp/vscode-spike spike). The
 * tests drive the real extension inside the same kind of host a user session
 * runs in: activation through the extension registry, the `.maieuticsnb`
 * serializer, and the IDE executor (ADR 0042 stage A) against the built-in
 * TypeScript language service.
 *
 * The runner's macOS launch (`open -n -g -j`, see test/vscode_runner.ts)
 * hands the app to Launch Services and cannot observe the exit code, so the
 * finally block of `run()` always writes the verdict — ok, first error,
 * failed names — into `sentinel.json` at the workspace folder root, where the
 * runner polls for it.
 */

import * as vscode from "vscode";
import { executeIdeMethod } from "../../controller.ts";

/** Upper bound on waiting for the built-in TypeScript extension: it
 * activates lazily on first use and starts (or warms) a TS server process,
 * so the first seconds of a fresh window legitimately produce no hover. */
const HoverTimeoutMs = 60_000;
const HoverPollIntervalMs = 1_000;

/** The extension id from package.json (publisher + name). */
const ExtensionId = "ghostflyby.maieutics-vscode";

interface HostTest {
  name: string;
  run: () => Promise<void>;
}

function assert(condition: unknown, label: string): asserts condition {
  if (!condition) throw new Error(`assertion failed: ${label}`);
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** The first workspace folder: the runner opens a scratch directory as the
 * only folder, so test fixtures land there and nowhere else. */
function workspaceRoot(): vscode.Uri {
  const folders = vscode.workspace.workspaceFolders;
  assert(folders !== undefined && folders.length > 0, "a workspace folder is open");
  return folders[0].uri;
}

/** The `.maieuticsnb` fixture: the version-1 shape notebookFormat.ts owns
 * (kind constants at notebookFormat.ts:15). */
const notebookFixture = {
  maieutics: "maieutics-notebook",
  version: 1,
  cells: [{ kind: "agent", text: "hello" }],
};

/** The extension is installed in the host's registry and activates. */
async function testActivationAsync(): Promise<void> {
  const extension = vscode.extensions.getExtension(ExtensionId);
  assert(extension !== undefined, "the maieutics-vscode extension is installed");
  if (!extension.isActive) await extension.activate();
  assert(extension.isActive, "the extension reports isActive after activate()");
}

/** The registered serializer opens a fixture as a Maieutics notebook. */
async function testSerializerAsync(): Promise<void> {
  const uri = vscode.Uri.joinPath(workspaceRoot(), "demo.maieuticsnb");
  await vscode.workspace.fs.writeFile(
    uri,
    new TextEncoder().encode(JSON.stringify(notebookFixture)),
  );
  const notebook = await vscode.workspace.openNotebookDocument(uri);
  assert(
    notebook.notebookType === "maieutics-notebook",
    "opened as a maieutics-notebook document",
  );
  assert(notebook.cellCount === 1, "the notebook has one cell");
  assert(
    notebook.cellAt(0).document.getText() === "hello",
    "the agent cell's text round-trips through the serializer",
  );
}

/** The IDE executor dispatches into the real language services: hover over
 * `answer` comes from the built-in TypeScript extension, and Problems
 * carries the file's diagnostics once the TS server analyzed it. */
async function testIdeExecutorAsync(): Promise<void> {
  const sampleUri = vscode.Uri.joinPath(workspaceRoot(), "sample.ts");
  // Line 2 plants a deterministic type error (number into string) so the
  // Problems assertions have a real diagnostic to find: a perfectly clean
  // file may carry no diagnostics entry at all, which would make "documents
  // contains the file" unfalsifiable.
  const source = "const answer: number = 42;\nexport const mismatch: string = answer;\n";
  await vscode.workspace.fs.writeFile(sampleUri, new TextEncoder().encode(source));
  await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(sampleUri));

  // Position of `answer`'s identifier on line 0 (LSP 0-based).
  const position = { line: 0, character: 8 };
  let hover: unknown;
  let waitedMs = 0;
  while (waitedMs < HoverTimeoutMs) {
    const attempt = await executeIdeMethod("textDocument/hover", {
      uri: sampleUri.toString(),
      position,
    });
    if (hoverArrived(attempt)) {
      hover = attempt;
      break;
    }
    await delay(HoverPollIntervalMs);
    waitedMs += HoverPollIntervalMs;
  }
  if (hover === undefined) {
    throw new Error(
      `textDocument/hover produced no hover within ${HoverTimeoutMs}ms.\n` +
        `installed extensions: ${describeExtensions()}\n` +
        `current problems: ${describeProblems()}`,
    );
  }

  // executeIdeMethod normalizes hover contents to MarkupContent (ADR 0042
  // decision 4): kind is a fixed tag, value the provider's rendered text.
  const first = hoverPayloadOf(hover);
  assert(first !== undefined, "the hover result carries a hover payload");
  const contents = first.contents as { kind?: unknown; value?: unknown } | undefined;
  assert(contents !== undefined, "the hover carries contents");
  assert(
    contents.kind === "markdown" || contents.kind === "plaintext",
    `contents.kind is a MarkupContent kind (got ${String(contents.kind)})`,
  );
  assert(
    typeof contents.value === "string" && contents.value.length > 0,
    "contents.value is a non-empty string",
  );

  const problems = await executeIdeMethod("maieutics/problems", {}) as {
    documents: { uri: string }[];
    analyzedDocuments: number;
  };
  assert(
    problems.documents.some((entry) => entry.uri === sampleUri.toString()),
    `problems documents contain ${sampleUri.toString()} (got ${
      problems.documents.map((entry) => entry.uri).join(", ") || "none"
    })`,
  );
  assert(
    problems.analyzedDocuments >= 1,
    `problems reports at least one analyzed document (got ${problems.analyzedDocuments})`,
  );
}

/** A poll result counts once a hover payload exists: the VS Code provider
 * command answers Hover[], while the executor's documented normalized shape
 * is a single {contents, range} object — either envelope carries the
 * contents contract the assertions below check. */
function hoverArrived(result: unknown): boolean {
  if (Array.isArray(result)) return result.length > 0;
  return typeof result === "object" && result !== null && "contents" in result;
}

/** The hover payload of a result in either envelope. */
function hoverPayloadOf(result: unknown): { contents?: unknown } | undefined {
  return Array.isArray(result) ? result[0] as { contents?: unknown } | undefined : result as {
    contents?: unknown;
  };
}

/** Timeout diagnostics: which extensions the host actually loaded, so a
 * missing built-in TypeScript service is visible in the failure. */
function describeExtensions(): string {
  return vscode.extensions.all
    .map((extension) => `${extension.id}${extension.isActive ? "" : " (inactive)"}`)
    .join(", ");
}

/** Timeout diagnostics: the editor's current diagnostics map, so a TS server
 * that never analyzed the sample is visible in the failure. */
function describeProblems(): string {
  const entries = vscode.languages.getDiagnostics() as [
    uri: { toString(): string },
    diagnostics: unknown,
  ][];
  if (entries.length === 0) return "(no document has diagnostics yet)";
  return entries
    .map(([uri, diagnostics]) =>
      `${uri.toString()}: ${Array.isArray(diagnostics) ? diagnostics.length : 0} diagnostic(s)`
    )
    .join("; ");
}

const tests: HostTest[] = [
  { name: "extension is installed and activates", run: testActivationAsync },
  { name: "the .maieuticsnb serializer round-trips the fixture", run: testSerializerAsync },
  { name: "the IDE executor reaches the real language services", run: testIdeExecutorAsync },
];

/** VS Code's extension-tests contract: the host calls exports.run() and a
 * rejection exits the process non-zero. Tests run sequentially and every
 * failure is reported before the run rejects. Because the runner's
 * `open -g -j` launch cannot observe that exit code, the finally block
 * writes the same verdict into <workspace>/sentinel.json for the runner to
 * poll — on every path, including an unexpected throw outside the loop. */
export async function run(): Promise<void> {
  const failed: string[] = [];
  let firstError: string | null = null;
  try {
    for (const test of tests) {
      try {
        await test.run();
        console.log("  ok -", test.name);
      } catch (error) {
        failed.push(test.name);
        const message = error instanceof Error ? (error.stack ?? error.message) : String(error);
        firstError ??= `${test.name}: ${message}`;
        console.log("  FAIL -", test.name);
        console.log(message);
      }
    }
    if (failed.length > 0) {
      throw new Error(
        `${failed.length} of ${tests.length} extension-host tests failed (${failed.join("; ")})`,
      );
    }
  } catch (error) {
    firstError ??= error instanceof Error ? error.message : String(error);
    throw error;
  } finally {
    await writeSentinelAsync({
      ok: firstError === null && failed.length === 0,
      error: firstError,
      failed,
    });
  }
}

/** The runner's only verdict channel: the sentinel into the workspace folder
 * root. A failed write is logged loudly — the runner will time out, and this
 * log line is the why. */
async function writeSentinelAsync(verdict: {
  ok: boolean;
  error: string | null;
  failed: string[];
}): Promise<void> {
  try {
    await vscode.workspace.fs.writeFile(
      vscode.Uri.joinPath(workspaceRoot(), "sentinel.json"),
      new TextEncoder().encode(JSON.stringify(verdict)),
    );
  } catch (error) {
    console.error("FAILED TO WRITE SENTINEL:", error);
  }
}
