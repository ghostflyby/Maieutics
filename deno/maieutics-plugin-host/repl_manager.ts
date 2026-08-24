/**
 * ReplManager: the plugin host's REPL process derivation (ADR 0020).
 *
 * The plugin host is the spawner of the Deno REPL. Unlike plugin workers
 * (spawned as `Worker` actors via `spawn()`), the REPL is spawned as a separate
 * PROCESS via worker-actor's `spawnProcess` — the REPL is the carrier of the
 * kernel's permission policy and must live in its own process boundary (ADR
 * 0020 decision 1), not in a host-owned worker.
 *
 * The closed loop (B3 + B5a):
 *
 *   kernel sends `host.repl.derive` over the control bus (B5b) — the host no
 *     longer decides the entry/env/permission shell itself (B1 skeleton default)
 *   -> host.spawnRepl({ sessionId, generation, entryUrl, env, permissions })
 *     -> spawnProcess(entryUrl) with the kernel's static permission shell
 *     -> REPL child pregest frame (the host learns the child pid before any
 *        broker-gated permission check the child can make)
 *     -> host emits `host.repl.spawned` over the control bus (B3)
 *     -> REPL child rpc.initialize() (env reads now resolve through the broker
 *        policy the kernel registered for the reported pid)
 *     -> host registers { pid, actor } in memory keyed by session
 *     -> child death / dispose -> host emits `host.repl.exited` exactly once
 *
 * The static permission shell passed at spawn is the broker's fallback
 * baseline, NOT the security boundary. The authoritative REPL policy is
 * computed by the kernel and registered with the permission broker for this
 * pid (ADR 0020 decision 1 / 3). The host never widens the shell on its own.
 *
 * B5a adds the kernel → host instruction stream: the host accepts
 * `host.repl.derive` envelopes (see {@link ReplManager.derive}) and derives
 * the REPL with the kernel-supplied entry/env/permissions instead of the
 * hard-coded skeleton parameters. The env contract is authoritative: the
 * kernel sends the FULL child env and the host only appends the broker
 * address (DENO_PERMISSION_BROKER_PATH, from its own MAIEUTICS_PERMISSION_BROKER)
 * and, on Windows, SystemRoot. See `host_repl_protocol.ts` for the wire
 * contract this manager implements.
 */

import { type ActorHandle, type Remote, spawnProcess } from "@ghostflyby/worker-actor";
import type { ReplEnvelope } from "../shared/protocol.ts";
import {
  type HostReplDerivePayload,
  type HostReplPermissions,
  type HostReplReport,
  type HostReplRpc,
} from "./host_repl_protocol.ts";

const SESSION_ENV = "MAIEUTICS_REPL_SESSION";
const GENERATION_ENV = "MAIEUTICS_REPL_GENERATION";
/** The kernel hands the broker address to the host under this name; the host
 * itself must NOT consult the broker (it runs with full launch-time grants and
 * no registered policy) and only forwards the address to the REPL child as
 * DENO_PERMISSION_BROKER_PATH (B2 env contract). */
const BROKER_ENV = "MAIEUTICS_PERMISSION_BROKER";
/** Deno's own broker env: when set at process launch the broker is the single
 * authority for every explicit permission check the process makes. */
const BROKER_PATH_ENV = "DENO_PERMISSION_BROKER_PATH";
/** Poll interval of the host-side pid liveness monitor. */
const LIVENESS_POLL_MS = 200;

/** Static permission shell for the REPL child at spawn when the derive
 * instruction carries no `permissions` (B5a default). This is the broker's
 * fallback baseline when the broker is absent, NOT a security boundary (ADR
 * 0020 decision 1): the kernel computes the authoritative REPL EffectivePolicy
 * and registers it with the permission broker for this pid. Read + env are the
 * skeleton's baseline grants: read to load the entry module graph, env so the
 * child can read the session identity vars (MAIEUTICS_REPL_SESSION /
 * MAIEUTICS_REPL_GENERATION). */
const REPL_SHELL_PERMISSIONS = { read: true, env: true } as const;

