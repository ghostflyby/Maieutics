/**
 * Plugin host process logic: manages one permission-scoped worker per plugin
 * export module and multiplexes extension point invocations over postMessage
 * frames. The bus wiring lives in `mod.ts`; this module is testable without a
 * control channel.
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
  /** Absolute plugin directory; read access is always injected for it. */
  rootDir: string;
  workers: readonly PluginWorkerConfig[];
  permissions: PermissionGrants;
}

export interface RegisteredExtension {
  readonly pluginId: string;
  readonly exportName: string;
  readonly extensionPoint: string;
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
  const read: boolean | string[] = typeof declaredRead === "boolean"
      ? declaredRead
      : (() => {
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

class PluginWorker {
  readonly pluginId: string;
  readonly exportName: string;
  readonly extensionPoints = new Set<string>();

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
      this.#pending.set(id, {resolve, reject, timer});
    });
    worker.postMessage({type: "invoke", id, extensionPoint, request});
    return await done;
  }

  dispose(): void {
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(new Error("Plugin worker is shutting down."));
    }
    this.#pending.clear();
    this.#worker?.terminate();
    this.#worker = undefined;
  }

  async #startWorker(): Promise<void> {
    const entryUrl = this.#plugin.workers.find((worker) =>
        worker.exportName === this.exportName
    )
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
          id?: string | null;
        };
        if (frame.type === "ready") {
          for (const name of frame.extensionPoints ?? []) {
            this.extensionPoints.add(name);
          }
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
      });
    });
    await ready;
    worker.onmessage = (event: MessageEvent): void =>
        this.#handleMessage(event);
    worker.onerror = (event: ErrorEvent): void => this.#handleCrash(event);
  }

  #handleMessage(event: MessageEvent): void {
    const frame = event.data as {
      type: string;
      id?: string | null;
      value?: unknown;
      code?: string;
      message?: string;
    };
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
            `${frame.code ?? "extension_failed"}: ${
                frame.message ?? "the extension failed"
            }`,
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
}

export class PluginHost {
  readonly extensions: RegisteredExtension[] = [];

  #workers = new Map<string, PluginWorker>();
  #options: HostOptions;

  constructor(options: HostOptions) {
    this.#options = options;
    for (const plugin of options.plugins) {
      for (const workerConfig of plugin.workers) {
        const worker = new PluginWorker(
            plugin,
            workerConfig.exportName,
            options,
        );
        this.#workers.set(
            workerKey(plugin.id, workerConfig.exportName),
            worker,
        );
      }
    }
  }

  /** Starts every worker eagerly and collects the extension registry. */
  async startAll(): Promise<readonly RegisteredExtension[]> {
    const registrations: RegisteredExtension[] = [];
    for (const worker of this.#workers.values()) {
      await worker.start();
      for (const name of worker.extensionPoints) {
        registrations.push({
          pluginId: worker.pluginId,
          exportName: worker.exportName,
          extensionPoint: name,
        });
      }
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
    return await worker.invoke(extensionPoint, request);
  }

  dispose(): void {
    for (const worker of this.#workers.values()) {
      worker.dispose();
    }
    this.#workers.clear();
  }
}

function workerKey(pluginId: string, exportName: string): string {
  return `${pluginId}\u0000${exportName}`;
}
