/// <reference lib="deno.ns" />
/**
 * Real-VSCode extension test runner. Resolves a VS Code install — env
 * override (MAIEUTICS_VSCODE_PATH, normalized to the .app bundle), then
 * `realpath(which code)` walked up to its bundle, then the platform default
 * install, then a download through `@vscode/test-electron` (the CI shape) —
 * launches it against the bundled `dist/test/extensionHost.cjs` entry with
 * isolated user-data/extensions directories and a scratch workspace folder,
 * and reads the verdict from the sentinel file the test entry writes into
 * that folder. `deno task test:vscode` wires the builds and this runner
 * together.
 *
 * Launch contract, pinned empirically on this machine and not to be
 * substituted:
 * - macOS launches through `open -n -g -j <App.app> --args <flags>
 *   <workspace>`. Starting the Electron binary directly pops a window that
 *   steals focus, and the `code` CLI wrapper is only supported while no other
 *   instance runs and detaches anyway (it exits 0 after ~1s without waiting
 *   or carrying the child's exit code). `-n` forces a separate instance so
 *   the arguments are not folded into an editor the user already has open,
 *   `-g` leaves the foreground alone, `-j` hides the window — the extension
 *   activates and the sentinel lands even in the hidden instance. `open`
 *   cannot carry the child's exit code, so the verdict is the
 *   `<workspace>/sentinel.json` the entry writes in its finally block
 *   (test/extensionHost/main.ts).
 * - Linux (CI) starts the binary directly, wrapped in an external
 *   `xvfb-run -a` when one is installed — the exit code is reliable there;
 *   the sentinel is still the primary verdict and doubles as diagnostics.
 *   Windows starts the binary directly.
 * - After the verdict (timeout included), `pkill -f <user-data-dir>` reaps
 *   exactly this run's instance — the pattern is the run's unique scratch
 *   path, so a user's own VS Code is never touched.
 */

import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

/** The entry's verdict file, polled where the runner created the workspace. */
const SentinelFileName = "sentinel.json";

/** The runner polls the sentinel at this cadence. */
const PollIntervalMs = 1_000;

/** Platform-default VS Code installs, probed before download mode; any other
 * platform goes straight to download. */
const PlatformDefaults: Partial<Record<string, string>> = {
  darwin: "/Applications/Visual Studio Code.app",
  linux: "/usr/share/code/code",
};

interface ResolvedInstall {
  /** macOS: the .app bundle handed to `open`; elsewhere: the Electron binary. */
  target: string;
  source: string;
}

/** The sentinel's shape — only what the runner reads; the entry may carry
 * extra fields, unknown ones are ignored. */
interface Sentinel {
  ok: boolean;
  error?: string | null;
  failed?: string[];
}

type Verdict = { ok: true } | { ok: false; reason: string };

async function isFileAsync(path: string): Promise<boolean> {
  try {
    return (await Deno.stat(path)).isFile;
  } catch {
    return false;
  }
}

async function isDirectoryAsync(path: string): Promise<boolean> {
  try {
    return (await Deno.stat(path)).isDirectory;
  } catch {
    return false;
  }
}

async function realPathOrNullAsync(path: string): Promise<string | undefined> {
  try {
    return await Deno.realPath(path);
  } catch {
    return undefined;
  }
}

/** The `which`/`where` locator for one name: the first hit's path, or
 * undefined when the locator is missing or finds nothing. */
async function whichAsync(name: string): Promise<string | undefined> {
  const [locator, ...flags] = Deno.build.os === "windows" ? ["where", name] : ["which", name];
  try {
    const output = await new Deno.Command(locator, {
      args: flags,
      stdin: "null",
      stdout: "piped",
      stderr: "null",
    }).output();
    if (!output.success) return undefined;
    const first = new TextDecoder().decode(output.stdout).split("\n")[0]?.trim();
    return first !== undefined && first.length > 0 ? first : undefined;
  } catch {
    return undefined;
  }
}