/** Sink for host → kernel REPL reports (see host_repl_protocol.ts). The bus
 * connection may not exist yet when a ReplManager is constructed; mod.ts wires
 * it once the control bus is connected (spawnRepl refuses to derive a REPL
 * before that — the pid report is what registers the child's broker policy and
 * control-channel identity, so deriving a REPL without a connected bus would
 * deadlock every broker-gated permission request the child makes). */
export type ReplReporter = (report: HostReplReport) => void;

export interface ReplManagerOptions {
  /** Absolute path of the REPL process entry module (process_main.ts). Used as
   * the fallback default when a derive instruction omits `entryUrl`; the
   * kernel supplies the authoritative entry in the B5a flow. */
  replEntryPath: string;
  /** Filesystem read grant for the REPL child's module graph (defaults to
   * allowing reads; the broker overrides in the full migration). Only applied
   * to the default permission shell, never to a kernel-provided shell. */
  replEntryReadPath?: string;
  /** Host → kernel reporter for pid/session reports. Set after the bus
   * connection is established; spawnRepl fails while it is still unset and a
   * report is expected. */
  reporter?: ReplReporter;
}

/** One derived REPL process: its self-reported pid plus the actor handle. */
export interface ReplHandle {
  readonly sessionId: string;
  readonly generation: number;
  readonly pid: number;
  readonly actor: Remote<HostReplRpc> & ActorHandle;
  /** Whether this derivation reports to the kernel. A `report: false` derive
   * stays silent: no spawned/exited reports are emitted for the handle. */
  readonly report: boolean;
  state: "running" | "stopped" | "crashed";
}

/**
 * A kernel `host.repl.derive` instruction normalized by
 * {@link ReplManager.derive}. `env` is optional here (the direct API call path
 * may omit it and let the manager fill the session identity vars); the bus
 * path always carries a full env record from the parser.
 */
export interface ReplDeriveRequest {
  /** Session id this REPL child belongs to (MAIEUTICS_REPL_SESSION). */
  sessionId: string;
  /** Generation number of the session (MAIEUTICS_REPL_GENERATION). */
  generation: number;
  /** Absolute file URL (or absolute filesystem path) of the REPL entry module.
   * Falls back to the manager's `replEntryPath` when empty/omitted. */
  entryUrl?: string;
  /** Complete REPL child env from the kernel. When a key is missing the host
   * fills MAIEUTICS_REPL_SESSION / MAIEUTICS_REPL_GENERATION from the request
   * identity; kernel-provided values are never overwritten. */
  env?: Record<string, string>;
  /** Static permission shell for `spawnProcess` (broker fallback baseline).
   * Absent means the skeleton default `{ read: true, env: true }`. */
  permissions?: HostReplPermissions;
  /** Whether the host reports spawned/exited/deriveFailed (default true). */
  report?: boolean;
}

/**
 * Thrown by spawnRepl when a derivation fails. `stage` tells the caller how
 * far the derive got before the failure:
 * - `"spawn"`: before any pid report reached the kernel; the caller should
 *   emit `host.repl.deriveFailed` when reporting is enabled.
 * - `"init"`: after `host.repl.spawned` already went out; the manager already
 *   balanced it with `host.repl.exited`, so the caller must NOT emit a second
 *   failure report (the kernel would see both).
 */
export class ReplDeriveError extends Error {
  readonly stage: "spawn" | "init";

  constructor(message: string, stage: "spawn" | "init") {
    super(message);
    this.name = "ReplDeriveError";
    this.stage = stage;
  }
}

/** Permission kinds the kernel may express in a derive shell. */
const PERMISSION_KINDS = [
  "read",
  "write",
  "net",
  "env",
  "run",
  "ffi",
  "sys",
  "import",
] as const;

/**
 * The pid the REPL child reports back to the host. worker-actor 0.4.0's
 * spawnProcess does not expose the spawned child pid and has no env option; to
 * learn the pid the host must round-trip through the child. The child reports
 * it twice: in a pregest handshake that runs at module top level BEFORE the
 * broker env can matter (so the host can emit `host.repl.spawned` before the
 * child's first broker-gated permission check), and again from
 * rpc.initialize(). A reported pid is accepted when it is a safe integer and
 * differs from the host's own pid.
 */
