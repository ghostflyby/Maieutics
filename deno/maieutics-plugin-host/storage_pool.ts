/**
 * Storage execution pool (ADR 0022): a bounded, lazily-grown set of dedicated
 * workers that own the per-plugin SQLite databases, plus the host-side router
 * that feeds them.
 *
 * The host main isolate is a pure router: it resolves the sending worker to
 * its owning plugin, validates the mailbox binding, and forwards the frame to
 * the plugin's bound pool worker. The pool worker executes the synchronous
 * SQLite op and writes the reply into the mailbox directly — the main isolate
 * never blocks on storage.
 *
 * Ownership and concurrency contract:
 *   - Each plugin database is opened by exactly ONE pool worker at a time
 *     (sticky first-op assignment). Single-connection ownership keeps the
 *     ordinal counter and quota accounting correct without database-side
 *     transaction protocols; the pool must never route one plugin's frames to
 *     two workers. Rebinding happens on this single-threaded router before
 *     any frame is forwarded.
 *   - Pool workers acknowledge a forwarded frame ON RECEIPT (before
 *     executing). The pending registry therefore only covers the
 *     route→ack window; a worker crashing later leaves the realm parked on
 *     its own bounded `Atomics.wait` timeout (typed `StorageTimeout`), the
 *     same failure shape as the responder dying entirely. Because a realm
 *     has at most one outstanding op, an unacked pending's realm is by
 *     definition still parked on it — a failure reply can never race that
 *     realm's next request, so the mailbox protocol needs no sequence
 *     numbers.
 *   - On worker `error` (crash) every unacked pending of that worker is
 *     failed with a typed error reply and its plugins are unbound; the next
 *     op for such a plugin reopens the database on another worker (WAL
 *     recovery replays committed writes).
 *
 * Pool workers are trusted internal Deno children (ADR 0018 §8): they receive
 * read+write on the plugin-data root plus read on the materialized module
 * directories — narrower than the host main isolate's full grants, and
 * unrelated to plugin worker grants (plugin workers stay zero-permission for
 * storage).
 */

import {
  type StorageMailbox,
  storageMailboxFor,
  writeStorageResponse,
} from "../maieutics-runtime/storage_channel.ts";

/** Frame the router forwards to a pool worker (the plugin realm's frame,
 * annotated with the router-resolved identity — a client-declared id is never
 * consulted). */
export const STORAGE_OP_FRAME_TYPE = "maieutics-storage-op";

/** Frame a pool worker sends back on receipt (before executing). */
export const STORAGE_ACK_FRAME_TYPE = "maieutics-storage-ack";

/** Frame the router sends on shutdown; the worker closes its databases. */
export const STORAGE_CLOSE_FRAME_TYPE = "maieutics-storage-close";

/** Frame a pool worker sends after closing its databases. */
export const STORAGE_CLOSED_FRAME_TYPE = "maieutics-storage-closed";

/** The slice of PluginConfig the router needs per frame. */
export interface PluginStorageOwner {
  id: string;
  storage?: { readonly dataDir: string };
}

export interface PluginStoragePoolOptions {
  /** Upper bound on pool workers; further plugins attach to owned workers. */
  maxWorkers?: number;
  /** Read grants for pool workers: the plugin-data root plus the materialized
   * module directories (the engine and the channel module). */
  readDirs: readonly string[];
  /** Write grant for pool workers: the plugin-data root. */
  writeDirs: readonly string[];
  /** Worker construction seam (tests inject a recording factory). */
  spawnWorker?: (url: URL, options: WorkerOptions) => Worker;
}

interface PoolMember {
  worker: Worker;
  /** Plugin ids whose database this worker owns. */
  plugins: Set<string>;
  /** Frames forwarded but not yet acked, by mailbox. */
  pending: Map<SharedArrayBuffer, { pluginId: string; dataDir: string }>;
  terminated: boolean;
}

const DEFAULT_MAX_WORKERS = 4;

