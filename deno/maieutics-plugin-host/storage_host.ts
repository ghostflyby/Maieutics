/**
 * Authoritative per-plugin storage for the plugin host main isolate (ADR 0022).
 *
 * Each plugin owns one SQLite database (`local-storage.db`, node:sqlite
 * `DatabaseSync`) in the kernel-assigned data directory, and the database IS
 * the store: there is no in-memory map and no flush step. Ops are synchronous
 * indexed point queries and WAL appends (`synchronous=NORMAL`, no per-commit
 * fsync), so a parked plugin's op completes in microseconds-to-sub-millisecond
 * on the main isolate and every committed write survives host crashes.
 * `sessionStorage` never reaches this module — it is per-realm memory inside
 * the worker.
 *
 * Request routing is identity-safe by construction: `handle()` receives the
 * plugin the FRAME SENDER was resolved to (the host maps the worker to its
 * owning plugin; a client-declared id is never consulted) and binds each
 * mailbox to that plugin on first sight. A mailbox that reappears under a
 * different plugin is rejected, so a mailbox handed across plugins through an
 * actor port cannot borrow another plugin's store.
 *
 * Schema (one database per plugin, schema version in `PRAGMA user_version`):
 *   kv(key TEXT PRIMARY KEY, value TEXT NOT NULL, ordinal INTEGER NOT NULL)
 *   WITHOUT ROWID, plus an index on (ordinal) for `keyAt`. `ordinal` is a
 *   monotonically increasing counter assigned only on INSERT and never reused:
 *   `ORDER BY rowid` would break after a deletion (SQLite reuses freed rowids),
 *   so insertion-order iteration must not depend on it.
 */

import { DatabaseSync, type StatementSync } from "node:sqlite";
import {
  readStorageRequest,
  STORAGE_QUOTA_LENGTH,
  type StorageMailbox,
  storageMailboxFor,
  type StorageRequest,
  type StorageResponse,
  writeStorageResponse,
} from "../maieutics-runtime/storage_channel.ts";

/** File name inside the per-plugin data directory. */
export const STORAGE_FILE_NAME = "local-storage.db";

/** Schema version recorded in `PRAGMA user_version`; bumped on breaking
 * schema changes (the pre-release JSON store is gone without a migration). */
export const STORAGE_SCHEMA_VERSION = 1;

interface PluginDatabase {
  db: DatabaseSync;
  /** Next insertion ordinal; never reused while the connection is open. */
  nextOrdinal: number;
  /** Sum of key.length + value.length (UTF-16 units) over all rows. */
  totalLength: number;
  statements: {
    get: StatementSync;
    upsert: StatementSync;
    remove: StatementSync;
    clear: StatementSync;
    length: StatementSync;
    keyAt: StatementSync;
  };
}

/** The slice of PluginConfig the responder needs per frame. */
export interface PluginStorageOwner {
  id: string;
  storage?: { readonly dataDir: string };
}

export class PluginStorageHost {
  readonly #databases = new Map<string, PluginDatabase>();
  /** Mailbox → owning plugin id, bound on first sight and validated after. */
  readonly #mailboxes = new Map<SharedArrayBuffer, string>();
  #disposed = false;

  /** Entry from the host's frame router. Synchronous: the reply is always
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
    this.#process(plugin.id, plugin.storage.dataDir, mailbox);
  }

  /** Closes every open database (each close checkpoints its WAL) and stops
   * serving. Called on the host's process exit path and from dispose. */
  shutdown(): void {
    this.#disposed = true;
    for (const [pluginId, record] of this.#databases) {
      try {
        record.db.close();
      } catch (error) {
        console.error(`[plugin-host] storage close failed for '${pluginId}':`, error);
      }
    }
    this.#databases.clear();
  }

  /** Alias kept for the host's dispose path; with a database-backed store
   * there is no buffered state, so both mean the same thing. */
  dispose(): void {
    this.shutdown();
  }

