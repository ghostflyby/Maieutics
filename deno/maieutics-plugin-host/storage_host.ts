/**
 * Authoritative per-plugin storage for the plugin host main isolate (ADR 0022).
 *
 * Every plugin owns one store, shared by all of its workers (each entrypoint
 * worker and every nested worker is the same origin). The host main isolate is
 * the single writer: it keeps the in-memory map, enforces the quota, and
 * persists asynchronously to the kernel-assigned per-plugin data directory.
 * Plugin workers never touch the filesystem for storage — the channel works
 * with zero Deno permissions.
 *
 * Request routing is identity-safe by construction: `handle()` receives the
 * plugin the FRAME SENDER was resolved to (the host maps the worker to its
 * owning plugin; a client-declared id is never consulted) and binds each
 * mailbox to that plugin on first sight. A mailbox that reappears under a
 * different plugin is rejected, so a mailbox handed across plugins through an
 * actor port cannot borrow another plugin's store.
 *
 * Persistence is a debounced atomic write (temp file + rename) of one
 * versioned JSON document per plugin. The in-memory map is authoritative for
 * worker-facing reads, so hot reload keeps storage continuous even before a
 * flush lands. `flushAll()` bounds the shutdown budget; `dispose()` cancels
 * pending debounced flushes without writing.
 */

import {
  readStorageRequest,
  STORAGE_QUOTA_LENGTH,
  type StorageMailbox,
  storageMailboxFor,
  type StorageRequest,
  type StorageResponse,
  writeStorageResponse,
} from "../maieutics-runtime/storage_channel.ts";

/** Version of the persisted per-plugin storage document. */
export const STORAGE_FORMAT_VERSION = 1;

/** File name inside the per-plugin data directory. */
export const STORAGE_FILE_NAME = "local-storage.json";

const DEFAULT_FLUSH_DEBOUNCE_MS = 100;

interface PluginStoreRecord {
  map: Map<string, string>;
  /** Sum of key.length + value.length over all entries (UTF-16 units). */
  totalLength: number;
  /** Monotonic mutation counter; `flushedGeneration` trails it. */
  generation: number;
  flushedGeneration: number;
  loaded: boolean;
  loading: Promise<void> | undefined;
  flushTimer: ReturnType<typeof setTimeout> | undefined;
}

/** The slice of PluginConfig the responder needs per frame. */
export interface PluginStorageOwner {
  id: string;
  storage?: { readonly dataDir: string };
}

export interface PluginStorageHostOptions {
  /** Debounce window for persistence after a mutation. */
  flushDebounceMs?: number;
}

export class PluginStorageHost {
  readonly #stores = new Map<string, PluginStoreRecord>();
  /** Mailbox → owning plugin id, bound on first sight and validated after. */
  readonly #mailboxes = new Map<SharedArrayBuffer, string>();
  /** Plugin id → kernel-assigned data directory, captured at first use so
   * flushes target one directory even if a later frame omits or changes it. */
  readonly #dataDirectories = new Map<string, string>();
  readonly #flushDebounceMs: number;
  #disposed = false;

  constructor(options: PluginStorageHostOptions = {}) {
    this.#flushDebounceMs = options.flushDebounceMs ?? DEFAULT_FLUSH_DEBOUNCE_MS;
  }

  /** Entry from the host's frame router. Fire-and-forget: the reply is always
   * written to the mailbox (success or typed error), so a parked worker never
   * waits past its bounded `Atomics.wait`. */
  handle(plugin: PluginStorageOwner, sab: SharedArrayBuffer): void {
    let mailbox: StorageMailbox;
    try {
      mailbox = storageMailboxFor(sab);
    } catch {
      return; // Malformed frame: nothing to reply to that the client sent.
    }
    if (this.#disposed) {
      writeStorageResponse(mailbox, {
        ok: false,
        name: "StorageUnavailable",
        message: "The plugin storage host is shutting down.",
      });
      return;
    }
    const bound = this.#mailboxes.get(sab);
    if (bound !== undefined && bound !== plugin.id) {
      writeStorageResponse(mailbox, {
        ok: false,
        name: "StorageAccessDenied",
        message: "This storage mailbox is bound to another plugin.",
      });
      return;
    }
    this.#mailboxes.set(sab, plugin.id);
    if (plugin.storage === undefined) {
      writeStorageResponse(mailbox, {
        ok: false,
        name: "StorageUnavailable",
        message: "The kernel did not configure a storage directory for this plugin.",
      });
      return;
    }
    void this.#process(plugin.id, plugin.storage.dataDir, mailbox);
  }

