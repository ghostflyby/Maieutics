/**
 * Plugin host process logic: manages one permission-scoped worker per plugin
 * export module and multiplexes extension point invocations over postMessage
 * frames. Plugins declare `maieutics.dependencies`; the host derives a
 * deterministic topological start order, starts dependencies first, cascades
 * teardown in reverse-topological waves when a plugin is disabled, crashes, or
 * reloads, and routes cross-plugin actor acquires between workers. The bus
 * wiring lives in `mod.ts`; this module is testable without a control channel.
 */

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
  /** Export subpath, e.g. "./mcp"; stable identity within the plugin. */
  exportName: string;
  /** File URL of the plugin export module. */
  entryUrl: string;
}

export interface PluginConfig {
  id: string;
  /** Package name from deno.json (`@scope/name`); the cross-plugin specifier root. */
  name: string;
  /** Absolute plugin directory; read access is always injected for it. */
  rootDir: string;
  workers: readonly PluginWorkerConfig[];
  permissions: PermissionGrants;
  /** Declared dependency plugin ids (directory names). */
  dependencies?: readonly string[];
}

export interface RegisteredExtension {
  readonly pluginId: string;
  readonly exportName: string;
  readonly extensionPoint: string;
}

export type PluginState = "stopped" | "starting" | "running" | "stopping";

export interface PluginSnapshot {
  readonly pluginId: string;
  readonly state: PluginState | "disabled" | "failed";
  readonly reason?: string;
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
}

const DEFAULT_INVOKE_TIMEOUT_MS = 15_000;
const DEFAULT_MAX_RESTARTS = 3;

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

interface PendingInvoke {
  timer: ReturnType<typeof setTimeout>;
  resolve(value: unknown): void;
  reject(error: Error): void;
}

interface RuntimeDependency {
  readonly pluginId: string;
  readonly exportName: string;
  /** Number of live acquire channels between consumer and owner. */
  readonly connections: number;
}

class PluginWorker {
  readonly pluginId: string;
  readonly exportName: string;
  readonly extensionPoints = new Set<string>();
  /** Top-level export names reported by the owner, for cross-plugin stubs. */
  exportNames: string[] = [];
  /** Dependency specifier → entry URL and known export names (set by the host before start). */
  redirectTable = new Map<string, { entryUrl: string; exportNames: string[] }>();
  /** Package names of every plugin known to the host, to reject undeclared cross-plugin imports. */
  knownPluginSpecifiers: string[] = [];
  /** Cross-plugin consumers currently acquiring this worker (pluginId/exportName → count). */
  readonly runtimeDependents = new Map<string, number>();
  readonly specifier: string;

  #worker: Worker | undefined;
  #pending = new Map<string, PendingInvoke>();
  #restarts = 0;
  #disabled = false;
  #started: Promise<void> | undefined;
  #options: HostOptions;
  #plugin: PluginConfig;

  constructor(
    plugin: PluginConfig,
    exportName: string,
    options: HostOptions,
  ) {
    this.pluginId = plugin.id;
    this.exportName = exportName;
    this.#plugin = plugin;
    this.#options = options;
    this.specifier = specifierOf(plugin, exportName);
  }

  get disabled(): boolean {
    return this.#disabled;
  }