export class PluginStoragePool {
  readonly #members: PoolMember[] = [];
  /** pluginId → owning member (sticky for the plugin's lifetime). */
  readonly #bindings = new Map<string, PoolMember>();
  /** Mailbox → owning plugin id, bound on first sight and validated after. */
  readonly #mailboxes = new Map<SharedArrayBuffer, string>();
  readonly #maxWorkers: number;
  readonly #readDirs: readonly string[];
  readonly #writeDirs: readonly string[];
  readonly #spawnWorker: (url: URL, options: WorkerOptions) => Worker;
  #disposed = false;

  constructor(options: PluginStoragePoolOptions) {
    this.#maxWorkers = Math.max(1, options.maxWorkers ?? DEFAULT_MAX_WORKERS);
    this.#readDirs = options.readDirs;
    this.#writeDirs = options.writeDirs;
    this.#spawnWorker = options.spawnWorker ??
      ((url, workerOptions) => new Worker(url, workerOptions));
  }

  /** Entry from the host's frame router. Synchronous and never throws: the
   * reply — success (via the pool worker) or a typed error (via this router)
   * — always reaches the mailbox, so a parked realm never waits past its
   * bounded `Atomics.wait`. */
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
    if (plugin.storage === undefined || plugin.storage.dataDir.length === 0) {
      writeStorageResponse(mailbox, {
        ok: false,
        name: "StorageUnavailable",
        message: "The kernel did not configure a storage directory for this plugin.",
      });
      return;
    }
    const member = this.#bindings.get(plugin.id) ?? this.#assign(plugin.id);
    if (member === undefined) {
      // Spawn failed; the failure is already logged. Reply inline.
      writeStorageResponse(mailbox, {
        ok: false,
        name: "StorageError",
        message: "The storage pool could not spawn a worker.",
      });
      return;
    }
    member.pending.set(sab, { pluginId: plugin.id, dataDir: plugin.storage.dataDir });
    try {
      member.worker.postMessage({
        type: STORAGE_OP_FRAME_TYPE,
        sab,
        pluginId: plugin.id,
        dataDir: plugin.storage.dataDir,
      });
    } catch (error) {
      // A terminated member drops messages silently on some runtimes; fail
      // the pending instead of letting the realm wait out its timeout.
      member.pending.delete(sab);
      this.#failPending(plugin.id, mailbox, error);
    }
  }

  /** Graceful shutdown: ask every worker to close its databases, wait bounded
   * for the acks, then terminate whatever is left. */
  async shutdown(timeoutMs: number = 2_000): Promise<void> {
    this.#disposed = true;
    const live = this.#members.filter((member) => !member.terminated);
    if (live.length === 0) return;
    await Promise.race([
      Promise.allSettled(live.map((member) => this.#closeMember(member))),
      new Promise((resolve) => setTimeout(resolve, timeoutMs)),
    ]);
    for (const member of live) this.#terminate(member);
  }

  /** Immediate teardown: terminate workers and fail every pending. Data
   * safety relies on WAL (a terminated worker is equivalent to a crash). */
  dispose(): void {
    this.#disposed = true;
    for (const member of this.#members) this.#terminate(member);
  }

  /** The worker currently owning a plugin's database (diagnostics/tests). */
  workerOf(pluginId: string): Worker | undefined {
    return this.#bindings.get(pluginId)?.worker;
  }

  #assign(pluginId: string): PoolMember | undefined {
    const live = this.#members.filter((member) => !member.terminated);
    // Grow while under the cap; otherwise (or if the spawn unexpectedly
    // fails) attach to the least-loaded live member. One worker may own
    // several databases — still exactly one connection per database.
    const member = live.length < this.#maxWorkers
      ? (this.#spawn() ?? this.#leastLoaded())
      : this.#leastLoaded();
    if (member === undefined) return undefined;
    member.plugins.add(pluginId);
    this.#bindings.set(pluginId, member);
    return member;
  }

  #leastLoaded(): PoolMember | undefined {
    let best: PoolMember | undefined;
    for (const member of this.#members) {
      if (member.terminated) continue;
      if (best === undefined || member.plugins.size < best.plugins.size) best = member;
    }
    return best;
  }

  #spawn(): PoolMember | undefined {
    if (this.#members.length >= this.#maxWorkers) return undefined;
    const url = new URL("./storage_pool_worker.ts", import.meta.url);
    let worker: Worker;
    try {
      worker = this.#spawnWorker(url, {
        type: "module",
        deno: { permissions: { read: [...this.#readDirs], write: [...this.#writeDirs] } },
      });
    } catch (error) {
      console.error("[plugin-host] storage pool worker spawn failed:", error);
      return undefined;
    }
    const member: PoolMember = {
      worker,
      plugins: new Set(),
      pending: new Map(),
      terminated: false,
    };
    worker.onmessage = (event: MessageEvent) => this.#onMemberMessage(member, event);
    worker.onerror = (event) => this.#onMemberError(member, event);
    this.#members.push(member);
    return member;
  }

  #onMemberMessage(member: PoolMember, event: MessageEvent): void {
    const frame = event.data as { type?: string; sab?: unknown };
    if (frame?.type === STORAGE_ACK_FRAME_TYPE && frame.sab instanceof SharedArrayBuffer) {
      member.pending.delete(frame.sab);
    }
  }

  #onMemberError(member: PoolMember, event: ErrorEvent): void {
    console.error(
      `[plugin-host] storage pool worker crashed: ${event.message}; failing ` +
        `${member.pending.size} pending op(s) and rebinding ${member.plugins.size} plugin(s).`,
    );
    event.preventDefault?.();
    this.#failMember(member, new Error("The storage pool worker crashed."));
  }

  #failMember(member: PoolMember, error: Error): void {
    member.terminated = true;
    try {
      member.worker.terminate();
    } catch {
      // Already gone.
    }
    for (const [sab, pending] of member.pending) {
      this.#mailboxes.delete(sab);
      let mailbox: StorageMailbox | undefined;
      try {
        mailbox = storageMailboxFor(sab);
      } catch {
        continue;
      }
      writeStorageResponse(mailbox, {
        ok: false,
        name: "StorageError",
        message: `${error.message} (plugin '${pending.pluginId}')`,
      });
    }
    member.pending.clear();
    for (const pluginId of member.plugins) {
      const bound = this.#bindings.get(pluginId);
      if (bound === member) this.#bindings.delete(pluginId);
    }
    member.plugins.clear();
  }

  #failPending(pluginId: string, mailbox: StorageMailbox, error: unknown): void {
    writeStorageResponse(mailbox, {
      ok: false,
      name: "StorageError",
      message: `The storage op for plugin '${pluginId}' could not be forwarded: ` +
        `${error instanceof Error ? error.message : String(error)}`,
    });
  }

  async #closeMember(member: PoolMember): Promise<void> {
    await new Promise<void>((resolve) => {
      const onClosed = (event: MessageEvent): void => {
        if ((event.data as { type?: string } | null)?.type === STORAGE_CLOSED_FRAME_TYPE) {
          clearTimeout(timeout);
          member.worker.removeEventListener("message", onClosed);
          resolve();
        }
      };
      const timeout = setTimeout(() => {
        member.worker.removeEventListener("message", onClosed);
        resolve();
      }, 1_000);
      member.worker.addEventListener("message", onClosed);
      member.worker.postMessage({ type: STORAGE_CLOSE_FRAME_TYPE });
    });
  }

  #terminate(member: PoolMember): void {
    if (member.terminated) return;
    member.terminated = true;
    try {
      member.worker.terminate();
    } catch {
      // Already gone.
    }
    // Terminating is crash-equivalent for SQLite (WAL recovery on reopen);
    // any still-pending frames belong to realms that are being torn down
    // with the host.
    member.pending.clear();
  }
}
