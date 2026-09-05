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
  const stderrTail = tailStderr(child);
  try {
    const discovery = await waitForDiscovery(discoveryPath, child, stderrTail, options.signal);
    return {
      client: FrontendClient.fromDiscovery(discovery),
      dispose: () => disposeOwned(child, discoveryPath),
    };
  } catch (error) {
    child.kill();
    await exited(child);
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
  stderrTail: () => string,
  signal?: AbortSignal,
): Promise<unknown> {
  const deadline = Date.now() + LaunchTimeoutMs;
  while (Date.now() < deadline) {
    if (signal?.aborted) throw new Error("The launch was aborted.");
    const exited = await exitedNow(child);
    if (exited !== undefined) {
      throw new Error(
        `The Maieutics executable exited with code ${exited} before publishing discovery.` +
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
  if (child.exitCode === null && child.signalCode === null) child.kill();
  await exited(child);
  await rm(discoveryPath).catch(() => {});
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
    else child.once("exit", () => resolve());
  });
}

function exitedNow(child: ChildProcess): Promise<number | null> {
  if (child.exitCode !== null || child.signalCode !== null) return Promise.resolve(child.exitCode);
  const exited = new Promise<number | null>((resolve) => {
    child.once("exit", (code) => resolve(code));
  });
  // Poll-free race: whoever settles first wins.
  return Promise.race([exited, Promise.resolve().then(() => child.exitCode)]);
}
