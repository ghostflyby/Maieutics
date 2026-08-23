/**
 * Plugin host process logic: spawns one permission-scoped plugin worker per
 * export module with worker-actor's `spawn()`, owns the specifier→workerId
 * registry and the `__acquire-actor` main-side router, and orchestrates
 * dependency start order, cascade teardown, crash handling, and hot reload.
 * The bus wiring lives in `mod.ts`; this module is testable without a control
 * channel.
 *
 * Cross-plugin calls reuse worker-actor's reference-acquire machinery: a
 * consumer's first call posts `__acquire-actor { specifier, refId }`; the host
 * maps the specifier to its owner worker id, rewrites the refId prefix, and
 * answers with `__serve-actor` (owner) / `__ref-acquired` (consumer). After
 * acquisition the two workers talk directly; the host is out of the data path.
 */

import { type ActorHandle, type Remote, spawn } from "@ghostflyby/worker-actor";
import { actorRefCodec } from "../maieutics-plugin-sdk/actor_ref.ts";
import { collectionStreamCodec } from "../maieutics-plugin-sdk/collection_stream.ts";

/** Positive permission grant: `true` allows all, `false` denies, a list allows those entries. */
export type PermissionGrant = boolean | readonly string[];

export interface PermissionGrants {
  env?: PermissionGrant;
  net?: PermissionGrant;
  read?: PermissionGrant;
  write?: PermissionGrant;
  run?: PermissionGrant;
  ffi?: PermissionGrant;
  sys?: PermissionGrant;
  import?: PermissionGrant;
}

export interface PluginWorkerConfig {
  /** Export subpath, e.g. "./main"; stable identity within the plugin. */
  exportName: string;
  /** File URL of the plugin export module. */
  entryUrl: string;
  /** Canonical specifier of this export (`<name>/<subpath>`). */
  specifier: string;
}

export interface PluginConfig {
  id: string;
  /** Absolute plugin directory; read access is always injected for it. */
  rootDir: string;
  workers: readonly PluginWorkerConfig[];
  permissions: PermissionGrants;
  /** Declared dependency plugin ids (directory names), resolved by the kernel. */
  dependencies?: readonly string[];
}

export interface RegisteredExtension {
  readonly pluginId: string;
  readonly exportName: string;
  readonly extensionPoint: string;
  readonly specifier: string;
}

export interface PluginState {
  readonly pluginId: string;
  readonly exportName: string;
  readonly specifier: string;
  readonly state:
    | "starting"
    | "running"
    | "stopping"
    | "stopped"
    | "failed"
    | "disabled"
    | "crashed";
  readonly failure?: string;
}

export interface HostOptions {
  /** File URL of the SDK module (materialized by the kernel). */
  sdkUrl: string;
  /** File URL of the worker entry module (materialized by the kernel). */
  workerEntryUrl: string;
  plugins: readonly PluginConfig[];
  /** Invocation timeout per extension point call. */
  invokeTimeoutMs?: number;
  /** Maximum restarts per worker before it is disabled. */
  maxRestarts?: number;
  /** Cooperative teardown window per cascade wave. */
  stopGraceMs?: number;
}

const DEFAULT_INVOKE_TIMEOUT_MS = 15_000;
const DEFAULT_MAX_RESTARTS = 3;
const DEFAULT_STOP_GRACE_MS = 5_000;

function filePathOf(url: string): string {
  return decodeURIComponent(new URL(url).pathname);
}

function normalize(grant: PermissionGrant | undefined): boolean | string[] {
  if (typeof grant === "boolean" || grant === undefined) {
    return grant === true;
  }
  return [...grant];
}