/** Walks up from `path` (symlinks resolved) to the nearest enclosing `.app`
 * bundle directory; undefined when no ancestor is one. */
async function enclosingAppAsync(path: string): Promise<string | undefined> {
  let current = await realPathOrNullAsync(path);
  if (current === undefined) return undefined;
  for (;;) {
    if (current.endsWith(".app") && await isDirectoryAsync(current)) return current;
    const parent = dirname(current);
    if (parent === current) return undefined;
    current = parent;
  }
}

/** Linux `code` hits are CLI wrappers (`<root>/bin/code`); the Electron
 * binary they shell out to sits at `<root>/code`. Launching the wrapper
 * itself would detach from the child's exit code — the same failure the
 * macOS contract rules out — so resolve past it. */
async function linuxBinaryBehindCliAsync(cli: string): Promise<string | undefined> {
  const resolved = await realPathOrNullAsync(cli);
  if (resolved === undefined) return undefined;
  const binDir = dirname(resolved);
  if (basename(binDir) !== "bin") return undefined;
  const binary = join(dirname(binDir), basename(resolved));
  return await isFileAsync(binary) ? binary : undefined;
}

async function resolveInstallAsync(): Promise<ResolvedInstall> {
  const override = Deno.env.get("MAIEUTICS_VSCODE_PATH");
  if (override !== undefined) {
    if (Deno.build.os === "darwin") {
      // The override may name the .app or anything inside it; normalize up to
      // the bundle, which is what `open` launches.
      const app = await enclosingAppAsync(override);
      if (app === undefined) {
        throw new Error(
          `MAIEUTICS_VSCODE_PATH is set but is neither a .app bundle nor inside one: ${override}`,
        );
      }
      return { target: app, source: "MAIEUTICS_VSCODE_PATH" };
    }
    if (!await isFileAsync(override)) {
      throw new Error(`MAIEUTICS_VSCODE_PATH is set but does not point to a file: ${override}`);
    }
    return { target: override, source: "MAIEUTICS_VSCODE_PATH" };
  }

  const cli = await whichAsync("code");
  if (Deno.build.os === "darwin" && cli !== undefined) {
    const app = await enclosingAppAsync(cli);
    if (app !== undefined) return { target: app, source: "realpath(which code)" };
  }
  if (Deno.build.os === "linux" && cli !== undefined) {
    const binary = await linuxBinaryBehindCliAsync(cli);
    if (binary !== undefined) return { target: binary, source: "realpath(which code) beside bin/" };
  }

  const platformDefault = PlatformDefaults[Deno.build.os];
  if (platformDefault !== undefined) {
    const exists = Deno.build.os === "darwin"
      ? await isDirectoryAsync(platformDefault)
      : await isFileAsync(platformDefault);
    if (exists) return { target: platformDefault, source: `platform default (${Deno.build.os})` };
  }

  const version = Deno.env.get("MAIEUTICS_VSCODE_VERSION") ?? "stable";
  console.log(`no local VS Code found — downloading ${version} via @vscode/test-electron…`);
  const { downloadAndUnzipVSCode } = await import("@vscode/test-electron");
  const executablePath = await downloadAndUnzipVSCode(version);
  if (Deno.build.os === "darwin") {
    // The download unzips a .app bundle; `open` needs the bundle, not the
    // Electron binary inside it.
    const app = await enclosingAppAsync(executablePath);
    if (app !== undefined) {
      return { target: app, source: `downloaded via @vscode/test-electron (${version})` };
    }
  }
  return { target: executablePath, source: `downloaded via @vscode/test-electron (${version})` };
}

/** VS Code binds a unix IPC socket under the user-data dir; macOS's long
 * per-process TMPDIR pushes that path past the 103-character sun_path limit
 * (the host then dies with `listen EINVAL`), so the scratch tree lives
 * directly under /tmp where the full socket path stays short. */