  #process(pluginId: string, dataDir: string, mailbox: StorageMailbox): void {
    try {
      const record = this.#databases.get(pluginId) ?? this.#open(pluginId, dataDir);
      const request = readStorageRequest(mailbox);
      if (request === null) {
        throw new Error("The storage frame does not announce a request.");
      }
      writeStorageResponse(mailbox, this.#apply(record, request));
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

  #open(pluginId: string, dataDir: string): PluginDatabase {
    Deno.mkdir(dataDir, { recursive: true });
    const db = new DatabaseSync(`${dataDir}/${STORAGE_FILE_NAME}`);
    try {
      db.exec("PRAGMA journal_mode = WAL");
      db.exec("PRAGMA synchronous = NORMAL");
      const version = db.prepare("PRAGMA user_version").get() as { user_version: number };
      if (version.user_version > STORAGE_SCHEMA_VERSION) {
        throw new Error(
          `The plugin storage schema version ${version.user_version} is newer than this host ` +
            `supports (${STORAGE_SCHEMA_VERSION}).`,
        );
      }
      if (version.user_version === 0) {
        db.exec(
          "CREATE TABLE IF NOT EXISTS kv (" +
            "key TEXT PRIMARY KEY, value TEXT NOT NULL, ordinal INTEGER NOT NULL) WITHOUT ROWID",
        );
        db.exec("CREATE INDEX IF NOT EXISTS kv_order ON kv (ordinal)");
        db.exec(`PRAGMA user_version = ${STORAGE_SCHEMA_VERSION}`);
      }
      // Hydrate the two in-memory aggregates with one scan: quota accounting
      // uses UTF-16 lengths (JS semantics), which SQL length() cannot reproduce
      // for astral characters, and the ordinal counter must resume past the
      // highest stored value.
      let nextOrdinal = 0;
      let totalLength = 0;
      const rows = db.prepare("SELECT key, value, ordinal FROM kv").all() as {
        key: string;
        value: string;
        ordinal: number;
      }[];
      for (const row of rows) {
        totalLength += row.key.length + row.value.length;
        if (row.ordinal >= nextOrdinal) nextOrdinal = row.ordinal + 1;
      }
      const record: PluginDatabase = {
        db,
        nextOrdinal,
        totalLength,
        statements: {
          get: db.prepare("SELECT value FROM kv WHERE key = ?"),
          upsert: db.prepare(
            "INSERT INTO kv (key, value, ordinal) VALUES (?, ?, ?) " +
              "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
          ),
          remove: db.prepare("DELETE FROM kv WHERE key = ?"),
          clear: db.prepare("DELETE FROM kv"),
          length: db.prepare("SELECT COUNT(*) AS n FROM kv"),
          keyAt: db.prepare("SELECT key FROM kv ORDER BY ordinal LIMIT 1 OFFSET ?"),
        },
      };
      this.#databases.set(pluginId, record);
      return record;
    } catch (error) {
      try {
        db.close();
      } catch {
        // The open itself may have failed.
      }
      throw error;
    }
  }

  /** Applies one op with synchronous statements: no await between the quota
   * check and the write, so interleaved frames cannot interleave a
   * read-modify-write. */
  #apply(record: PluginDatabase, request: StorageRequest): StorageResponse {
    switch (request.op) {
      case "get": {
        const row = record.statements.get.get(request.key) as { value: string } | undefined;
        return { ok: true, value: row?.value ?? null };
      }
      case "set": {
        const previous = record.statements.get.get(request.key) as { value: string } | undefined;
        const nextTotal = record.totalLength -
          (previous === undefined ? 0 : request.key.length + previous.value.length) +
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
        // ON CONFLICT updates the value only, so an overwrite keeps its
        // insertion position; the ordinal binds to the new row otherwise.
        record.statements.upsert.run(request.key, request.value, record.nextOrdinal);
        if (previous === undefined) record.nextOrdinal += 1;
        record.totalLength = nextTotal;
        return { ok: true };
      }
      case "remove": {
        const previous = record.statements.get.get(request.key) as { value: string } | undefined;
        if (previous !== undefined) {
          record.statements.remove.run(request.key);
          record.totalLength -= request.key.length + previous.value.length;
        }
        return { ok: true };
      }
      case "clear": {
        record.statements.clear.run();
        record.totalLength = 0;
        record.nextOrdinal = 0;
        return { ok: true };
      }
      case "length": {
        const row = record.statements.length.get() as { n: number };
        return { ok: true, length: row.n };
      }
      case "keyAt": {
        if (!Number.isInteger(request.index) || request.index < 0) {
          return { ok: true, key: null };
        }
        const row = record.statements.keyAt.get(request.index) as { key: string } | undefined;
        return { ok: true, key: row?.key ?? null };
      }
      default: {
        const unknown = request as { op?: unknown };
        throw new Error(`Unsupported storage op '${String(unknown.op)}'.`);
      }
    }
  }
}