function buildWorkerPermissions(
  plugin: PluginConfig,
  options: HostOptions,
): Deno.PermissionOptionsObject {
  const declaredRead = normalize(plugin.permissions.read);
  const read: boolean | string[] = typeof declaredRead === "boolean" ? declaredRead : (() => {
    const entries = new Set<string>(declaredRead);
    entries.add(plugin.rootDir);
    entries.add(filePathOf(options.sdkUrl));
    entries.add(filePathOf(options.workerEntryUrl));
    // No DENO_DIR read grant is needed: Deno resolves jsr:/npm: modules
    // internally without a filesystem read permission (the cache is not a
    // user-facing read target), and reading DENO_DIR/HOME here would require
    // env access the broker does not grant.
    return [...entries];
  })();
  return {
    env: normalize(plugin.permissions.env),
    net: normalize(plugin.permissions.net),
    read,
    write: normalize(plugin.permissions.write),
    run: normalize(plugin.permissions.run),
    ffi: normalize(plugin.permissions.ffi),
    sys: normalize(plugin.permissions.sys),
    import: normalize(plugin.permissions.import),
  };
}

/** The worker's RPC surface as seen by the host: extension points by name. */
interface WorkerRpc {
  [method: string]: (payload: unknown) => Promise<unknown>;
}

/** One contract identity exported by a worker's entry module: the export key
 * (what a dependency imports), the extension point name (what providers
 * address), the defining module URL (the identity's owner), and the element
 * kind (data or service). */
interface ContractExportIdentity {
  readonly exportName: string;
  readonly name: string;
  readonly owner: string;
  readonly serviceKind: "data" | "service";
}

/** Identity + runtime handle of one spawned worker. */
interface WorkerHandle {
  plugin: PluginConfig;
  config: PluginWorkerConfig;
  /** Canonical specifier of this worker's export; interop identity. */
  specifier: string;
  actor: Remote<WorkerRpc> & ActorHandle;
  worker: Worker;
  state: "starting" | "running" | "stopping" | "stopped" | "failed" | "disabled" | "crashed";
  failure?: string;
  extensionPoints: Set<string>;
  /** Contract identities (extension points) this worker's entry exports. */
  contractIdentities: ContractExportIdentity[];
  restarts: number;
}

const enum State {
  Starting = "starting",
  Running = "running",
  Stopping = "stopping",
  Stopped = "stopped",
  Failed = "failed",
  Disabled = "disabled",
  Crashed = "crashed",
}

function specifierOf(plugin: PluginConfig, worker: PluginWorkerConfig): string {
  return worker.specifier;
}

export class PluginHost {
  readonly extensions: RegisteredExtension[] = [];

  #workers = new Map<string, WorkerHandle>();
  #bySpecifier = new Map<string, string>(); // specifier → workerKey
  #options: HostOptions;
  #acquireRouterInstalled = false;