export function isValidReplPid(pid: number): boolean {
  return pid !== Deno.pid && Number.isSafeInteger(pid) && pid > 0;
}

/** Resolves a kernel-supplied REPL entry to the string handed to `deno run`.
 * An absolute file:// URL is normalized to its href; an absolute filesystem
 * path (including a Windows drive path, which `new URL` misreads as a scheme)
 * is passed through unchanged — `deno run` accepts both. A relative URL has no
 * sensible base on the host side and is left to `deno run` (which fails
 * loudly); the kernel contract is an absolute entry. */
function resolveReplEntry(entryUrl: string): string {
  try {
    const url = new URL(entryUrl);
    if (url.protocol === "file:") return url.href;
  } catch {
    // Not a URL: an absolute filesystem path (or Windows drive path).
  }
  return entryUrl;
}

/** Validates a `host.repl.derive` payload into a {@link ReplDeriveRequest}.
 * Throws on malformed payloads so the caller can report the rejection. The
 * kernel env contract (`host_repl_protocol.ts`) requires `env` to be a string
 * record when present; a missing `env` is tolerated as empty (the host fills
 * the session identity vars). */
function parseDeriveRequest(payload: unknown): ReplDeriveRequest {
  if (typeof payload !== "object" || payload === null || Array.isArray(payload)) {
    throw new Error("host.repl.derive payload must be an object.");
  }
  const value = payload as Partial<HostReplDerivePayload>;
  const sessionId = value.sessionId;
  if (typeof sessionId !== "string" || sessionId.length === 0) {
    throw new Error("host.repl.derive requires a non-empty string sessionId.");
  }
  const generation = value.generation as number | undefined;
  if (generation === undefined || !Number.isSafeInteger(generation) || generation < 0) {
    throw new Error("host.repl.derive requires a non-negative integer generation.");
  }
  if (typeof value.entryUrl !== "string" || value.entryUrl.length === 0) {
    throw new Error("host.repl.derive requires a non-empty string entryUrl.");
  }
  const env: Record<string, string> = {};
  if (value.env !== undefined) {
    if (typeof value.env !== "object" || value.env === null || Array.isArray(value.env)) {
      throw new Error("host.repl.derive env must be a string record.");
    }
    for (const [key, entry] of Object.entries(value.env)) {
      if (typeof entry !== "string") {
        throw new Error(`host.repl.derive env['${key}'] must be a string.`);
      }
      env[key] = entry;
    }
  }
  if (value.report !== undefined && typeof value.report !== "boolean") {
    throw new Error("host.repl.derive report must be a boolean.");
  }
  return {
    sessionId,
    generation,
    entryUrl: value.entryUrl,
    env,
    permissions: value.permissions === undefined ? undefined : parsePermissions(value.permissions),
    report: value.report,
  };
}

/** Validates the optional static permission shell of a derive payload. */
function parsePermissions(value: unknown): HostReplPermissions {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error("host.repl.derive permissions must be an object.");
  }
  const record = value as Record<string, unknown>;
  const permissions: HostReplPermissions = {};
  for (const kind of PERMISSION_KINDS) {
    const entry = record[kind];
    if (entry === undefined) continue;
    if (typeof entry === "boolean") {
      permissions[kind] = entry;
    } else if (Array.isArray(entry) && entry.every((item) => typeof item === "string")) {
      permissions[kind] = entry;
    } else {
      throw new Error(
        `host.repl.derive permissions.${kind} must be a boolean or a string array.`,
      );
    }
  }
  return permissions;
}

export class ReplManager {
  #replEntryPath: string;
  #replReadGrant: boolean | string[];
  #reporter?: ReplReporter;
  /** In-memory pid registry keyed by session id. B2 reads this to forward the
   * pid to the kernel broker and control-channel identity check. */
  #repls = new Map<string, ReplHandle>();

  constructor(options: ReplManagerOptions) {
    this.#replEntryPath = options.replEntryPath;
    const entryRead = options.replEntryReadPath;
    this.#replReadGrant = entryRead === undefined || entryRead === null ? true : [entryRead];
    this.#reporter = options.reporter;
  }

