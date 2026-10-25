/// <reference lib="deno.ns" />
import { assert, assertEquals, assertRejects, assertThrows } from "@std/assert";
import {
  type CommandOutput,
  installProcessBridge,
  isSpawnToolUnavailable,
  PROCESS_SANDBOX_ENV,
  type ProcessBridgeDeno,
} from "./process_bridge.ts";

/** A recording stub of the worker's Deno namespace: the original Command is
 * faked natively, the installed wrapper is observed through it. */
function stubDeno() {
  const calls: { tool: string; args: Record<string, unknown> }[] = [];
  const nativeOutput: CommandOutput = {
    success: true,
    code: 0,
    stdout: new TextEncoder().encode("native"),
    stderr: new Uint8Array(),
  };
  const stub = {
    Command: class {
      constructor(
        public readonly command: string,
        public readonly options?: Record<string, unknown>,
      ) {}
      output() {
        return Promise.resolve(nativeOutput);
      }
      spawn() {
        return { native: true };
      }
      outputSync() {
        return nativeOutput;
      }
    },
  };
  return {
    stub: stub as unknown as ProcessBridgeDeno,
    calls,
    nativeOutput,
    invokeWith(
      answer: (tool: string, args: Record<string, unknown>) => Promise<unknown>,
    ) {
      return (tool: string, args: Record<string, unknown>) => {
        calls.push({ tool, args });
        return answer(tool, args);
      };
    },
  };
}

Deno.test("PROCESS_SANDBOX_ENV names the arming variable", () => {
  assertEquals(PROCESS_SANDBOX_ENV, "MAIEUTICS_PROCESS_SANDBOX");
});

Deno.test("install is a no-op unless the mode env arms it", async () => {
  const harness = stubDeno();
  const invoke = harness.invokeWith(() => {
    throw new Error("must not be called");
  });
  for (const mode of [undefined, "", "off", "OFF", "prefer "] as const) {
    installProcessBridge(harness.stub, mode as string | undefined, invoke);
  }

  const result = await new harness.stub.Command("git", { args: ["status"] }).output();
  assertEquals(
    new TextDecoder().decode(result.stdout),
    "native",
    "the un-armed surface is the original API",
  );
  assertEquals(harness.calls.length, 0);
});

Deno.test("an armed output() routes through the kernel spawn tool", async () => {
  const harness = stubDeno();
  const invoke = harness.invokeWith(() =>
    Promise.resolve({ exitCode: 0, stdout: "captured", stderr: "", timedOut: false })
  );
  installProcessBridge(harness.stub, "prefer", invoke);

  const result = await new harness.stub.Command("git", {
    args: ["status", "--short"],
    env: { PATH: "/usr/bin", SECRET: "x" },
  }).output();

  assertEquals(result.success, true);
  assertEquals(result.code, 0);
  assertEquals(new TextDecoder().decode(result.stdout), "captured");
  assertEquals(harness.calls, [{
    tool: "process_spawn",
    args: {
      executable: "git",
      arguments: ["status", "--short"],
      // The cell selects env NAMES only; the kernel supplies policy-allowed values.
      environmentNames: ["PATH", "SECRET"],
    },
  }]);
});

Deno.test("an armed non-zero exit and a kernel denial surface typed", async () => {
  const harness = stubDeno();
  const invoke = harness.invokeWith(() =>
    Promise.resolve({ exitCode: 3, stdout: "", stderr: "boom", timedOut: false })
  );
  installProcessBridge(harness.stub, "require", invoke);

  const failed = await new harness.stub.Command("make").output();
  assertEquals(failed.success, false);
  assertEquals(failed.code, 3);
  assertEquals(new TextDecoder().decode(failed.stderr), "boom");

  const denied = harness.invokeWith(() =>
    Promise.reject(new Error("process_spawn_denied: run policy refuses curl"))
  );
  installProcessBridge(harness.stub, "require", denied);
  const failure = await assertRejects(
    () => new harness.stub.Command("curl").output(),
    Error,
  );
  assert(!isSpawnToolUnavailable(failure));
  assert(failure.message.startsWith("process_spawn_denied:"));
});

Deno.test("a timeout surfaces typed and tool absence falls back to the native API", async () => {
  const harness = stubDeno();
  const timeout = harness.invokeWith(() =>
    Promise.resolve({ exitCode: null, timedOut: true, stdout: "partial", stderr: "" })
  );
  installProcessBridge(harness.stub, "prefer", timeout);
  await assertRejects(
    () => new harness.stub.Command("sleep", { args: ["10"] }).output(),
    Error,
    "process_spawn_timeout",
  );

  // A fresh stub per install: the polyfill captures the current Command as its fallback, so
  // installing twice over one stub would stack wrappers.
  const second = stubDeno();
  const absent = second.invokeWith(() => {
    throw new Error("tool_not_found: older kernel");
  });
  installProcessBridge(second.stub, "prefer", absent);
  const fallback = await new second.stub.Command("git").output();
  assertEquals(new TextDecoder().decode(fallback.stdout), "native");
});

Deno.test("armed spawn/outputSync fail typed; cwd, identity, and inherited stdio are refused", async () => {
  const harness = stubDeno();
  const invoke = harness.invokeWith(() => Promise.resolve({ exitCode: 0, stdout: "", stderr: "" }));
  installProcessBridge(harness.stub, "prefer", invoke);
  const command = new harness.stub.Command("sh");

  assertThrows(() => command.spawn(), Error, "process_spawn_interactive_unsupported");
  assertThrows(() => command.outputSync(), Error, "process_spawn_sync_unsupported");
  await assertRejects(
    () => new harness.stub.Command("sh", { cwd: "/tmp" }).output(),
    Error,
    "process_spawn_cwd_unsupported",
  );
  await assertRejects(
    () => new harness.stub.Command("sh", { uid: 1000 }).output(),
    Error,
    "process_spawn_identity_unsupported",
  );
  await assertRejects(
    () => new harness.stub.Command("sh", { stdout: "inherit" }).output(),
    Error,
    "process_spawn_inherit_unsupported",
  );
  await assertRejects(
    () => new harness.stub.Command("sh", { stdin: "inherit" }).output(),
    Error,
    "process_spawn_stdin_unsupported",
  );
});

Deno.test("a timeout option rides the request as the kill budget", async () => {
  const harness = stubDeno();
  const invoke = harness.invokeWith(() =>
    Promise.resolve({ exitCode: 0, stdout: "", stderr: "", timedOut: false })
  );
  installProcessBridge(harness.stub, "prefer", invoke);

  await new harness.stub.Command("sleep", { args: ["1"], timeout: 2500 }).output();

  assertEquals(harness.calls[0]?.args.timeoutMs, 2500);
  // The timeout error carries the kernel's captured prefix.
  const timedOut = harness.invokeWith(() =>
    Promise.resolve({ exitCode: null, timedOut: true, stdout: "partial", stderr: "" })
  );
  installProcessBridge(harness.stub, "prefer", timedOut);
  const failure = await assertRejects(
    () => new harness.stub.Command("sleep", { args: ["1"] }).output(),
    Error,
    "process_spawn_timeout",
  ) as Error & { stdout?: Uint8Array };
  assertEquals(new TextDecoder().decode(failure.stdout ?? new Uint8Array()), "partial");
});
