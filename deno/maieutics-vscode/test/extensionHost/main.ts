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
import { executeIdeMethod, MaieuticsNotebookController } from "../../controller.ts";
import { QueueProjection } from "../../queueProjection.ts";
import { FrontendClient } from "../../client.ts";
import { readTurnBinding } from "../../cellHistory.ts";
import { MaieuticsFileSystemProvider } from "../../vfs.ts";
import { emptyNotebook } from "../../notebookFormat.ts";
import { readStoredSessionId } from "../../serializer.ts";
import { FrontendError } from "../../protocol.ts";
import { registerCommandCompletion } from "../../completion.ts";

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

/** VS Code stats a session notebook before its first read. Empty sessions are live
 * but absent from the stored-session listing, so stat must consult the session itself. */
async function testFreshSessionVfsAsync(): Promise<void> {
  const sessionId = "e".repeat(32);
  let fetched = 0;
  const provider = new MaieuticsFileSystemProvider({
    listSessions: () => Promise.resolve([]),
    fetchTranscript: (id) => {
      assert(id === sessionId, "stat resolves the addressed session");
      fetched++;
      const notebook = emptyNotebook();
      notebook.session = { serverSessionId: id };
      return Promise.resolve(notebook);
    },
    currentWorkspace: () => ({ caseInsensitive: false }),
  });
  const registration = vscode.workspace.registerFileSystemProvider(
    "maieutics-session-test",
    provider,
  );
  try {
    await provider.refresh();
    const uri = vscode.Uri.from({
      scheme: "maieutics-session-test",
      path: `/sessions/${sessionId}.maieuticsnb`,
    });
    const stat = await vscode.workspace.fs.stat(uri);
    assert(stat.type === vscode.FileType.File, "a fresh zero-turn session exists before readFile");
    const notebook = await vscode.workspace.openNotebookDocument(uri);
    assert(
      readStoredSessionId(notebook.metadata) === sessionId,
      "the opened notebook keeps the fresh session id",
    );
    assert(fetched === 1, "stat and notebook read share the materialized transcript");
    provider.syncSessions([]);
    assert(
      (await vscode.workspace.fs.stat(uri)).type === vscode.FileType.File,
      "a stored-list refresh cannot hide a live view",
    );
  } finally {
    registration.dispose();
  }
}

/** An unreachable server must not be presented as a nonexistent notebook. */
async function testVfsLookupFailureAsync(): Promise<void> {
  for (
    const [status, code, expected] of [
      [404, "not_found", "FileNotFound"],
      [503, "unavailable", "Unavailable"],
    ] as const
  ) {
    const provider = new MaieuticsFileSystemProvider({
      listSessions: () => Promise.resolve([]),
      fetchTranscript: () => Promise.reject(new FrontendError(code, status, "Lookup failed")),
      currentWorkspace: () => ({ caseInsensitive: false }),
    });
    const uri = vscode.Uri.from({
      scheme: "maieutics",
      path: `/sessions/${"f".repeat(32)}.maieuticsnb`,
    });
    let observed: unknown;
    try {
      await provider.stat(uri);
    } catch (error) {
      observed = error;
    }
    assert(
      observed instanceof vscode.FileSystemError && observed.code === expected,
      `VFS preserves the difference between missing and unavailable sessions (${status})`,
    );
  }
}

/** Runs real notebook cell executions against a controlled protocol peer. Admission
 * completes after its run has finished, matching a fast provider / delayed HTTP response. */
