/**
 * The REPL's process-start polyfill (ADR 0045): the kernel arms it with the
 * `MAIEUTICS_PROCESS_SANDBOX` env (prefer/require); without the value (or
 * with `off`) the worker's `Deno.Command` stays the native API and spawns
 * keep riding the child's own Deno permission path — the pre-ADR-0045
 * behavior, unchanged. Armed, every `new Deno.Command(...)` the cell makes
 * routes through the kernel's `process_spawn` tool: the session's permission
 * policy gates the executable and shapes the platform sandbox, the
 * environment comes from the policy's env allowlist, and the argv the kernel
 * launches may be the request verbatim or a sandbox wrapper carrying it.
 *
 * Declared scope: `output()` semantics — captured stdout/stderr, exit code,
 * a declarative timeout that kills the process tree. `spawn()` and
 * `outputSync()` have no kernel round trip: armed, they fail typed
 * (interactive processes belong to the terminal tools; a sync call cannot
 * await the kernel), and the per-call `tool_not_found` fallback keeps the
 * native API for every shape when the kernel does not serve the tool.
 */

/** The env var the kernel sets at spawn when the sandbox mode is armed. */
export const PROCESS_SANDBOX_ENV = "MAIEUTICS_PROCESS_SANDBOX";

/** The subset of Deno.CommandOptions the polyfill understands. */
export interface CommandOptions {
  readonly args?: readonly string[];
  readonly cwd?: string;
  readonly env?: Record<string, string>;
  readonly clearEnv?: boolean;
  readonly stdin?: "piped" | "inherit" | "null";
  readonly stdout?: "piped" | "inherit" | "null";
  readonly stderr?: "piped" | "inherit" | "null";
}

/** The shape of Deno.CommandOutput the polyfill answers. */
export interface CommandOutput {
  readonly success: boolean;
  readonly code: number;
  readonly stdout: Uint8Array;
  readonly stderr: Uint8Array;
}

/** The kernel's failure envelope, flattened by the client layer into
 * `Error` messages shaped `"<code>: <message>"`. */
export function bridgeErrorCode(error: unknown): string | undefined {
  if (!(error instanceof Error)) return undefined;
  const colon = error.message.indexOf(":");
  return colon > 0 ? error.message.slice(0, colon) : undefined;
}

/** The code that means the kernel does not serve the spawn tool (sandbox
 * mode Off, or a kernel predating ADR 0045): the per-call fallback to the
 * native API, exactly the pre-polyfill behavior. */
export function isSpawnToolUnavailable(error: unknown): boolean {
  return bridgeErrorCode(error) === "tool_not_found";
}

export interface ProcessBridgeDeno {
  Command: new (command: string, options?: CommandOptions) => {
    output(): Promise<CommandOutput>;
    spawn(): unknown;
    outputSync(): CommandOutput;
  };
}

const encoder = new TextEncoder();

/** Installs the polyfill on the given Deno namespace (pass the real `Deno`
 * from the worker; tests pass a stub). No-op unless the mode env arms it —
 * the native surface stays entirely. */
export function installProcessBridge(
  denoObject: ProcessBridgeDeno,
  mode: string | undefined,
  invoke: (tool: string, args: Record<string, unknown>) => Promise<unknown>,
): void {
  if (mode !== "prefer" && mode !== "require") return;

  const OriginalCommand = denoObject.Command;

  class Command {
    readonly #command: string;
    readonly #options: CommandOptions;

    constructor(command: string, options?: CommandOptions) {
      this.#command = command;
      this.#options = options ?? {};
    }

    async output(): Promise<CommandOutput> {
      if (this.#options.cwd !== undefined) {
        throw new Error(
          "process_spawn_cwd_unsupported: the kernel spawns processes in the workspace root; " +
            "pass absolute paths instead of a cwd.",
        );
      }
      if (this.#options.stdin === "inherit") {
        throw new Error(
          "process_spawn_stdin_unsupported: inherited stdin has no kernel round trip; write the " +
            "input through the stdin option of a piped command instead.",
        );
      }
      if (this.#options.stdout === "inherit" || this.#options.stderr === "inherit") {
        throw new Error(
          "process_spawn_inherit_unsupported: inherited output has no kernel round trip; the " +
            "terminal tools own interactive processes.",
        );
      }

      const request: Record<string, unknown> = {
        executable: this.#command,
        arguments: [...(this.#options.args ?? [])],
      };
      // The cell may select env NAMES; the kernel supplies only names the session's
      // policy env allowlist carries, so no value crosses from the cell.
      if (this.#options.env !== undefined) {
        request.environmentNames = Object.keys(this.#options.env);
      }

      try {
        const answer = await invoke("process_spawn", request) as {
          exitCode?: number | null;
          timedOut?: boolean;
          outputTruncated?: boolean;
          stdout?: unknown;
          stderr?: unknown;
        };
        if (answer.timedOut === true) {
          throw new Error(
            "process_spawn_timeout: the process was killed after its timeout; the captured " +
              "partial output was discarded.",
          );
        }

        return {
          success: answer.exitCode === 0,
          code: answer.exitCode ?? -1,
          stdout: encoder.encode(typeof answer.stdout === "string" ? answer.stdout : ""),
          stderr: encoder.encode(typeof answer.stderr === "string" ? answer.stderr : ""),
        };
      } catch (error) {
        if (isSpawnToolUnavailable(error)) {
          const native = new OriginalCommand(this.#command, this.#options);
          return native.output();
        }
        throw error;
      }
    }

    spawn(): unknown {
      // No synchronous kernel round trip exists; the typed failure points at the
      // surfaces that own interactive processes.
      throw new Error(
        "process_spawn_interactive_unsupported: interactive processes belong to the terminal " +
          "tools; use output() for captured runs.",
      );
    }

    outputSync(): CommandOutput {
      throw new Error(
        "process_spawn_sync_unsupported: a synchronous spawn cannot await the kernel; use " +
          "output() instead.",
      );
    }
  }

  denoObject.Command = Command as unknown as ProcessBridgeDeno["Command"];
}