async function makeScratchDirAsync(): Promise<string> {
  if (Deno.build.os !== "windows") {
    try {
      if ((await Deno.stat("/tmp")).isDirectory) {
        return await Deno.makeTempDir({ dir: "/tmp", prefix: "maieutics-vscode-test-" });
      }
    } catch {
      // No /tmp (unusual) — fall through to the default temp dir.
    }
  }
  return await Deno.makeTempDir({ prefix: "maieutics-vscode-test-" });
}

/** The pollable child on platforms that start the binary directly; undefined
 * on macOS, where `open` hands the app to Launch Services and exits. */
interface Launch {
  child?: Deno.ChildProcess;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Flags shared by every launch shape; the caller appends the workspace
 * folder as the trailing positional — the tests' only workspace root. */
function launchFlags(
  extensionRoot: string,
  testsPath: string,
  userDataDir: string,
  extensionsDir: string,
): string[] {
  return [
    `--extensionDevelopmentPath=${extensionRoot}`,
    `--extensionTestsPath=${testsPath}`,
    "--disable-extensions",
    "--disable-gpu",
    "--no-sandbox",
    "--disable-updates",
    "--skip-welcome",
    "--skip-release-notes",
    "--disable-workspace-trust",
    `--user-data-dir=${userDataDir}`,
    `--extensions-dir=${extensionsDir}`,
  ];
}

async function launchAsync(
  install: ResolvedInstall,
  flags: string[],
  workspaceDir: string,
): Promise<Launch> {
  if (Deno.build.os === "darwin") {
    // `open` exits as soon as Launch Services takes the hand-off; its exit
    // code says the bundle launched, nothing about the tests. The verdict
    // travels through the sentinel instead.
    const open = new Deno.Command("open", {
      args: ["-n", "-g", "-j", install.target, "--args", ...flags, workspaceDir],
      stdin: "null",
      stdout: "inherit",
      stderr: "inherit",
    }).spawn();
    const status = await open.status;
    if (!status.success) {
      throw new Error(`open exited with code ${status.code} while launching ${install.target}`);
    }
    return {};
  }

  let command = install.target;
  let args = [...flags, workspaceDir];
  if (Deno.build.os === "linux" && await whichAsync("xvfb-run") !== undefined) {
    // CI's headless boxes have no display; the external xvfb-run wraps the
    // binary in a virtual one. A desktop Linux without xvfb-run still works.
    command = "xvfb-run";
    args = ["-a", ...args];
    console.log("launching under xvfb-run");
  }
  return {
    child: new Deno.Command(command, {
      args,
      stdin: "null",
      stdout: "inherit",
      stderr: "inherit",
    }).spawn(),
  };
}

async function readSentinelAsync(path: string): Promise<Sentinel | undefined> {
  let text: string;
  try {
    text = await Deno.readTextFile(path);
  } catch {
    return undefined; // not written yet
  }
  try {
    const parsed: unknown = JSON.parse(text);
    if (typeof parsed !== "object" || parsed === null) return undefined;
    const candidate = parsed as Sentinel;
    return typeof candidate.ok === "boolean" ? candidate : undefined;
  } catch {
    return undefined; // mid-write; keep polling
  }
}

/** Polls the sentinel until it lands, the child exits, or the budget runs
 * out. The sentinel is the verdict on every platform; a child exit without
 * one (Linux/Windows only) just fails faster than the timeout would. */
async function waitForVerdictAsync(
  sentinelPath: string,
  timeoutMs: number,
  child: Deno.ChildProcess | undefined,
): Promise<Verdict> {
  const deadline = Date.now() + timeoutMs;
  let exitCode: number | undefined;
  for (;;) {
    const sentinel = await readSentinelAsync(sentinelPath);
    if (sentinel !== undefined) {
      if (sentinel.ok) return { ok: true };
      const failed = sentinel.failed ?? [];
      return {
        ok: false,
        reason: `the extension-host tests failed: ${sentinel.error ?? "(no error reported)"}${
          failed.length > 0 ? ` (failed: ${failed.join("; ")})` : ""
        }`,
      };
    }
    if (exitCode !== undefined) {
      return {
        ok: false,
        reason: `VS Code exited with code ${exitCode} without writing ${sentinelPath}`,
      };
    }
    if (Date.now() >= deadline) {
      return { ok: false, reason: `timed out after ${timeoutMs}ms without ${sentinelPath}` };
    }
    const tick = delay(PollIntervalMs).then(() => "tick" as const);
    if (child === undefined) {
      await tick;
      continue;
    }
    const outcome = await Promise.race([tick, child.status.then(() => "exit" as const)]);
    if (outcome === "exit") {
      const status = await child.status;
      exitCode = status.code;
    }
  }
}

/** Reaps this run's instance by its unique user-data-dir substring — on macOS
 * `open` left no child handle, and helper processes can outlive a fast exit
 * elsewhere. pkill reports 1 when nothing matches, which is fine. */
async function killInstanceAsync(userDataDir: string): Promise<void> {
  if (Deno.build.os === "windows") return;
  try {
    await new Deno.Command("pkill", {
      args: ["-f", userDataDir],
      stdin: "null",
      stdout: "null",
      stderr: "null",
    }).output();
  } catch {
    // No pkill on this box; the isolated scratch tree collides with nothing.
  }
}

async function mainAsync(): Promise<number> {
  // The extension root is the runner's parent directory, independent of the
  // working directory the task was invoked from.
  const extensionRoot = fileURLToPath(new URL("../", import.meta.url)).replace(/[\\/]+$/, "");
  const testsPath = join(extensionRoot, "dist", "test", "extensionHost.cjs");
  if (!await isFileAsync(testsPath)) {
    throw new Error(`${testsPath} is missing — run 'deno task build:test' first`);
  }

  const timeoutMs = Number(Deno.env.get("MAIEUTICS_VSCODE_TIMEOUT_MS") ?? "600000");
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    throw new Error(
      `MAIEUTICS_VSCODE_TIMEOUT_MS must be a positive number of milliseconds (got ${timeoutMs})`,
    );
  }