  /** Flushes every dirty store (bounded) and stops the debounce timers. */
  async shutdown(budgetMs: number = 5_000): Promise<void> {
    const flush = this.flushAll(budgetMs);
    for (const record of this.#stores.values()) this.#clearFlushTimer(record);
    await flush;
  }

  /** Cancels pending debounced flushes without writing. In-memory state stays
   * authoritative for workers until the process dies with them. */
  dispose(): void {
    this.#disposed = true;
    for (const record of this.#stores.values()) this.#clearFlushTimer(record);
  }

  /** Flushes all dirty stores; resolves within `budgetMs` even if writes lag
   * (lagging writes are observed, not abandoned silently). */
  async flushAll(budgetMs: number = 5_000): Promise<void> {
    const pending: Promise<void>[] = [];
    for (const [pluginId, record] of this.#stores) {
      if (record.generation > record.flushedGeneration) {
        pending.push(this.#flush(pluginId, record));
      }
    }
    if (pending.length === 0) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        Promise.allSettled(pending),
        new Promise<void>((resolve) => {
          timer = setTimeout(resolve, budgetMs);
        }),
      ]);
    } finally {
      if (timer !== undefined) clearTimeout(timer);
    }
  }

  async #process(pluginId: string, dataDir: string, mailbox: StorageMailbox): Promise<void> {
    try {
      if (!this.#dataDirectories.has(pluginId)) this.#dataDirectories.set(pluginId, dataDir);
      const record = await this.#ensureLoaded(pluginId, dataDir);
      const request = readStorageRequest(mailbox);
      if (request === null) {
        throw new Error("The storage frame does not announce a request.");
      }
      writeStorageResponse(mailbox, this.#apply(record, request));
      this.#scheduleFlush(pluginId, record);
    } catch (error) {
      writeStorageResponse(mailbox, {
        ok: false,
        name: error instanceof Error && error.name === "QuotaExceededError"
          ? "QuotaExceededError"
          : "StorageError",
        message: error instanceof Error ? error.message : String(error),
      });
    }
  }

  /** Applies one op synchronously: no await between the quota check and the
   * map mutation, so interleaved frames cannot interleave a read-modify-write. */
  #apply(record: PluginStoreRecord, request: StorageRequest): StorageResponse {
    switch (request.op) {
      case "get":
        return { ok: true, value: record.map.get(request.key) ?? null };
      case "set": {
        const previous = record.map.get(request.key);
        const nextTotal = record.totalLength -
          (previous === undefined ? 0 : request.key.length + previous.length) +
          request.key.length + request.value.length;
        if (nextTotal > STORAGE_QUOTA_LENGTH) {
          const error = new DOMException(
            `The plugin storage quota (${STORAGE_QUOTA_LENGTH} code units) is exhausted.`,
            "QuotaExceededError",
          );
          return {
            ok: false,
            name: error.name,
            message: error.message,
          };
        }
        record.totalLength = nextTotal;
        record.map.set(request.key, request.value);
        record.generation += 1;
        return { ok: true };
      }
      case "remove": {
        const previous = record.map.get(request.key);
        if (previous !== undefined) {
          record.totalLength -= request.key.length + previous.length;
          record.map.delete(request.key);
          record.generation += 1;
        }
        return { ok: true };
      }
      case "clear": {
        if (record.map.size > 0) {
          record.totalLength = 0;
          record.map.clear();
          record.generation += 1;
        }
        return { ok: true };
      }
      case "length":
        return { ok: true, length: record.map.size };
      case "keyAt": {
        if (!Number.isInteger(request.index) || request.index < 0) {
          return { ok: true, key: null };
        }
        return { ok: true, key: [...record.map.keys()][request.index] ?? null };
      }
      default: {
        const unknown = request as { op?: unknown };
        throw new Error(`Unsupported storage op '${String(unknown.op)}'.`);
      }
    }
  }