  /** Starts the worker and waits for its extension point scan. */
  async start(): Promise<void> {
    if (this.#started !== undefined) {
      return this.#started;
    }
    this.#started = this.#startWorker().catch((error) => {
      this.#started = undefined;
      throw error;
    });
    return this.#started;
  }

  async invoke(extensionPoint: string, request: unknown): Promise<unknown> {
    await this.start();
    if (this.#disabled) {
      throw new Error(
        `Plugin worker '${this.pluginId}/${this.exportName}' is disabled after repeated crashes.`,
      );
    }
    if (!this.extensionPoints.has(extensionPoint)) {
      throw new Error(
        `Extension point '${extensionPoint}' is not registered by '${this.pluginId}/${this.exportName}'.`,
      );
    }
    const worker = this.#worker;
    if (worker === undefined) {
      throw new Error(
        `Plugin worker '${this.pluginId}/${this.exportName}' is not running.`,
      );
    }
    const id = crypto.randomUUID();
    const done = new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(
          new Error(`Extension point '${extensionPoint}' timed out.`),
        );
      }, this.#options.invokeTimeoutMs ?? DEFAULT_INVOKE_TIMEOUT_MS);
      this.#pending.set(id, { resolve, reject, timer });
    });
    worker.postMessage({ type: "invoke", id, extensionPoint, request });
    return await done;
  }

  /** Stops the worker and resets its start state so it can be started again (reload). */
  dispose(): void {
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(new Error("Plugin worker is shutting down."));
    }
    this.#pending.clear();
    const worker = this.#worker;
    this.#worker = undefined;
    this.#started = undefined;
    this.#restarts = 0;
    this.#disabled = false;
    this.extensionPoints.clear();
    if (worker !== undefined) {
      // Detach first so the termination cannot route through the crash handler.
      worker.onmessage = null;
      worker.onerror = null;
      worker.terminate();
    }
  }

  async #startWorker(): Promise<void> {
    const entryUrl = this.#plugin.workers.find((worker) => worker.exportName === this.exportName)
      ?.entryUrl;
    if (entryUrl === undefined) {
      throw new Error(
        `Worker '${this.exportName}' of plugin '${this.pluginId}' has no entry URL.`,
      );
    }
    const worker = new Worker(this.#options.workerEntryUrl, {
      type: "module",
      deno: {
        permissions: buildWorkerPermissions(
          this.#plugin,
          this.#options,
        ),
      },
    });
    this.#worker = worker;
    const ready = new Promise<void>((resolve, reject) => {
      const timeout = setTimeout(() => {
        worker.terminate();
        reject(
          new Error(
            `Plugin worker '${this.pluginId}/${this.exportName}' did not become ready.`,
          ),
        );
      }, this.#options.invokeTimeoutMs ?? DEFAULT_INVOKE_TIMEOUT_MS);
      worker.onmessage = (event: MessageEvent): void => {
        const frame = event.data as {
          type: string;
          extensionPoints?: string[];
          exportNames?: string[];
          id?: string | null;
        };
        if (frame.type === "ready") {
          for (const name of frame.extensionPoints ?? []) {
            this.extensionPoints.add(name);
          }
          this.exportNames = frame.exportNames ?? [];
          clearTimeout(timeout);
          resolve();
        } else if (
          frame.type === "error" &&
          (frame.id === null || frame.id === undefined)
        ) {
          clearTimeout(timeout);
          reject(
            new Error(
              `Plugin worker failed to initialize: ${JSON.stringify(frame)}`,
            ),
          );
        }
      };
      worker.onerror = (event: ErrorEvent): void => {
        clearTimeout(timeout);
        this.#worker = undefined;
        reject(new Error(`Plugin worker crashed: ${event.message}`));
      };
      worker.postMessage({
        type: "init",
        entryUrl,
        selfSpecifier: this.specifier,
        redirectTable: Object.fromEntries(
          [...this.redirectTable].map(([specifier, entry]) => [
            specifier,
            entry,
          ]),
        ),
        knownPluginSpecifiers: [...this.knownPluginSpecifiers],
      });
    });
    await ready;
    worker.onmessage = (event: MessageEvent): void => this.#handleMessage(event);
    worker.onerror = (event: ErrorEvent): void => this.#handleCrash(event);
  }

  #handleMessage(event: MessageEvent): void {
    const frame = event.data as {
      type: string;
      id?: string | null;
      value?: unknown;
      code?: string;
      message?: string;
      refId?: string;
      specifier?: string;
    };
    if (frame.type === "acquire-actor" && frame.refId !== undefined) {
      const acquire = this.onAcquire;
      if (acquire !== undefined) {
        acquire({
          refId: frame.refId,
          specifier: frame.specifier ?? "",
        });
      }
      return;
    }
    if (frame.type === "serve-error" && frame.refId !== undefined) {
      this.onServeError?.(frame.refId, frame.message ?? "The plugin actor could not be served.");
      return;
    }
    if (frame.id === undefined || frame.id === null) {
      return;
    }
    const pending = this.#pending.get(frame.id);
    if (pending === undefined) {
      return;
    }
    this.#pending.delete(frame.id);
    clearTimeout(pending.timer);
    if (frame.type === "result") {
      pending.resolve(frame.value);
    } else if (frame.type === "error") {
      pending.reject(
        new Error(
          `${frame.code ?? "extension_failed"}: ${frame.message ?? "the extension failed"}`,
        ),
      );
    }
  }

  #handleCrash(event: ErrorEvent): void {
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(
        new Error(`Plugin worker crashed: ${event.message}`),
      );
    }
    this.#pending.clear();
    this.#worker = undefined;
    this.#started = undefined;
    this.#restarts += 1;
    if (
      this.#restarts > (this.#options.maxRestarts ?? DEFAULT_MAX_RESTARTS)
    ) {
      this.#disabled = true;
    }
  }

  onAcquire?: (request: {
    refId: string;
    specifier: string;
  }) => void;

  /** Called when this worker reports an owner-side serve failure to the host. */
  onServeError?: (refId: string, error: string) => void;

  /** Posts a message to the underlying worker (used for acquire replies and serve frames). */
  postToWorker(message: unknown, transfer?: Transferable[]): void {
    if (this.#worker === undefined) return;
    if (transfer === undefined) {
      this.#worker.postMessage(message);
    } else {
      this.#worker.postMessage(message, transfer);
    }
  }

  /** Records a live cross-plugin connection from a consumer to this worker. */
  addRuntimeDependent(pluginId: string, exportName: string): void {
    const key = `${pluginId}/${exportName}`;
    this.runtimeDependents.set(key, (this.runtimeDependents.get(key) ?? 0) + 1);
  }

  /** Drops one live cross-plugin connection from a consumer. */
  removeRuntimeDependent(pluginId: string, exportName: string): void {
    const key = `${pluginId}/${exportName}`;
    const count = this.runtimeDependents.get(key);
    if (count === undefined || count <= 1) {
      this.runtimeDependents.delete(key);
      return;
    }
    this.runtimeDependents.set(key, count - 1);
  }
}

