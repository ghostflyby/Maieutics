/**
 * Owns the lifecycle of the executable a notebook talks to. Two connection
 * modes:
 *
 * - launch (default): spawn the `maieutics` executable with
 *   `--frontend-discovery <path>`, wait for the discovery file to appear (the
 *   readiness signal), and build a client from it. The spawned process is
 *   owned: disposing the handle stops it and removes the discovery file.
 * - attach: read a discovery file from `maieutics.discoveryFile` when the
 *   executable is launched outside the extension. The process is not owned.
 */

import { randomUUID } from "node:crypto";
import { type ChildProcess, spawn } from "node:child_process";
import { readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { FrontendClient } from "./client.ts";

const LaunchTimeoutMs = 20_000;
const PollIntervalMs = 50;
/** Bounded stderr tail kept for launch-failure diagnostics. */
const StderrTailBytes = 8 * 1024;
/** Grace budget for the SIGTERM stop before escalating to SIGKILL. */
const StopGraceMs = 3_000;

export interface ConnectionOptions {
  executablePath: string;
  workspaceRoot?: string;
  /** Attach to an externally launched executable through this discovery file. */
  discoveryFile?: string;
  signal?: AbortSignal;
}

export interface Connection {
  client: FrontendClient;
  dispose(): Promise<void>;
}

export async function connect(options: ConnectionOptions): Promise<Connection> {
  if (options.discoveryFile) return await attach(options.discoveryFile, options.signal);

  const discoveryPath = join(tmpdir(), `maieutics-vscode-discovery-${randomUUID()}.json`);
  const args = ["--frontend-discovery", discoveryPath];
  if (options.workspaceRoot) args.push("--workspace", options.workspaceRoot);
  const child = spawn(options.executablePath, args, {
    stdio: ["ignore", "ignore", "pipe"],
    windowsHide: true,
  });
  // Spawn failures (ENOENT etc.) surface here, not through "exit". The
  // watcher is read through a getter because the event fires asynchronously,
  // after this synchronous setup returns.
  let spawnError: Error | undefined;
  child.on("error", (error) => {
    spawnError = error;
  });
  const stderrTail = tailStderr(child);
  try {
    const discovery = await waitForDiscovery(discoveryPath, child, () => spawnError, stderrTail, {
      executablePath: options.executablePath,
    }, options.signal);
    return {
      client: FrontendClient.fromDiscovery(discovery),
      dispose: () => disposeOwned(child, discoveryPath),
    };
  } catch (error) {
    await stopChild(child);
    throw error;
  }
}

async function attach(discoveryPath: string, signal?: AbortSignal): Promise<Connection> {
  const discovery = await readDiscovery(discoveryPath, signal);
  return {
    client: FrontendClient.fromDiscovery(discovery),
    dispose: async () => {},
  };
}

async function waitForDiscovery(
  path: string,
  child: ChildProcess,
  spawnError: () => Error | undefined,
  stderrTail: () => string,
  options: { executablePath: string },
  signal?: AbortSignal,
): Promise<unknown> {
  const deadline = Date.now() + LaunchTimeoutMs;
  while (Date.now() < deadline) {
    if (signal?.aborted) throw new Error("The launch was aborted.");
    const spawnFailure = spawnError();
    if (spawnFailure !== undefined) {
      throw new Error(
        `The Maieutics executable '${options.executablePath}' could not be started: ${spawnFailure.message}. ` +
          "GUI-launched VS Code does not inherit a shell profile, so a bare executable name only works if its " +
          "directory is on the system-wide PATH — set maieutics.executablePath to an absolute path if needed." +
          (stderrTail() ? `\nstderr:\n${stderrTail()}` : ""),
      );
    }

    const exited = exitNow(child);
    if (exited !== undefined) {
      throw new Error(
        `The Maieutics executable '${options.executablePath}' exited (${describeExit(exited)}) ` +
          "before publishing discovery." +
          (stderrTail() ? `\nstderr:\n${stderrTail()}` : ""),
      );
    }

    try {
      return await readDiscovery(path, signal);
    } catch {
      await new Promise((resolve) => setTimeout(resolve, PollIntervalMs));
    }
  }

  throw new Error(
    "The Maieutics executable did not publish a discovery file in time." +
      (stderrTail() ? `\nstderr:\n${stderrTail()}` : ""),
  );
}

async function readDiscovery(path: string, signal?: AbortSignal): Promise<unknown> {
  const data = await readFile(path).catch(() => null);
  if (data === null) throw new Error("No discovery file yet.");
  signal?.throwIfAborted();
  return JSON.parse(new TextDecoder().decode(data));
}

async function disposeOwned(child: ChildProcess, discoveryPath: string): Promise<void> {
  await stopChild(child);
  await rm(discoveryPath).catch(() => {});
}

/** Stops an owned child: SIGTERM, one grace budget, then SIGKILL escalation.
 * A child whose spawn never succeeded has no process to stop (its "error"
 * already fired and "exit" will never come). */
async function stopChild(child: ChildProcess): Promise<void> {
  if (child.pid === undefined) return;
  child.kill();
  const stopped = await Promise.race([
    exited(child).then(() => true),
    new Promise<false>((resolve) => setTimeout(() => resolve(false), StopGraceMs)),
  ]);
  if (!stopped && exitNow(child) === undefined) {
    child.kill("SIGKILL");
    await exited(child);
  }
}

function tailStderr(child: ChildProcess): () => string {
  const chunks: Uint8Array[] = [];
  let length = 0;
  child.stderr?.on("data", (chunk: Uint8Array) => {
    chunks.push(chunk);
    length += chunk.byteLength;
    while (length > StderrTailBytes && chunks.length > 1) {
      const first = chunks[0]!;
      length -= first.byteLength;
      chunks.shift();
    }
  });
  return () => new TextDecoder().decode(Buffer.concat(chunks));
}

function exited(child: ChildProcess): Promise<void> {
  return new Promise((resolve) => {
    if (child.exitCode !== null || child.signalCode !== null) resolve();
    else {
      child.once("exit", () => resolve());
      // A failed spawn never emits "exit"; its "error" is the end marker.
      child.once("error", () => resolve());
    }
  });
}

/** Synchronous liveness snapshot: undefined while the process is running,
 * otherwise how it ended. A null code means it was killed by `signal`. */
function exitNow(
  child: ChildProcess,
): { code: number | null; signal: string | null } | undefined {
  if (child.exitCode === null && child.signalCode === null) return undefined;
  return { code: child.exitCode, signal: child.signalCode };
}

function describeExit(exit: { code: number | null; signal: string | null }): string {
  return exit.code !== null ? `code ${exit.code}` : `signal ${exit.signal ?? "unknown"}`;
}