  /** Sets (or replaces) the host → kernel reporter. Must be non-null before
   * spawnRepl runs with reporting enabled; the bus is established after the
   * control hello. */
  setReporter(reporter: ReplReporter | undefined): void {
    this.#reporter = reporter;
  }

  /**
   * Handles a kernel `host.repl.derive` envelope (ADR 0020, B5a). This is the
   * receive end of the kernel → host instruction stream: the kernel decides the
   * REPL entry, the complete child env, and the static permission shell, and
   * the host executes the derive. Validation and derivation are async, so the
   * outcome is reported fire-and-forget from the bus call site (mod.ts ignores
   * the returned promise, matching the `extension.invoke` style). The promise
   * resolves with the derived handle on success and never rejects (failures
   * are reported instead), which makes it awaitable in tests:
   * - a malformed payload is rejected with `host.repl.deriveFailed`;
   * - a pre-pid derivation failure (spawn error, duplicate session, missing
   *   reporter) is reported with `host.repl.deriveFailed`;
   * - a failure AFTER `host.repl.spawned` is balanced by `host.repl.exited`
   *   inside spawnRepl and logged here (never double-reported).
   */
  derive(envelope: ReplEnvelope): Promise<ReplHandle | undefined> {
    let request: ReplDeriveRequest;
    let report = true;
    try {
      request = parseDeriveRequest(envelope.payload);
      report = request.report !== false;
    } catch (error) {
      const raw = envelope.payload as Partial<HostReplDerivePayload> | undefined;
      report = raw?.report !== false;
      // The protocol types sessionId/generation as string/number; a malformed
      // payload may carry non-conforming values, so only echo them when they
      // already match (the .NET side deserializes the failure into a string
      // sessionId + number generation).
      const sessionId = typeof raw?.sessionId === "string" ? raw.sessionId : "";
      const generation = typeof raw?.generation === "number" && Number.isSafeInteger(raw.generation)
        ? raw.generation
        : 0;
      this.#reportDeriveFailed(sessionId, generation, error, report);
      return Promise.resolve(undefined);
    }
    return this.spawnRepl(request).then(
      (handle) => handle,
      (error: Error) => {
        if (error instanceof ReplDeriveError && error.stage === "init") {
          // The spawn report already went out and was balanced with an exited
          // report; a second deriveFailed would double-report the failure.
          console.error(
            `[plugin-host] REPL derive for session '${request.sessionId}' failed after ` +
              `spawn: ${error.message}`,
          );
          return undefined;
        }
        this.#reportDeriveFailed(request.sessionId, request.generation, error, report);
        return undefined;
      },
    );
  }