async function testQueueExecutionAsync(
  failBeforeRun: boolean,
  supportsRecovery = true,
  missingReplay = false,
): Promise<void> {
  const sessionId = "a".repeat(32);
  const runId = "b".repeat(32);
  const itemId = "queued-item";
  let admitted = false;
  let scopedReplay = false;
  const client = FrontendClient.fromDiscovery({
    version: 1,
    url: "http://127.0.0.1:9",
    token: "test",
    pid: 0,
  });
  client.capabilities = () =>
    Promise.resolve({
      protocolVersion: 1,
      serverVersion: "test",
      multiSession: true,
      session: { id: sessionId, turns: 0, persistenceEnabled: true },
    });
  client.session = () => Promise.resolve({ id: sessionId, turns: 0, persistenceEnabled: true });
  client.enqueueTurns = () => {
    admitted = true;
    return Promise.resolve([{ id: itemId, position: 1 }]);
  };
  client.getQueue = () =>
    Promise.resolve(JSON.parse(JSON.stringify({
      sessionId,
      items: [],
      capacity: 64,
      version: admitted ? 2 : 0,
      outcomes: !supportsRecovery ? undefined : !admitted ? [] : failBeforeRun
        ? [{
          itemId,
          state: "failed",
          error: { code: "configuration_error", message: "No model configured." },
        }]
        : [{ itemId, state: "completed", runId }],
    })));
  client.events = async function* (_sessionId, options = {}) {
    yield { type: "hello" };
    if (options.runId !== undefined) {
      assert(options.runId === runId, "replay targets the retained run identity");
      scopedReplay = true;
      if (missingReplay) {
        yield { type: "run.missing", runId };
        return;
      }
      yield { type: "run.started", runId };
      yield { type: "text.delta", runId, sequence: 1, messageId: "m", text: "Recovered reply" };
      yield { type: "run.completed", runId };
      return;
    }
    if (options.signal?.aborted) return;
    await new Promise<void>((resolve) =>
      options.signal?.addEventListener("abort", () => resolve(), { once: true })
    );
  };
  const id = missingReplay
    ? "queue-missing-test"
    : !supportsRecovery
    ? "queue-upgrade-test"
    : failBeforeRun
    ? "queue-failure-test"
    : "queue-replay-test";
  const output = vscode.window.createOutputChannel(id);
  const owner = new MaieuticsNotebookController(
    {
      client: () => Promise.resolve(client),
      session: () => client.session(),
      fetchObject: () => Promise.resolve(new Uint8Array()),
    },
    output,
    new QueueProjection(),
    id,
  );
  try {
    const data = new vscode.NotebookData([
      new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "Question", "maieutics"),
    ]);
    data.metadata = { maieuticsSessionId: sessionId };
    const notebook = await vscode.workspace.openNotebookDocument("maieutics-notebook", data);
    await vscode.window.showNotebookDocument(notebook);
    await vscode.commands.executeCommand("notebook.selectKernel", { id, extension: ExtensionId });
    const budget = AbortSignal.timeout(15_000);
    let outputChanged: vscode.Disposable | undefined;
    const visibleOutput = new Promise<void>((resolve) => {
      outputChanged = vscode.workspace.onDidChangeNotebookDocument((event) => {
        if (event.notebook !== notebook) return;
        const text = notebook.cellAt(0).outputs.flatMap((entry) => entry.items)
          .map((item) => new TextDecoder().decode(item.data)).join("\n");
        if (
          text.includes(
            missingReplay
              ? "run_missing"
              : !supportsRecovery
              ? "queue_recovery_unavailable"
              : failBeforeRun
              ? "configuration_error"
              : "Recovered reply",
          )
        ) resolve();
      });
    });
    try {
      await Promise.race([
        (async () => {
          await owner.runAsync([notebook.cellAt(0)], notebook);
          await visibleOutput;
        })(),
        new Promise<never>((_, reject) =>
          budget.addEventListener(
            "abort",
            () => reject(new Error("The queue item did not reach a visible terminal output.")),
            { once: true },
          )
        ),
      ]);
      const text = notebook.cellAt(0).outputs.flatMap((entry) => entry.items)
        .map((item) => new TextDecoder().decode(item.data)).join("\n");
      if (missingReplay) {
        assert(text.includes("run_missing"), "expired run replay is an explicit cell failure");
      } else if (!supportsRecovery) {
        assert(
          !admitted && text.includes("queue_recovery_unavailable"),
          "an old kernel must fail visibly before admitting work",
        );
      } else if (failBeforeRun) {
        assert(
          text.includes("configuration_error") && text.includes("No model configured."),
          "a failure before run creation is visible in the cell",
        );
      } else {
        assert(
          scopedReplay && text.includes("Recovered reply"),
          "a completed early run replays into its cell",
        );
        assert(
          readTurnBinding({ metadata: notebook.cellAt(0).metadata, text: "Question" })?.runId ===
            runId,
          "the execution commits the accepted run identity",
        );
      }
    } finally {
      outputChanged?.dispose();
    }
  } finally {
    await owner.dispose();
    output.dispose();
  }
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

  // The TS server publishes diagnostics asynchronously after answering
  // hovers — on a cold CI runner that lag is seconds — so poll the problems
  // answer until the planted error shows up (bounded), then assert on its
  // content: the file must arrive with at least one real diagnostic.
  let problems:
    | { documents: { uri: string; diagnostics: unknown[] }[]; analyzedDocuments: number }
    | undefined;
  let waitedForProblemsMs = 0;
  while (waitedForProblemsMs < HoverTimeoutMs) {
    const attempt = await executeIdeMethod("maieutics/problems", {}) as {
      documents: { uri: string; diagnostics: unknown[] }[];
      analyzedDocuments: number;
    };
    const entry = attempt.documents.find((candidate) => candidate.uri === sampleUri.toString());
    if (entry !== undefined && entry.diagnostics.length > 0) {
      problems = attempt;
      break;
    }
    await delay(HoverPollIntervalMs);
    waitedForProblemsMs += HoverPollIntervalMs;
  }
  if (problems === undefined) {
    throw new Error(
      `maieutics/problems never carried ${sampleUri.toString()} with diagnostics within ${HoverTimeoutMs}ms.\n` +
        `current problems: ${describeProblems()}`,
    );
  }
  assert(
    problems.documents.some((entry) => entry.uri === sampleUri.toString()),
    `problems documents contain ${sampleUri.toString()}`,
  );
  assert(
    (problems.documents.find((entry) => entry.uri === sampleUri.toString())?.diagnostics.length ??
      0) >= 1,
    "the planted type error arrives as a real diagnostic",
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

async function testCommandCompletionAsync(): Promise<void> {
  const cases = [
    { text: "/se", match: "%session", start: 0, filter: "/session", trigger: "/" },
    { text: "%session ", match: "current", start: 9, filter: "current", trigger: " " },
    {
      text: "%model use @ven/dra",
      match: "@ven/dragon",
      start: 11,
      filter: "@ven/dragon",
      trigger: "/",
    },
  ];
  for (const entry of cases) {
    let calls = 0;
    const logs: string[] = [];
    const registration = registerCommandCompletion(
      () =>
        Promise.resolve({
          complete: (text: string, cursor: number) => {
            calls++;
            assert(
              text === entry.text && cursor === text.length,
              "the complete cell and UTF-16 cursor reach the server",
            );
            return Promise.resolve({
              matches: [entry.match],
              tokenStart: entry.start,
              tokenEnd: text.length,
            });
          },
        } as unknown as FrontendClient),
      (message) => logs.push(message),
    );
    try {
      const notebook = await vscode.workspace.openNotebookDocument(
        "maieutics-notebook",
        new vscode.NotebookData([
          new vscode.NotebookCellData(vscode.NotebookCellKind.Code, entry.text, "markdown"),
        ]),
      );
      const cell = notebook.cellAt(0);
      const completions = await vscode.commands.executeCommand<vscode.CompletionList>(
        "vscode.executeCompletionItemProvider",
        cell.document.uri,
        new vscode.Position(0, entry.text.length),
        entry.trigger,
      );
      assert(
        calls > 0,
        `the notebook command provider is invoked for ${JSON.stringify(entry.trigger)}`,
      );
      const item = completions?.items.find((item) =>
        item.label === entry.match && item.range instanceof vscode.Range &&
        item.range.start.character === entry.start
      );
      assert(
        item !== undefined,
        `the candidate replaces the server's complete command token (${logs.join("; ")})`,
      );
      assert(
        (item.filterText ?? item.label) === entry.filter,
        "slash discovery remains visible to the editor's prefix filter",
      );
    } finally {
      registration.dispose();
    }
  }
}

const tests: HostTest[] = [
  { name: "extension is installed and activates", run: testActivationAsync },
  { name: "the .maieuticsnb serializer round-trips the fixture", run: testSerializerAsync },
  { name: "notebook command completion reaches its provider", run: testCommandCompletionAsync },
  { name: "a fresh session notebook survives stat before read", run: testFreshSessionVfsAsync },
  {
    name: "VFS distinguishes missing sessions from unavailable servers",
    run: testVfsLookupFailureAsync,
  },
  {
    name: "a run finished before admission still replies in the cell",
    run: () => testQueueExecutionAsync(false),
  },
  {
    name: "a pre-run queue failure is visible in the cell",
    run: () => testQueueExecutionAsync(true),
  },
  {
    name: "an older kernel requests an upgrade before accepting queued work",
    run: () => testQueueExecutionAsync(true, false),
  },
  {
    name: "expired run replay fails visibly instead of painting an empty success",
    run: () => testQueueExecutionAsync(false, true, true),
  },
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
