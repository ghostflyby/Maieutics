/// <reference lib="deno.ns" />
import { assert, assertEquals } from "@std/assert";
import {
  type BridgedDirEntry,
  bridgeErrorCode,
  installFsBridge,
  isBridgeInactive,
  toWorkspaceUri,
  WORKSPACE_ROOT_ENV,
} from "./fs_bridge.ts";

const ROOT = "/home/user/ws";

Deno.test("toWorkspaceUri maps workspace-relative and absolute paths to the workspace domain", () => {
  assertEquals(toWorkspaceUri("/home/user/ws/a.ts", ROOT, ROOT), "workspace://local/a.ts");
  assertEquals(toWorkspaceUri("a.ts", ROOT, ROOT), "workspace://local/a.ts");
  assertEquals(toWorkspaceUri("sub/dir/b.ts", ROOT, ROOT), "workspace://local/sub/dir/b.ts");
  assertEquals(toWorkspaceUri("./sub/../c.ts", ROOT, ROOT), "workspace://local/c.ts");
  assertEquals(
    toWorkspaceUri(ROOT, ROOT, ROOT),
    "workspace://local/",
    "the root lists as the root URI",
  );
  assertEquals(
    toWorkspaceUri(new URL("file:///home/user/ws/d%20space.ts"), ROOT, ROOT),
    "workspace://local/d%20space.ts",
  );
});

Deno.test("toWorkspaceUri rejects paths outside the workspace root, including lexical escapes", () => {
  assertEquals(toWorkspaceUri("/elsewhere/x.ts", ROOT, ROOT), null);
  assertEquals(toWorkspaceUri("../outside.ts", ROOT, ROOT), null);
  assertEquals(toWorkspaceUri("/home/user/ws../sneak.ts", ROOT, ROOT), null);
  assertEquals(
    toWorkspaceUri("/home/user/wsthing/a.ts", ROOT, ROOT),
    null,
    "prefix collisions fail",
  );
  assertEquals(toWorkspaceUri("a\u0000b", ROOT, ROOT), null);
});

Deno.test("bridgeErrorCode reads the client's flattened failure envelope", () => {
  assertEquals(
    bridgeErrorCode(new Error("workspace_bridge_inactive: no IDE frontend")),
    "workspace_bridge_inactive",
  );
  assertEquals(
    bridgeErrorCode(new Error("workspace_path_not_found: no such file")),
    "workspace_path_not_found",
  );
  assertEquals(bridgeErrorCode(new Error("no code here")), undefined);
  assertEquals(bridgeErrorCode("not an error"), undefined);
  assert(isBridgeInactive(new Error("workspace_bridge_inactive: fall back")));
  assert(!isBridgeInactive(new Error("workspace_write_conflict: changed")));
  assert(
    isBridgeInactive(new Error("tool_not_found: older kernel")),
    "a kernel predating the script tools also falls back",
  );
});

interface StubDeno {
  cwd(): string;
  readTextFile(path: string | URL): Promise<string>;
  writeTextFile(path: string | URL, data: string | Uint8Array): Promise<void>;
  readDir(path: string | URL): AsyncIterable<BridgedDirEntry>;
}

function stubDeno(nativeRead: (path: string | URL) => Promise<string>): StubDeno {
  return {
    cwd: () => ROOT,
    readTextFile: nativeRead,
    writeTextFile: () => Promise.resolve(),
    readDir: async function* () {},
  };
}

Deno.test("installFsBridge routes workspace reads through the bridge and answers the content", async () => {
  const invoked: Array<[string, Record<string, unknown>]> = [];
  const denoObject = stubDeno(() => Promise.resolve("native"));
  installFsBridge(denoObject, ROOT, (tool, args) => {
    invoked.push([tool, args]);
    return Promise.resolve({ content: "bridged text" });
  });
  const text = await denoObject.readTextFile("/home/user/ws/a.ts");
  assertEquals(text, "bridged text");
  assertEquals(invoked.length, 1);
  assertEquals(invoked[0][0], "workspace_fs_read");
  assertEquals(invoked[0][1], { uri: "workspace://local/a.ts" });
});

Deno.test("installFsBridge falls back per call on the typed inactive bridge", async () => {
  const denoObject = stubDeno(() => Promise.resolve("native"));
  installFsBridge(
    denoObject,
    ROOT,
    () => Promise.reject(new Error("workspace_bridge_inactive: no IDE frontend")),
  );
  assertEquals(await denoObject.readTextFile("/home/user/ws/a.ts"), "native");
});

Deno.test("installFsBridge keeps non-workspace paths and binary payloads native", async () => {
  let calls = 0;
  const denoObject = stubDeno(() => Promise.resolve("native"));
  installFsBridge(denoObject, ROOT, () => {
    calls += 1;
    return Promise.resolve({ content: "" });
  });
  assertEquals(await denoObject.readTextFile("/elsewhere/x.ts"), "native");
  await denoObject.writeTextFile("/home/user/ws/bin.ts", new Uint8Array([1, 2, 3]));
  assertEquals(calls, 0, "binary payloads and outside paths never reach the bridge");
});

Deno.test("installFsBridge routes string writes and bridged listings", async () => {
  const invoked: Array<[string, Record<string, unknown>]> = [];
  const denoObject = stubDeno(() => Promise.resolve("native"));
  installFsBridge(denoObject, ROOT, (tool, args) => {
    invoked.push([tool, args]);
    return Promise.resolve({
      entries: [
        { name: "a.ts", kind: "file", sizeBytes: 3 },
        { name: "sub", kind: "directory", sizeBytes: null },
        { name: "ln", kind: "symbolic_link", sizeBytes: null },
      ],
    });
  });

  await denoObject.writeTextFile("w.ts", "hello");
  assertEquals(invoked[0], ["workspace_fs_write", {
    uri: "workspace://local/w.ts",
    content: "hello",
  }]);

  const entries: BridgedDirEntry[] = [];
  for await (const entry of denoObject.readDir("sub")) entries.push(entry);
  assertEquals(invoked[1][0], "workspace_fs_list");
  assertEquals(invoked[1][1], { uri: "workspace://local/sub" });
  assertEquals(entries, [
    { name: "a.ts", isFile: true, isDirectory: false, isSymlink: false },
    { name: "sub", isFile: false, isDirectory: true, isSymlink: false },
    { name: "ln", isFile: false, isDirectory: false, isSymlink: true },
  ]);
});

Deno.test("installFsBridge falls back to the native listing when the bridge is inactive", async () => {
  const denoObject = stubDeno(() => Promise.resolve("native"));
  denoObject.readDir = async function* (): AsyncIterable<BridgedDirEntry> {
    yield { name: "native.txt", isFile: true, isDirectory: false, isSymlink: false };
  };
  installFsBridge(
    denoObject,
    ROOT,
    () => Promise.reject(new Error("workspace_bridge_inactive: no IDE frontend")),
  );
  const entries: BridgedDirEntry[] = [];
  for await (const entry of denoObject.readDir("sub")) entries.push(entry);
  assertEquals(entries, [{
    name: "native.txt",
    isFile: true,
    isDirectory: false,
    isSymlink: false,
  }]);
});

Deno.test("the workspace root env var name is the kernel's contract", () => {
  assertEquals(WORKSPACE_ROOT_ENV, "MAIEUTICS_WORKSPACE_ROOT");
});