  /** Registers `sessionId` as a running REPL in the pid registry. The kernel
   * supplies the entry module, the complete child environment, and the static
   * permission shell through the request (B5a); the host only appends the
   * broker address and Windows SystemRoot, and fills the session identity vars
   * when the request env omits them. */
  async spawnRepl(request: ReplDeriveRequest): Promise<ReplHandle> {
    const { sessionId, generation } = request;
    const report = request.report !== false;
    const entryUrl = request.entryUrl || this.#replEntryPath;
    if (entryUrl.length === 0) {
      throw new ReplDeriveError(
        `No REPL entry for session '${sessionId}': the derive request has no ` +
          `entryUrl and the host has no default entry.`,
        "spawn",
      );
    }
    const existing = this.#repls.get(sessionId);
    if (existing !== undefined && existing.state === "running") {
      throw new ReplDeriveError(
        `A REPL process is already running for session '${sessionId}'.`,
        "spawn",
      );
    }
    if (report && this.#reporter === undefined) {
      throw new ReplDeriveError(
        `The control bus is not connected; the host.repl.spawned pid report ` +
          `registers the child's broker policy and control-channel identity, so ` +
          `no REPL can be derived before it (session '${sessionId}').`,
        "spawn",
      );
    }

    const permissions: Deno.PermissionOptionsObject = request.permissions === undefined
      ? this.#defaultShellPermissions()
      : { ...request.permissions };
    // The REPL child reads its session identity, broker address, and every
    // kernel-decided variable from the environment captured at launch. The
    // kernel env is authoritative; the host only appends the broker address +
    // Windows SystemRoot (never guessing or overwriting kernel entries).
    const restoreEnv = this.#injectChildEnv(request.env ?? {}, sessionId, generation);
    let actor: Remote<HostReplRpc> & ActorHandle;
    try {
      actor = await spawnProcess<HostReplRpc>(
        resolveReplEntry(entryUrl),
        {
          permissions,
          onDeath: (reason: unknown) => this.#handleDeath(sessionId, generation, reason),
        },
      );
    } catch (error) {
      throw new ReplDeriveError(
        error instanceof Error ? error.message : String(error),
        "spawn",
      );
    } finally {
      // Deno reads DENO_PERMISSION_BROKER_PATH at launch; restoring the host's
      // own environment before the handshake resolves keeps the host's later
      // permission checks on their full-grant flags.
      restoreEnv();
    }

    // The pid comes from the child itself: spawnProcess (worker-actor 0.4.0)
    // does not expose the spawned process id. The child reports Deno.pid from
    // the pregest frame; the host reports it to the kernel BEFORE calling
    // initialize() so the broker policy the kernel registers for the pid is in
    // place when the child's first broker-gated permission check (the env reads
    // inside initialize) arrives.
    let pid: number;
    try {
      pid = await this.#requestPid(actor);
    } catch (error) {
      // Pre-pid failure: no report reached the kernel yet, so the caller
      // reports deriveFailed. Dispose the child so a broken handshake does not
      // leak the process.
      await actor.dispose().catch(() => {});
      throw new ReplDeriveError(
        error instanceof Error ? error.message : String(error),
        "spawn",
      );
    }
    if (report) this.#emitSpawned(sessionId, generation, pid, report);

    let info: { pid: number; sessionId: string; generation: number };
    try {
      info = await actor.initialize();
      if (!isValidReplPid(info.pid)) {
        throw new Error(`REPL process for session '${sessionId}' reported an invalid pid.`);
      }
      if (info.pid !== pid) {
        // The pregest and initialize handshakes must report the same process;
        // a mismatch means the child re-executed between the two or misread its
        // own pid.
        throw new Error(
          `REPL process for session '${sessionId}' reported pid ${info.pid} after the ` +
            `pregest pid ${pid}.`,
        );
      }
      if (info.sessionId !== sessionId) {
        // The child must be the REPL for this session; mismatched identity means
        // the host passed the wrong session env or the child read a stale one.
        throw new Error(
          `REPL process for session '${sessionId}' reported session '${info.sessionId}'.`,
        );
      }
    } catch (error) {
      // The spawn report already reached the kernel; balance it with an exited
      // report so the pid registration (broker policy + session identity) is
      // released even though no handle ever entered the registry.
      await actor.dispose().catch(() => {});
      if (report) {
        this.#emitExited(
          sessionId,
          generation,
          pid,
          error instanceof Error ? error.message : String(error),
          report,
        );
      }
      throw new ReplDeriveError(
        error instanceof Error ? error.message : String(error),
        "init",
      );
    }

