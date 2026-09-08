/// <reference lib="deno.ns" />
/**
 * Launch-path tests for connect(): the owned child is only reported as
 * exited when it really exited, and the discovery wait succeeds against a
 * healthy child. Fixtures are POSIX shell scripts; the Deno CI job runs on
 * Ubuntu and local development is macOS/Linux.
 */

import { assert, assertRejects } from "@std/assert";
import { connect } from "./connection.ts";

/** Writes a self-contained fixture executable (a POSIX shell script). */
async function makeExecutable(script: string): Promise<string> {
  const path = await Deno.makeTempFile({ prefix: "maieutics-fixture-", suffix: ".sh" });
  await Deno.writeTextFile(path, `#!/bin/sh\n${script}\n`);
  await Deno.chmod(path, 0o755);
  return path;
}

const fakeDiscovery = '{"version":1,"url":"http://127.0.0.1:9","token":"t","pid":1}';

Deno.test("connect launches, reads discovery from a healthy child, and owns it", async () => {
  // `$2` is the --frontend-discovery path connect() passes.
  const exe = await makeExecutable(`printf '${fakeDiscovery}' > "$2"; exec sleep 5`);
  try {
    const connection = await connect({ executablePath: exe });
    assert(connection.client !== undefined);
    await connection.dispose();
  } finally {
    await Deno.remove(exe).catch(() => {});
  }
});

Deno.test("connect rejects (never hangs) when the executable cannot spawn", async () => {
  // The error event fires asynchronously; the catch path must still settle.
  await assertRejects(
    () => connect({ executablePath: "/nonexistent/maieutics-binary" }),
    Error,
    "could not be started",
  );
});

Deno.test("connect reports a real exit code before discovery", async () => {
  const exe = await makeExecutable("exit 3");
  try {
    const error = await assertRejects(
      () => connect({ executablePath: exe }),
      Error,
      "code 3",
    );
    assert(error.message.includes("before publishing discovery"));
  } finally {
    await Deno.remove(exe).catch(() => {});
  }
});

Deno.test("connect reports a signal kill before discovery", async () => {
  const exe = await makeExecutable("kill -9 $$");
  try {
    const error = await assertRejects(
      () => connect({ executablePath: exe }),
      Error,
      "signal SIGKILL",
    );
    assert(error.message.includes("before publishing discovery"));
  } finally {
    await Deno.remove(exe).catch(() => {});
  }
});
