import * as vscode from "vscode";
import { createServer } from "node:http";

function assert(value: unknown, message: string): asserts value {
  if (!value) throw new Error(message);
}

/** Exercises the activated extension's actual clientOf and folder-change handlers over HTTP. */
export async function testProjectRegistrationAsync(root: vscode.Uri): Promise<void> {
  const original = vscode.workspace.workspaceFolders ?? [];
  const bad = vscode.Uri.joinPath(root, "bad-project");
  const added = vscode.Uri.joinPath(root, "added-project");
  const discovery = vscode.Uri.joinPath(root, "registration-discovery.json");
  await vscode.workspace.fs.createDirectory(bad);
  await vscode.workspace.fs.createDirectory(added);
  const commands: string[] = [];
  let capabilities = 0;
  let statuses = 0;
  let markAdded: () => void = () => {};
  const addedRegistered = new Promise<void>((resolve) => {
    markAdded = resolve;
  });
  const server = createServer((request, response) => {
    const reply = (status: number, value: unknown) => {
      response.writeHead(status, { "Content-Type": "application/json" });
      response.end(JSON.stringify(value));
    };
    if (request.headers.authorization !== "Bearer test-registration") return reply(401, {});
    if (request.url === "/v1/agent/capabilities") {
      capabilities++;
      return reply(200, {
        protocolVersion: 1,
        serverVersion: "test",
        workspaceRoot: root.fsPath + "-home",
        session: { id: "a".repeat(32), turns: 0, persistenceEnabled: false },
      });
    }
    if (request.url === "/v1/status") {
      statuses++;
      return reply(200, { markdown: "Kernel remains usable" });
    }
    if (request.url === "/v1/agent/sessions") return reply(200, { sessions: [] });
    if (request.url === "/v1/agent/commands") {
      let body = "";
      request.setEncoding("utf8");
      request.on("data", (chunk: string) => {
        body += chunk;
      });
      request.on("end", () => {
        const text = (JSON.parse(body) as { text: string }).text;
        commands.push(text);
        if (text === `%workspace open ${bad.fsPath}`) {
          reply(400, {
            code: "command_error",
            message: "Persistent project identity mismatch; close the link to recover",
          });
        } else {
          reply(200, { markdown: "registered" });
          if (text === `%workspace open ${added.fsPath}`) markAdded();
        }
      });
      return;
    }
    reply(404, {});
  });
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", reject);
      resolve();
    });
  });
  const address = server.address();
  assert(address !== null && typeof address !== "string", "mock kernel listens on a dynamic port");
  await vscode.workspace.fs.writeFile(
    discovery,
    new TextEncoder().encode(JSON.stringify({
      version: 1,
      url: `http://127.0.0.1:${address.port}`,
      token: "test-registration",
      pid: 0,
    })),
  );
  const settings = vscode.workspace.getConfiguration("maieutics");
  const previous = settings.get<string>("discoveryFile");
  let count = 0;
  const updateFolders = async (remove: number, folders: { uri: vscode.Uri }[]) => {
    let listener: vscode.Disposable | undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await new Promise<void>((resolve, reject) => {
        listener = vscode.workspace.onDidChangeWorkspaceFolders(() => resolve());
        timer = setTimeout(
          () => reject(new Error("folder-change event was not delivered")),
          10_000,
        );
        if (!vscode.workspace.updateWorkspaceFolders(original.length, remove, ...folders)) {
          reject(new Error("folder change rejected"));
        }
      });
    } finally {
      listener?.dispose();
      if (timer !== undefined) clearTimeout(timer);
    }
  };
  let budget: ReturnType<typeof setTimeout> | undefined;
  try {
    await settings.update("discoveryFile", discovery.fsPath, vscode.ConfigurationTarget.Workspace);
    await updateFolders(0, [{ uri: bad }]);
    count = 1;
    // This command reaches the extension's clientOf, whose mount fails persistently.
    await vscode.commands.executeCommand("maieutics.showStatus");
    assert(statuses > 0, "project registration failure must not block the real extension command");
    assert(
      commands.includes(`%workspace open ${bad.fsPath}`),
      "the failure was exercised through clientOf",
    );
    assert(
      capabilities === 1,
      "initialization and project registration share one capabilities request",
    );
    await updateFolders(1, [{ uri: bad }, { uri: added }]);
    count = 2;
    await Promise.race([
      addedRegistered,
      new Promise<never>((_, reject) => {
        budget = setTimeout(() =>
          reject(
            new Error("the extension folder-change handler did not register the added project"),
          ), 10_000);
      }),
    ]);
    // No direct ProjectMounts calls or client retrieval caused that added-project request.
    await vscode.commands.executeCommand("maieutics.showStatus");
    assert(
      statuses >= 2,
      "a failed folder-change registration must not poison later client retrieval",
    );
    assert(
      commands.filter((text) => text === `%workspace open ${added.fsPath}`).length === 1,
      "folder-change and clientOf share successful registrations",
    );
    assert(capabilities === 1, "folder changes and retries do not re-fetch capabilities");
  } finally {
    if (budget !== undefined) clearTimeout(budget);
    try {
      if (count) await updateFolders(count, []);
      await settings.update("discoveryFile", previous, vscode.ConfigurationTarget.Workspace);
      // Reset the actual extension connection. The test host's executable path is deliberately invalid.
      await vscode.commands.executeCommand("maieutics.restartServer").then(
        () => {
          throw new Error("the isolated host unexpectedly launched a real kernel");
        },
        () => {},
      );
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve, reject) =>
        server.close((error) => error ? reject(error) : resolve())
      );
    }
  }
}