    const handle: ReplHandle = {
      sessionId,
      generation,
      pid,
      actor,
      state: "running",
      report,
    };
    this.#repls.set(sessionId, handle);
    this.#startLivenessMonitor(sessionId, handle);
    console.error(
      `[plugin-host] derived REPL process for session '${sessionId}' generation ` +
        `${generation}: pid ${pid}.`,
    );
    return handle;
  }

  /** The registered handle for a session, if one is currently running. */
  get(sessionId: string): ReplHandle | undefined {
    const handle = this.#repls.get(sessionId);
    return handle !== undefined && handle.state === "running" ? handle : undefined;
  }

  /** Disposes the REPL process for a session and removes it from the registry.
   * Returns false when the session had no running REPL. */
  async disposeRepl(sessionId: string): Promise<boolean> {
    const handle = this.#repls.get(sessionId);
    if (handle === undefined || handle.state !== "running") return false;
    handle.state = "stopped";
    try {
      await handle.actor.dispose();
    } finally {
      this.#repls.delete(sessionId);
      this.#emitExited(sessionId, handle.generation, handle.pid, undefined, handle.report);
    }
    return true;
  }

  /** Disposes every derived REPL process (host shutdown path). */
  async disposeAll(): Promise<void> {
    for (const sessionId of [...this.#repls.keys()]) {
      await this.disposeRepl(sessionId);
    }
  }

  /** The default permission shell when the derive instruction carries none
   * (skeleton baseline: read + env, with the optional entry read grant). */
  #defaultShellPermissions(): Deno.PermissionOptionsObject {
    const permissions: Deno.PermissionOptionsObject = {
      ...REPL_SHELL_PERMISSIONS,
    };
    if (typeof this.#replReadGrant !== "boolean") {
      permissions.read = this.#replReadGrant;
    }
    return permissions;
  }

  /**
   * Merges the kernel-provided child env with the host-only additions (broker
   * path, Windows SystemRoot) onto the host's own process environment around
   * one spawn, because worker-actor 0.4.0's spawnProcess has no env option and
   * the child captures its environment at launch. Returns the restore closure.
   * This is safe only while spawnRepl calls are serialized (the host drives
   * REPL sessions one at a time in the skeleton); concurrent derivations would
   * race on the shared environment. B1's single-threaded skeleton assumption
   * is documented in place.
   *
   * Merge rules (B5a, see `host_repl_protocol.ts`):
   * - the kernel env is authoritative: the host never overwrites a
   *   kernel-provided value;
   * - when the kernel env omits MAIEUTICS_REPL_SESSION / MAIEUTICS_REPL_GENERATION
   *   (direct API calls), the host fills them from the request identity;
   * - the broker address is forwarded verbatim under DENO_PERMISSION_BROKER_PATH
   *   from the host's own MAIEUTICS_PERMISSION_BROKER. When the host was
   *   launched without a broker, the env stays unset so the child launches
   *   with the static shell only (tests / no-kernel runs);
   * - on Windows the host appends its own SystemRoot; the kernel env already
   *   carries MAIEUTICS_REPL_PIPE when the pipe bootstrap is in use, and the
   *   host never invents a pipe name.
   */
  #injectChildEnv(
    env: Record<string, string>,
    sessionId: string,
    generation: number,
  ): () => void {
    const previous = new Map<string, string | undefined>();
    const set = (key: string, value: string): void => {
      if (!previous.has(key)) previous.set(key, Deno.env.get(key));
      Deno.env.set(key, value);
    };
    for (const [key, value] of Object.entries(env)) set(key, value);
    if (!Object.hasOwn(env, SESSION_ENV)) set(SESSION_ENV, sessionId);
    if (!Object.hasOwn(env, GENERATION_ENV)) set(GENERATION_ENV, String(generation));
    const broker = Deno.env.get(BROKER_ENV);
    if (broker !== undefined && broker.length > 0) set(BROKER_PATH_ENV, broker);
    if (Deno.build.os === "windows") {
      const systemRoot = Deno.env.get("SystemRoot");
      if (systemRoot !== undefined && systemRoot.length > 0) set("SystemRoot", systemRoot);
    }
    return () => {
      for (const [key, value] of previous) {
        if (value === undefined) Deno.env.delete(key);
        else Deno.env.set(key, value);
      }
    };
  }

  /**
   * Host-side liveness monitor: polls the REPL child pid and clears the registry
   * when the process is gone. This is the reliable death signal for the
   * skeleton because worker-actor 0.4.0's `onDeath` only fires when the IPC
   * channel closes (crash / handshake failure), not when the child is killed
   * outright (e.g. SIGKILL) — the library never wires `child.on("exit")` to the
   * transport close. The host owns the pid, so it can and must watch it.
   * Polling stops once the handle leaves the registry (dispose / onDeath).
   */
  #startLivenessMonitor(sessionId: string, handle: ReplHandle): void {
    const timer = setInterval(() => {
      if (this.#repls.get(sessionId) !== handle) {
        clearInterval(timer);
        return;
      }
      let alive = true;
      try {
        Deno.kill(handle.pid, 0);
      } catch {
        alive = false;
      }
      if (!alive) {
        clearInterval(timer);
        this.#handleDeath(
          sessionId,
          handle.generation,
          new Error(`REPL process ${handle.pid} is no longer alive.`),
        );
      }
    }, LIVENESS_POLL_MS);
  }

  /** Requests the child pid through the pregest rpc so the host can emit
   * `host.repl.spawned` before any broker-gated child request. The pregest
   * handshake is required: falling back to `initialize()` would run the
   * child's broker-gated env reads before the pid report reached the kernel
   * (a 10s broker wait then a default deny). The pid is only reported onward
   * after it passes {@link isValidReplPid}; a bogus pid must not reach the
   * kernel (the .NET side rejects pid <= 0, but a negative or NaN report
   * would fail the handshake on the way there). */
  async #requestPid(actor: Remote<HostReplRpc> & ActorHandle): Promise<number> {
    if (typeof actor.pregestPid !== "function") {
      throw new Error("The REPL child does not expose the pregest pid handshake.");
    }
    const pid = await actor.pregestPid();
    if (!isValidReplPid(pid)) {
      throw new Error(`The REPL child reported an invalid pid ${pid}.`);
    }
    return pid;
  }

  /** Emits `host.repl.spawned`. The reporter is guaranteed to exist (spawnRepl
   * refuses to run without it when reporting is enabled), but the bus may have
   * dropped meanwhile; a failed report is logged, not fatal — the child is
   * still derived locally. */
  #emitSpawned(sessionId: string, generation: number, pid: number, report: boolean): void {
    if (!report) return;
    const spawned: HostReplReport = {
      type: "host.repl.spawned",
      payload: { sessionId, generation, pid },
    };
    try {
      this.#reporter?.(spawned);
    } catch (error) {
      console.error(
        `[plugin-host] could not report REPL spawn for session '${sessionId}': ` +
          `${error instanceof Error ? error.message : String(error)}.`,
      );
    }
  }

  /** Emits `host.repl.exited` exactly once per handle. disposeRepl marks the
   * handle stopped before emitting; a later liveness/onDeath report sees no
   * running handle and stays silent. */
  #emitExited(
    sessionId: string,
    generation: number,
    pid: number,
    failure: string | undefined,
    report: boolean,
  ): void {
    if (!report) return;
    const exited: HostReplReport = {
      type: "host.repl.exited",
      payload: { sessionId, generation, pid, ...(failure === undefined ? {} : { failure }) },
    };
    try {
      this.#reporter?.(exited);
    } catch (error) {
      console.error(
        `[plugin-host] could not report REPL exit for session '${sessionId}': ` +
          `${error instanceof Error ? error.message : String(error)}.`,
      );
    }
  }

  /** Reports that a `host.repl.derive` instruction could not be executed
   * BEFORE any pid existed. Gated by the instruction's `report` flag. */
  #reportDeriveFailed(
    sessionId: string,
    generation: number,
    error: unknown,
    report: boolean,
  ): void {
    if (!report) return;
    const failed: HostReplReport = {
      type: "host.repl.deriveFailed",
      payload: {
        sessionId,
        generation,
        message: error instanceof Error ? error.message : String(error),
      },
    };
    try {
      this.#reporter?.(failed);
    } catch (sendError) {
      console.error(
        `[plugin-host] could not report REPL derive failure for session '${sessionId}': ` +
          `${sendError instanceof Error ? sendError.message : String(sendError)}.`,
      );
    }
  }

  #handleDeath(sessionId: string, generation: number, reason: unknown): void {
    const handle = this.#repls.get(sessionId);
    if (handle === undefined || handle.state === "stopped") return;
    handle.state = "crashed";
    console.error(
      `[plugin-host] REPL process for session '${sessionId}' generation ${generation} died: ` +
        `${reason instanceof Error ? reason.message : String(reason)}.`,
    );
    this.#repls.delete(sessionId);
    this.#emitExited(
      sessionId,
      handle.generation,
      handle.pid,
      reason instanceof Error ? reason.message : String(reason),
      handle.report,
    );
  }
}