  #ensureLoaded(pluginId: string, dataDir: string): Promise<PluginStoreRecord> {
    const existing = this.#stores.get(pluginId);
    if (existing !== undefined) {
      return existing.loading === undefined
        ? Promise.resolve(existing)
        : existing.loading.then(() => existing);
    }
    const record: PluginStoreRecord = {
      map: new Map(),
      totalLength: 0,
      generation: 0,
      flushedGeneration: 0,
      loaded: false,
      loading: undefined,
      flushTimer: undefined,
    };
    record.loading = this.#load(dataDir)
      .then((loaded) => {
        record.map = loaded.map;
        record.totalLength = loaded.totalLength;
        record.loaded = true;
      })
      .finally(() => {
        record.loading = undefined;
      });
    this.#stores.set(pluginId, record);
    return record.loading.then(() => record);
  }

  async #load(
    dataDir: string,
  ): Promise<{ map: Map<string, string>; totalLength: number }> {
    await Deno.mkdir(dataDir, { recursive: true });
    let text: string;
    try {
      text = await Deno.readTextFile(`${dataDir}/${STORAGE_FILE_NAME}`);
    } catch (error) {
      if (error instanceof Deno.errors.NotFound) {
        return { map: new Map(), totalLength: 0 };
      }
      // A broken document must not brick the plugin: start empty and say why.
      console.error(`[plugin-host] storage load failed (${dataDir}):`, error);
      return { map: new Map(), totalLength: 0 };
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch (error) {
      console.error(`[plugin-host] storage document is not valid JSON (${dataDir}):`, error);
      return { map: new Map(), totalLength: 0 };
    }
    const document = parsed as { version?: unknown; entries?: unknown };
    if (document.version !== STORAGE_FORMAT_VERSION || !Array.isArray(document.entries)) {
      console.error(
        `[plugin-host] storage document version mismatch (${dataDir}); starting empty.`,
      );
      return { map: new Map(), totalLength: 0 };
    }
    const map = new Map<string, string>();
    let totalLength = 0;
    for (const entry of document.entries) {
      if (!Array.isArray(entry) || entry.length !== 2) continue;
      const [key, value] = entry;
      if (typeof key !== "string" || typeof value !== "string") continue;
      map.set(key, value);
      totalLength += key.length + value.length;
    }
    return { map, totalLength };
  }

  #scheduleFlush(pluginId: string, record: PluginStoreRecord): void {
    if (record.flushTimer !== undefined) return;
    record.flushTimer = setTimeout(() => {
      record.flushTimer = undefined;
      void this.#flush(pluginId, record).catch((error: unknown) => {
        console.error(`[plugin-host] storage flush failed for '${pluginId}':`, error);
      });
    }, this.#flushDebounceMs);
  }

  #clearFlushTimer(record: PluginStoreRecord): void {
    if (record.flushTimer !== undefined) {
      clearTimeout(record.flushTimer);
      record.flushTimer = undefined;
    }
  }

  async #flush(pluginId: string, record: PluginStoreRecord): Promise<void> {
    const generation = record.generation;
    const document = {
      version: STORAGE_FORMAT_VERSION,
      entries: [...record.map.entries()],
    };
    const directory = this.#dataDirectories.get(pluginId);
    if (directory === undefined) return;
    const tempPath = `${directory}/.local-storage-${crypto.randomUUID().slice(0, 8)}.tmp`;
    const targetPath = `${directory}/${STORAGE_FILE_NAME}`;
    await Deno.writeTextFile(tempPath, JSON.stringify(document));
    await Deno.rename(tempPath, targetPath);
    // Only seal the generation that was written; mutations during the write
    // keep the store dirty and the debounce loop continues.
    if (record.generation === generation) record.flushedGeneration = generation;
  }
}