  const install = await resolveInstallAsync();

  const scratch = await makeScratchDirAsync();
  const userDataDir = join(scratch, "user-data-dir");
  const extensionsDir = join(scratch, "extensions-dir");
  const workspaceDir = join(scratch, "workspace");
  for (const dir of [userDataDir, extensionsDir, workspaceDir]) {
    await Deno.mkdir(dir, { recursive: true });
  }

  console.log(`VS Code: ${install.target} (${install.source})`);
  console.log(`scratch: ${scratch}`);
  console.log(`timeout: ${timeoutMs}ms`);

  try {
    const launch = await launchAsync(
      install,
      launchFlags(extensionRoot, testsPath, userDataDir, extensionsDir),
      workspaceDir,
    );
    const verdict = await waitForVerdictAsync(
      join(workspaceDir, SentinelFileName),
      timeoutMs,
      launch.child,
    );
    if (!verdict.ok) {
      console.error(`vscode_runner: ${verdict.reason}`);
      return 1;
    }
    console.log("vscode_runner: extension-host tests passed");
    return 0;
  } finally {
    await killInstanceAsync(userDataDir);
    // Give the reaped instance a moment to finish writing before the tree
    // under it disappears.
    await delay(500);
    await Deno.remove(scratch, { recursive: true }).catch((error: unknown) => {
      console.error(`vscode_runner: could not remove ${scratch}: ${String(error)}`);
    });
  }
}

try {
  Deno.exit(await mainAsync());
} catch (error) {
  console.error(`vscode_runner: ${error instanceof Error ? error.message : String(error)}`);
  Deno.exit(1);
}