  constructor(options: HostOptions) {
    this.#options = options;
    for (const plugin of options.plugins) {
      for (const workerConfig of plugin.workers) {
        const key = workerKey(plugin.id, workerConfig.exportName);
        this.#workers.set(key, {
          plugin,
          config: workerConfig,
          specifier: specifierOf(plugin, workerConfig),
          actor: undefined as never,
          worker: undefined as never,
          state: State.Stopped,
          extensionPoints: new Set(),
          contractIdentities: [],
          restarts: 0,
        });
        this.#bySpecifier.set(workerConfig.specifier, key);
      }
    }
    this.#installAcquireRouter();
  }

  /** Starts every worker in topological waves (dependencies first) and collects the registry. */
  async startAll(): Promise<readonly RegisteredExtension[]> {
    const waves = this.#computeStartWaves();
    for (const wave of waves) {
      await Promise.all(wave.map((key) => this.#startWorker(key)));
    }
    const registrations = this.#collectExtensions();
    this.#refreshExtensions(registrations);
    return registrations;
  }

  /** Replaces the public registry snapshot with the current per-worker extension points. */
  #refreshExtensions(registrations: RegisteredExtension[]): void {
    this.extensions.length = 0;
    this.extensions.push(...registrations);
  }

  /** Invokes one extension point on the targeted plugin worker. */
  async invoke(
    pluginId: string,
    exportName: string,
    extensionPoint: string,
    request: unknown,
  ): Promise<unknown> {
    const handle = this.#requireHandle(workerKey(pluginId, exportName));
    if (handle.state !== State.Running) {
      throw new Error(
        `Plugin worker '${pluginId}/${exportName}' is not running (state ${handle.state}).`,
      );
    }
    if (!handle.extensionPoints.has(extensionPoint)) {
      throw new Error(
        `Extension point '${extensionPoint}' is not registered by '${pluginId}/${exportName}'.`,
      );
    }
    const actor = handle.actor as unknown as Record<string, (p: unknown) => Promise<unknown>>;
    const call = actor[extensionPoint];
    if (typeof call !== "function") {
      throw new Error(
        `Extension point '${extensionPoint}' has no remote callable on '${pluginId}/${exportName}'.`,
      );
    }
    return await call(request);
  }

  /** Snapshot of per-worker lifecycle states (backward compatible registry field). */
  states(): PluginState[] {
    const result: PluginState[] = [];
    for (const handle of this.#workers.values()) {
      result.push({
        pluginId: handle.plugin.id,
        exportName: handle.config.exportName,
        specifier: handle.specifier,
        state: handle.state,
        ...(handle.failure === undefined ? {} : { failure: handle.failure }),
      });
    }
    return result;
  }

  /** Cascade-disables one worker and every transitive dependent, then restarts topologically.
   * When a replacement {@link PluginConfig} is supplied (permission/config change), the worker's
   * plugin configuration is updated first so the rebuilt workers carry the new grants. */
  async reload(
    pluginId: string,
    exportName: string,
    nextConfig?: PluginConfig,
  ): Promise<void> {
    const key = workerKey(pluginId, exportName);
    const handle = this.#workers.get(key);
    if (handle === undefined) return;
    if (nextConfig !== undefined && nextConfig !== null) {
      handle.plugin = nextConfig;
      const nextWorker = nextConfig.workers.find((w) => w.exportName === exportName);
      if (nextWorker !== undefined) {
        // The rebuilt worker must load the replacement entry URL, not the
        // stale one from the pre-reload config.
        handle.config = { ...handle.config, ...nextWorker };
      }
      // Specifier identity is the worker's canonical interop name; a config
      // change may rename an entrypoint, so refresh the by-specifier map.
      this.#bySpecifier.delete(handle.specifier);
      handle.specifier = nextWorker?.specifier ?? handle.specifier;
      this.#bySpecifier.set(handle.specifier, key);
    }
    await this.#cascade(key);
    // Restart the whole cascaded closure (the worker plus its transitive
    // dependents) in topological waves; restarting only the target would leave
    // dependents permanently Stopped until the next host-process restart.
    await this.#startSubgraph(this.#dependencyClosure(key));
    this.#refreshExtensions(this.#collectExtensions());
  }

  /** Collects the current registry snapshot from every worker's extension points. */
  #collectExtensions(): RegisteredExtension[] {
    const registrations: RegisteredExtension[] = [];
    for (const key of this.#workers.keys()) {
      const handle = this.#requireHandle(key);
      for (const name of handle.extensionPoints) {
        registrations.push({
          pluginId: handle.plugin.id,
          exportName: handle.config.exportName,
          extensionPoint: name,
          specifier: handle.specifier,
        });
      }
    }
    return registrations;
  }

  dispose(): void {
    for (const handle of this.#workers.values()) {
      if (handle.actor !== undefined && handle.state !== State.Stopped) {
        try {
          void handle.actor.dispose();
        } catch {
          // best-effort shutdown
        }
      }
      handle.state = State.Stopped;
    }
  }

  // —— Acquire router (main side) ——

  #installAcquireRouter(): void {
    if (this.#acquireRouterInstalled) return;
    this.#acquireRouterInstalled = true;
    // The router is attached per spawned worker (see #attachAcquireListener);
    // nothing to install at construction.
  }

  /** Attach the acquire router to a freshly spawned worker. */
  #attachAcquireListener(worker: Worker): void {
    worker.addEventListener("message", (event: MessageEvent) => {
      const frame = event.data as {
        type?: string;
        specifier?: unknown;
        refId?: unknown;
        name?: unknown;
      };
      if (frame?.type !== "__acquire-actor") return;
      const specifier = frame.specifier;
      const refId = frame.refId;
      if (typeof specifier !== "string" || typeof refId !== "string") return;
      const requester = event.currentTarget as Worker;
      const name = typeof frame.name === "string" ? frame.name : undefined;
      this.#routeAcquire(requester, specifier, refId, name);
    });
  }

  /** Bootstraps the owner↔holder channel for a specifier acquire. */
  #routeAcquire(requester: Worker, specifier: string, refId: string, name?: string): void {
    const key = this.#bySpecifier.get(specifier);
    if (key === undefined) {
      return;
    }
    const owner = this.#requireHandle(key);
    if (owner.state !== State.Running || owner.worker === undefined) {
      console.error(
        `[plugin-host] acquire of '${specifier}' refused: owner is ${owner.state}.`,
      );
      return;
    }
    const { port1, port2 } = new MessageChannel();
    // serveWorker dispatches __serve-ref / __ref-acquired; the specifier rides
    // on the serve frame so the owner serves the right surface. The optional
    // name addresses a specific surface (a remote collection) when present;
    // the host stays agnostic and forwards it unchanged.
    owner.worker.postMessage(
      {
        type: "__serve-ref",
        refId,
        specifier,
        ...(name === undefined ? {} : { name }),
        port: port1,
      },
      { transfer: [port1] },
    );
    requester.postMessage(
      { type: "__ref-acquired", refId, port: port2 },
      { transfer: [port2] },
    );
  }

  // —— Lifecycle ——

  async #startWorker(key: string): Promise<void> {
    const handle = this.#requireHandle(key);
    if (handle.state === State.Running || handle.state === State.Starting) return;
    handle.state = State.Starting;
    handle.failure = undefined;
    try {
      const worker = new Worker(this.#options.workerEntryUrl, {
        type: "module",
        deno: {
          permissions: buildWorkerPermissions(handle.plugin, this.#options),
        },
      });
      this.#attachAcquireListener(worker);
      // The worker's SDK reads its specifier and the actor-entry registry of
      // its declared dependencies from this per-worker message. It must be
      // posted before spawn() runs the handshake: the SDK awaits the config
      // before calling serveWorker, which is what sends the handshake frame.
      worker.postMessage({
        type: "maieutics-config",
        payload: {
          specifier: handle.specifier,
          actorEntries: this.#dependencyActorEntries(handle),
        },
      });
      const actor = await spawn<WorkerRpc>(worker, {
        codecs: [actorRefCodec, collectionStreamCodec],
        signal: AbortSignal.timeout(this.#options.invokeTimeoutMs ?? DEFAULT_INVOKE_TIMEOUT_MS),
        onDeath: (reason) => this.#handleDeath(key, reason),
      });
      handle.actor = actor;
      handle.worker = worker;
      await this.#initWorker(key);
      handle.state = State.Running;
    } catch (error) {
      handle.state = State.Failed;
      handle.failure = error instanceof Error ? error.message : String(error);
      if (handle.worker !== undefined) {
        try {
          handle.worker.terminate();
        } catch {
          // already gone
        }
      }
      throw error;
    }
  }

  #initWorker(key: string): Promise<void> {
    const handle = this.#requireHandle(key);
    const ready = new Promise<void>((resolve, reject) => {
      const timeout = setTimeout(() => {
        reject(new Error(`Plugin worker '${key}' did not become ready.`));
      }, this.#options.invokeTimeoutMs ?? DEFAULT_INVOKE_TIMEOUT_MS);
      // addEventListener coexists with spawn's onmessage property (which owns
      // the RPC response channel); the ready/init-error frames are host-side.
      const onMessage = (event: MessageEvent): void => {
        const frame = event.data as {
          type?: string;
          extensionPoints?: string[];
          contractIdentities?: ContractExportIdentity[];
          message?: string;
        };
        if (frame?.type === "ready") {
          handle.worker.removeEventListener("message", onMessage);
          for (const name of frame.extensionPoints ?? []) handle.extensionPoints.add(name);
          handle.contractIdentities = frame.contractIdentities ?? [];
          clearTimeout(timeout);
          resolve();
        } else if (frame?.type === "init-error") {
          handle.worker.removeEventListener("message", onMessage);
          clearTimeout(timeout);
          reject(new Error(frame.message ?? "Plugin worker failed to initialize."));
        }
      };
      handle.worker.addEventListener("message", onMessage);
    });
    handle.worker.postMessage({
      type: "init",
      entryUrl: handle.config.entryUrl,
      specifier: handle.specifier,
    });
    return ready;
  }

  #handleDeath(key: string, reason: unknown): void {
    const handle = this.#workers.get(key);
    if (handle === undefined || handle.state === State.Stopped) return;
    handle.state = State.Crashed;
    handle.failure = reason instanceof Error ? reason.message : String(reason);
    handle.restarts += 1;
    if (handle.restarts > (this.#options.maxRestarts ?? DEFAULT_MAX_RESTARTS)) {
      handle.state = State.Disabled;
    }
    void this.#cascade(key);
  }

  async #cascade(rootKey: string): Promise<void> {
    const closure = this.#dependencyClosure(rootKey);
    // Reverse-topological waves: leaves first, wave-parallel, waves-serial.
    const waves = this.#reverseWaves(closure);
    for (const wave of waves) {
      await Promise.all(wave.map((key) => this.#stopWorker(key)));
    }
  }

  async #stopWorker(key: string): Promise<void> {
    const handle = this.#workers.get(key);
    if (handle === undefined || handle.state === State.Stopped || handle.state === State.Failed) {
      return;
    }
    // Before the worker dies, tell every dependency it contributes to that this
    // provider is gone, so the definers drop its remote contributions (the
    // provider's change stream would otherwise hang and the value would linger).
    this.#notifyProviderDead(handle);
    handle.state = State.Stopping;
    const grace = this.#options.stopGraceMs ?? DEFAULT_STOP_GRACE_MS;
    const stopped = Promise.race([
      handle.actor?.dispose().catch(() => {}),
      new Promise<void>((resolve) => setTimeout(resolve, grace)),
    ]);
    await stopped;
    handle.state = State.Stopped;
    handle.extensionPoints.clear();
    handle.contractIdentities = [];
  }

  /** Notifies every worker that `stopped` contributes to (its declared
   * dependencies) that the provider is dead, so they drop its contributions.
   * The notification rides the worker-actor RPC surface (a bare postMessage
   * frame would be ignored by the worker runtime's onmessage dispatcher). */
  #notifyProviderDead(stopped: WorkerHandle): void {
    const declared = new Set(stopped.plugin.dependencies ?? []);
    for (const candidate of this.#workers.values()) {
      if (candidate.plugin.id !== stopped.plugin.id && declared.has(candidate.plugin.id)) {
        if (candidate.state === State.Running && candidate.actor !== undefined) {
          try {
            void (candidate.actor as unknown as Record<
              string,
              (specifier: string) => Promise<unknown>
            >)["__maieuticsProviderDead"](stopped.specifier).catch((error: unknown) => {
              console.error(
                `[plugin-host] provider-dead notification to '${candidate.specifier}' failed: ` +
                  `${error instanceof Error ? error.message : String(error)}`,
              );
            });
          } catch {
            // best-effort: the definer may already be stopping
          }
        }
      }
    }
  }

  async #startSubgraph(keys: string[]): Promise<void> {
    const waves = this.#computeStartWaves(keys);
    for (const wave of waves) {
      await Promise.all(
        wave.map((key) =>
          this.#startWorker(key).catch((error) => {
            const handle = this.#workers.get(key);
            if (handle) handle.state = State.Failed;
          })
        ),
      );
    }
  }

  // —— Dependency graph (waves) ——

  /**
   * The actor-entry registry of this worker's declared dependencies: for each
   * dependency plugin id, the canonical specifier and the actual entry file
   * URL of every worker that plugin runs. Both come from spawn-time known
   * data (the plugin manifest exports), never from reading module sources.
   * The consumer worker's load hook uses this to decide which import edges
   * carry actor semantics and must be redirected. The dependency worker's
   * contract identities ride along so the consumer can synthesize stub
   * identity exports for them.
   */
  #dependencyActorEntries(handle: WorkerHandle): Array<{
    specifier: string;
    entryUrl: string;
    identities?: ContractExportIdentity[];
  }> {
    const result: Array<{
      specifier: string;
      entryUrl: string;
      identities?: ContractExportIdentity[];
    }> = [];
    const declared = new Set(handle.plugin.dependencies ?? []);
    for (const candidate of this.#workers.values()) {
      if (candidate.plugin.id !== handle.plugin.id && declared.has(candidate.plugin.id)) {
        if (candidate.specifier.length > 0) {
          result.push({
            specifier: candidate.specifier,
            entryUrl: candidate.config.entryUrl,
            ...(candidate.contractIdentities.length === 0
              ? {}
              : { identities: candidate.contractIdentities }),
          });
        }
      }
    }
    return result;
  }

  #dependencyOf(key: string): string[] {
    const handle = this.#workers.get(key);
    if (handle === undefined) return [];
    const result: string[] = [];
    for (const depId of handle.plugin.dependencies ?? []) {
      for (const [candidateKey, candidate] of this.#workers) {
        if (candidate.plugin.id === depId) result.push(candidateKey);
      }
    }
    return result;
  }

  #computeStartWaves(scope?: string[]): string[][] {
    const keys = scope ?? [...this.#workers.keys()];
    const remaining = new Set(keys);
    const waves: string[][] = [];
    while (remaining.size > 0) {
      const wave = [...remaining].filter((key) =>
        this.#dependencyOf(key).every((dep) => !remaining.has(dep))
      );
      if (wave.length === 0) {
        // Cycle: break it deterministically (start everything left).
        wave.push(...remaining);
      }
      for (const key of wave) remaining.delete(key);
      waves.push(wave);
    }
    return waves;
  }

  #dependencyClosure(rootKey: string): string[] {
    const closure = new Set<string>([rootKey]);
    let grew = true;
    while (grew) {
      grew = false;
      for (const [key, handle] of this.#workers) {
        if (closure.has(key)) continue;
        if (this.#dependencyOf(key).some((dep) => closure.has(dep))) {
          closure.add(key);
          grew = true;
        }
      }
    }
    return [...closure];
  }

  #reverseWaves(closure: string[]): string[][] {
    const remaining = new Set(closure);
    const waves: string[][] = [];
    while (remaining.size > 0) {
      const wave = [...remaining].filter((key) =>
        this.#dependencyOf(key).every((dep) => !remaining.has(dep))
      );
      if (wave.length === 0) {
        wave.push(...remaining);
      }
      for (const key of wave) remaining.delete(key);
      waves.push(wave);
    }
    return waves;
  }

  #requireHandle(key: string): WorkerHandle {
    const handle = this.#workers.get(key);
    if (handle === undefined) {
      throw new Error(`No worker for key '${key}'.`);
    }
    return handle;
  }
}

function workerKey(pluginId: string, exportName: string): string {
  return `${pluginId}\u0000${exportName}`;
}
