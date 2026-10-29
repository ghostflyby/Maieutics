/// <reference lib="deno.ns" />
import { assertEquals, assertRejects } from "@std/assert";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type { FrontendClient } from "./client.ts";
import { localProjectPaths, ProjectMounts } from "./projectMounts.ts";

const home = join(tmpdir(), "agent-home");
const first = join(tmpdir(), "ide-project");
const second = join(tmpdir(), "second project");

Deno.test("multi-root IDE workspaces register every disk project, including same-name projects", async () => {
  const a = join(tmpdir(), "one", "api");
  const b = join(tmpdir(), "two", "api");
  const commands: string[] = [];
  const mounts = new ProjectMounts(client((text) => {
    commands.push(text);
    return Promise.resolve("");
  }));
  try {
    const paths = localProjectPaths([
      { uri: { scheme: "file", fsPath: a } },
      { uri: { scheme: "maieutics", fsPath: "/sessions" } },
      { uri: { scheme: "file", fsPath: b } },
      { uri: { scheme: "file", fsPath: second } },
    ]);
    await mounts.sync(paths);
    assertEquals(commands, [
      `%workspace open ${a}`,
      `%workspace open ${b}`,
      `%workspace open ${second}`,
    ]);
  } finally {
    await mounts.dispose();
  }
});

Deno.test("a failed project does not prevent later workspace projects from registering", async () => {
  const commands: string[] = [];
  const mounts = new ProjectMounts(client((text) => {
    commands.push(text);
    return text.endsWith(first) ? Promise.reject(new Error("first failed")) : Promise.resolve("");
  }));
  try {
    await assertRejects(() => mounts.sync([first, second]), AggregateError, "first failed");
    assertEquals(commands, [`%workspace open ${first}`, `%workspace open ${second}`]);
  } finally {
    await mounts.dispose();
  }
});

function client(execute: (text: string, signal?: AbortSignal) => Promise<string>) {
  return {
    capabilities: () => Promise.resolve({ workspaceRoot: home }),
    executeCommand: execute,
  } as Pick<FrontendClient, "capabilities" | "executeCommand">;
}

Deno.test("IDE projects register through kernel links without overriding home or granting permissions", async () => {
  const commands: string[] = [];
  const mounts = new ProjectMounts(client((text) => {
    commands.push(text);
    return Promise.resolve("");
  }));
  try {
    await mounts.sync([home, first, first, second]);
    await mounts.sync([first, second]);
    assertEquals(commands, [`%workspace open ${first}`, `%workspace open ${second}`]);
  } finally {
    await mounts.dispose();
  }
});

Deno.test("concurrent project syncs serialize, deduplicate and accept newly added roots", async () => {
  const entered = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  const commands: string[] = [];
  const mounts = new ProjectMounts(client(async (text) => {
    commands.push(text);
    if (commands.length === 1) {
      entered.resolve();
      await release.promise;
    }
    return "";
  }));
  try {
    const a = mounts.sync([first]);
    await entered.promise;
    const b = mounts.sync([first, second]);
    assertEquals(commands, [`%workspace open ${first}`]);
    release.resolve();
    await Promise.all([a, b]);
    assertEquals(commands, [`%workspace open ${first}`, `%workspace open ${second}`]);
  } finally {
    release.resolve();
    await mounts.dispose();
  }
});

Deno.test("failed mounts retry without repeating successful registrations", async () => {
  let fail = true;
  const commands: string[] = [];
  const mounts = new ProjectMounts(client((text) => {
    commands.push(text);
    if (text.endsWith(second) && fail) {
      fail = false;
      return Promise.reject(new Error("mount failed"));
    }
    return Promise.resolve("");
  }));
  try {
    await assertRejects(() => mounts.sync([first, second]), Error, "mount failed");
    await mounts.sync([first, second]);
    assertEquals(commands, [
      `%workspace open ${first}`,
      `%workspace open ${second}`,
      `%workspace open ${second}`,
    ]);
  } finally {
    await mounts.dispose();
  }
});

Deno.test("disposal cancels and joins a pending registration without closing kernel links", async () => {
  const entered = Promise.withResolvers<void>();
  const mounts = new ProjectMounts(client((_text, signal) =>
    new Promise((_resolve, reject) => {
      entered.resolve();
      signal?.addEventListener("abort", () => reject(signal.reason), { once: true });
    })
  ));
  const pending = mounts.sync([first]);
  const rejected = assertRejects(() => pending);
  await entered.promise;
  await mounts.dispose();
  await rejected;
  await assertRejects(() => mounts.sync([second]), Error, "disposed");
});

Deno.test("project paths containing command syntax are unambiguous and multiline paths fail before dispatch", async () => {
  const commands: string[] = [];
  const mounts = new ProjectMounts(client((text) => {
    commands.push(text);
    return Promise.resolve("");
  }));
  try {
    const unusual = join(tmpdir(), "project as alias");
    await mounts.sync([unusual]);
    assertEquals(commands, [`%workspace open ${unusual} as project_as_alias`]);
    await assertRejects(
      () => mounts.sync([join(tmpdir(), "bad\npath")]),
      Error,
      "cannot be represented",
    );
    assertEquals(commands.length, 1);
  } finally {
    await mounts.dispose();
  }
});