export interface HostCallbacks {
  /** Called whenever the registry or plugin states change. */
  onRegistry?(snapshot: {
    registrations: readonly RegisteredExtension[];
    plugins: readonly PluginSnapshot[];
  }): void;
}

export class PluginHost {
  readonly extensions: RegisteredExtension[] = [];

  #workers = new Map<string, PluginWorker>();
  #plugins = new Map<string, PluginConfig>();
  #options: HostOptions;
  #callbacks: HostCallbacks;
  /** Topological start order of plugin ids. */
  #startOrder: string[] = [];
  #dependents = new Map<string, string[]>();
  #state = new Map<string, PluginState | "disabled" | "failed">();
  #reasons = new Map<string, string>();
  #stopping = new Map<string, Promise<void>>();
  #acquireConsumers = new Map<string, PluginWorker>();

  constructor(options: HostOptions, callbacks: HostCallbacks = {}) {
    this.#options = options;
    this.#callbacks = callbacks;
    const pluginNames = options.plugins.map((plugin) => plugin.name);
    for (const plugin of options.plugins) {
      this.#plugins.set(plugin.id, plugin);
      for (const workerConfig of plugin.workers) {
        const worker = new PluginWorker(
          plugin,
          workerConfig.exportName,
          options,
        );
        worker.knownPluginSpecifiers = pluginNames;
        worker.onAcquire = (request) => {
          void this.#routeAcquire(request, worker);
        };
        worker.onServeError = (refId, error) => {
          this.#handleServeError(refId, error);
        };
        this.#workers.set(
          workerKey(plugin.id, workerConfig.exportName),
          worker,
        );
      }
    }
    this.#buildDependencyOrder();
    // Seed redirect tables from declared dependencies; export names are filled
    // in by the host just before a worker starts (dependencies start first).
    for (const worker of this.#workers.values()) {
      const plugin = this.#plugins.get(worker.pluginId);
      if (plugin === undefined) continue;
      for (const dependencyId of plugin.dependencies ?? []) {
        const dependency = this.#plugins.get(dependencyId);
        if (dependency === undefined) continue;
        for (const depWorker of dependency.workers) {
          worker.redirectTable.set(specifierOf(dependency, depWorker.exportName), {
            entryUrl: depWorker.entryUrl,
            exportNames: [],
          });
        }
      }
    }
  }

  #buildDependencyOrder(): void {
    const known = new Set(this.#plugins.keys());
    const reasons = new Map<string, string>();
    for (const plugin of this.#plugins.values()) {
      for (const dependency of plugin.dependencies ?? []) {
        if (!known.has(dependency)) {
          reasons.set(plugin.id, `missing_dependency:${dependency}`);
          break;
        }
      }
    }
    const propagate = (): boolean => {
      let changed = false;
      for (const plugin of this.#plugins.values()) {
        if (reasons.has(plugin.id)) continue;
        for (const dependency of plugin.dependencies ?? []) {
          if (reasons.has(dependency)) {
            reasons.set(plugin.id, `dependency_excluded:${dependency}`);
            changed = true;
            break;
          }
        }
      }
      return changed;
    };
    while (propagate()) {}

    const remaining = [...this.#plugins.values()]
      .filter((plugin) => !reasons.has(plugin.id));
    const dependents = new Map<string, string[]>();
    for (const plugin of remaining) dependents.set(plugin.id, []);
    for (const plugin of remaining) {
      for (const dependency of plugin.dependencies ?? []) {
        const list = dependents.get(dependency);
        if (list !== undefined && !list.includes(plugin.id)) {
          list.push(plugin.id);
        }
      }
    }
    const inDegree = new Map(
      remaining.map((plugin) => [plugin.id, plugin.dependencies?.length ?? 0]),
    );
    const ready = new Set<string>();
    for (const plugin of remaining) {
      if ((plugin.dependencies?.length ?? 0) === 0) ready.add(plugin.id);
    }
    const order: string[] = [];
    const pending = new Set(ready);
    while (pending.size > 0) {
      const wave = [...pending].sort();
      pending.clear();
      for (const id of wave) {
        order.push(id);
        for (const dependent of dependents.get(id) ?? []) {
          inDegree.set(dependent, (inDegree.get(dependent) ?? 0) - 1);
          if ((inDegree.get(dependent) ?? 0) === 0) pending.add(dependent);
        }
      }
    }
    const leftover = remaining
      .filter((plugin) => (inDegree.get(plugin.id) ?? 0) > 0)
      .map((plugin) => plugin.id);
    for (const id of leftover) {
      reasons.set(id, "dependency_cycle");
    }

    this.#startOrder = order.filter((id) => !reasons.has(id));
    this.#dependents = dependents;
    for (const id of this.#startOrder) {
      this.#state.set(id, "stopped");
    }
    for (const [id, reason] of reasons) {
      this.#state.set(id, "failed");
      this.#reasons.set(id, reason);
    }
  }

  stateOf(pluginId: string): PluginState | "disabled" | "failed" {
    return this.#state.get(pluginId) ?? "stopped";
  }

  reasonOf(pluginId: string): string | undefined {
    return this.#reasons.get(pluginId);
  }

  snapshots(): readonly PluginSnapshot[] {
    return this.#startOrder.map((id) => ({
      pluginId: id,
      state: this.#state.get(id) ?? "stopped",
      reason: this.#reasons.get(id),
    })).concat(
      [...this.#reasons.entries()]
        .filter(([id]) => !this.#startOrder.includes(id))
        .map(([id, reason]) => ({
          pluginId: id,
          state: "failed" as const,
          reason,
        })),
    );
  }

  /** Starts every eligible plugin in topological waves. */
  async startAll(): Promise<readonly RegisteredExtension[]> {
    const registrations: RegisteredExtension[] = [];
    const failed = new Set<string>();
    for (const pluginId of this.#startOrder) {
      if (failed.has(pluginId)) {
        continue;
      }
      const state = this.#state.get(pluginId) ?? "stopped";
      if (state === "running") {
        for (const worker of this.workersOf(pluginId)) {
          for (const name of worker.extensionPoints) {
            registrations.push({
              pluginId: worker.pluginId,
              exportName: worker.exportName,
              extensionPoint: name,
            });
          }
        }
        continue;
      }
      const dependents = this.#dependents.get(pluginId) ?? [];
      for (const dependent of dependents) {
        if (this.#state.get(dependent) === "starting") {
          failed.add(dependent);
          this.#reasons.set(dependent, `dependency_failed:${pluginId}`);
        }
      }
      this.#state.set(pluginId, "starting");
      try {
        for (const worker of this.workersOf(pluginId)) {
          this.#refreshRedirects(worker);
          await worker.start();
        }
        this.#state.set(pluginId, "running");
        for (const worker of this.workersOf(pluginId)) {
          for (const name of worker.extensionPoints) {
            registrations.push({
              pluginId: worker.pluginId,
              exportName: worker.exportName,
              extensionPoint: name,
            });
          }
        }
      } catch (error) {
        this.#state.set(pluginId, "failed");
        this.#reasons.set(
          pluginId,
          error instanceof Error ? error.message : String(error),
        );
        failed.add(pluginId);
        for (const dependent of dependents) {
          failed.add(dependent);
          this.#reasons.set(dependent, `dependency_failed:${pluginId}`);
        }
      }
      this.#emitRegistry();
    }
    this.extensions.length = 0;
    this.extensions.push(...registrations);
    return registrations;
  }

  /** Invokes one extension point on the targeted plugin worker. */
  async invoke(
    pluginId: string,
    exportName: string,
    extensionPoint: string,
    request: unknown,
  ): Promise<unknown> {
    const worker = this.#workers.get(workerKey(pluginId, exportName));
    if (worker === undefined) {
      throw new Error(
        `No worker for plugin '${pluginId}' export '${exportName}'.`,
      );
    }
    const state = this.#state.get(pluginId) ?? "stopped";
    if (state === "stopping") {
      throw new Error(
        `Plugin '${pluginId}' is stopping (plugin_reloading).`,
      );
    }
    return await worker.invoke(extensionPoint, request);
  }

  /** Stops a plugin and everything that transitively depends on it. */
  async disable(pluginId: string): Promise<void> {
    await this.#stopCascade(pluginId, `disabled`);
  }

  /**
   * Stops the reloaded plugin and its dependents, then starts them again in
   * topological order. Returns the new registration snapshot.
   */
  async reload(pluginIds: readonly string[]): Promise<readonly RegisteredExtension[]> {
    for (const pluginId of pluginIds) {
      if (this.#reasons.has(pluginId)) continue;
      await this.#stopCascade(pluginId, `reload`);
    }
    const registrations = await this.startAll();
    // startAll skips already-running plugins without emitting; always publish the
    // final snapshot so the kernel sees the post-reload registry.
    this.#emitRegistry();
    return registrations;
  }

  dispose(): void {
    for (const worker of this.#workers.values()) {
      worker.dispose();
    }
    this.#workers.clear();
  }

  workersOf(pluginId: string): PluginWorker[] {
    return [...this.#workers.values()].filter(
      (worker) => worker.pluginId === pluginId,
    );
  }

  async #stopCascade(pluginId: string, cause: string): Promise<void> {
    const closure = this.transitiveDependentsOf(pluginId);
    const targets = [pluginId, ...closure];
    const remaining = new Set(targets);
    while (remaining.size > 0) {
      const wave = [...remaining]
        .filter((id) => {
          const dependents = this.#dependents.get(id) ?? [];
          return dependents.every(
            (dependent) => !remaining.has(dependent),
          );
        })
        .sort();
      if (wave.length === 0) break;
      for (const id of wave) {
        remaining.delete(id);
        if (this.#state.get(id) !== "running" && this.#state.get(id) !== "stopping") {
          this.#state.set(id, "stopped");
          continue;
        }
        if (this.#stopping.has(id)) {
          await this.#stopping.get(id);
          continue;
        }
        const stopping = this.#stopWorker(id, cause);
        this.#stopping.set(id, stopping);
        try {
          await stopping;
        } finally {
          this.#stopping.delete(id);
        }
      }
      this.#emitRegistry();
    }
  }

  async #stopWorker(pluginId: string, cause: string): Promise<void> {
    this.#state.set(pluginId, "stopping");
    for (const worker of this.workersOf(pluginId)) {
      // No cooperative cancellation protocol exists yet; termination is bounded and
      // immediate (in-flight calls were already rejected by dispose).
      worker.dispose();
    }
    this.#state.set(pluginId, "stopped");
    this.#reasons.set(pluginId, cause);
    this.#emitRegistry();
  }

  transitiveDependentsOf(pluginId: string): string[] {
    const closure: string[] = [];
    const visited = new Set<string>([pluginId]);
    const frontier = [pluginId];
    while (frontier.length > 0) {
      const current = frontier.pop()!;
      for (const dependent of this.#dependents.get(current) ?? []) {
        if (visited.has(dependent)) continue;
        visited.add(dependent);
        closure.push(dependent);
        frontier.push(dependent);
      }
    }
    return closure;
  }

  async #routeAcquire(
    request: {
      refId: string;
      specifier: string;
    },
    consumer: PluginWorker,
  ): Promise<void> {
    if (!consumer.redirectTable.has(request.specifier)) {
      consumer.postToWorker({
        type: "actor-acquired",
        refId: request.refId,
        error: `Plugin dependency '${request.specifier}' is not declared by '${consumer.pluginId}'.`,
      });
      return;
    }
    const owner = [...this.#workers.values()].find(
      (worker) => worker.specifier === request.specifier,
    );
    if (owner === undefined) {
      consumer.postToWorker({
        type: "actor-acquired",
        refId: request.refId,
        error: `Plugin dependency '${request.specifier}' has no running worker.`,
      });
      return;
    }
    const ownerState = this.#state.get(owner.pluginId) ?? "stopped";
    if (ownerState !== "running") {
      consumer.postToWorker({
        type: "actor-acquired",
        refId: request.refId,
        error: `Plugin dependency '${request.specifier}' is not running (state '${ownerState}').`,
      });
      return;
    }
    const channel = new MessageChannel();
    this.#acquireConsumers.set(request.refId, consumer);
    owner.addRuntimeDependent(consumer.pluginId, consumer.exportName);
    consumer.postToWorker(
      {
        type: "actor-acquired",
        refId: request.refId,
        port: channel.port2,
      },
      [channel.port2],
    );
    owner.postToWorker(
      {
        type: "serve-actor",
        refId: request.refId,
        port: channel.port1,
      },
      [channel.port1],
    );
    this.#acquireConsumers.delete(request.refId);
  }

  /** Forwards an owner-side serve failure to the waiting consumer if it has not been answered yet. */
  #handleServeError(refId: string, error: string): void {
    const consumer = this.#acquireConsumers.get(refId);
    if (consumer === undefined) return;
    this.#acquireConsumers.delete(refId);
    consumer.postToWorker({
      type: "actor-acquired",
      refId,
      error,
    });
  }

  /** Fills dependency export names into a worker's redirect table before it starts. */
  #refreshRedirects(consumer: PluginWorker): void {
    for (const [specifier, entry] of consumer.redirectTable) {
      const owner = [...this.#workers.values()].find(
        (worker) => worker.specifier === specifier,
      );
      if (owner !== undefined) entry.exportNames = owner.exportNames;
    }
  }

  #emitRegistry(): void {
    this.#callbacks.onRegistry?.({
      registrations: this.extensions,
      plugins: this.snapshots(),
    });
  }
}

function workerKey(pluginId: string, exportName: string): string {
  return `${pluginId}\u0000${exportName}`;
}

function specifierOf(plugin: PluginConfig, exportName: string): string {
  const subpath = exportName === "." ? "" : exportName.replace(/^\.\//, "");
  return subpath === "" ? plugin.name : `${plugin.name}/${subpath}`;
}
