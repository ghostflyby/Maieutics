/**
 * Synchronous plugin storage channel (ADR 0022): the wire protocol and the
 * worker-side client that back `localStorage` and `sessionStorage` inside
 * plugin workers.
 *
 * Browser-style storage is synchronous, but the authoritative per-plugin store
 * lives in the plugin host's main isolate — the only writer that persists, and
 * the single consistency point shared by every worker of one plugin (the
 * origin). The channel follows the admission handshake pattern
 * (`maieutics-plugin-sdk/admission.ts`): the request direction is a bare
 * `postMessage` frame (the receiving side's event loop is free), the reply
 * direction is a per-realm SharedArrayBuffer mailbox, and the requesting
 * thread parks in a bounded `Atomics.wait`. No Deno permission is involved:
 * the mailbox is created inside the requesting realm and shared by reference.
 *
 * Request routing never trusts a client-declared identity. Frames carry only
 * the mailbox; the host maps the sending worker to its owning plugin, binds
 * the mailbox to that plugin on first sight, and rejects mailboxes that
 * reappear under a different plugin (a mailbox handed across plugins through
 * an actor port cannot borrow another plugin's store).
 *
 * Mailbox layout (one SharedArrayBuffer per realm, reused for every op):
 *   bytes 0-3    state: 0=idle, 1=request ready, 2=response ready
 *   bytes 4-7    request length (bytes, UTF-8 JSON in the payload region)
 *   bytes 8-11   response length (bytes, UTF-8 JSON in the payload region)
 *   bytes 12+    payload region (request/response JSON, never both live)
 *
 * A realm has at most one outstanding synchronous op (its isolate is parked),
 * so one slot suffices. A timed-out op poisons the mailbox: the host may still
 * write the late reply into the payload region, so reusing it could corrupt
 * the next request; every later op fails loudly instead.
 */

import { onControlledWorkerCreated } from "./worker_patch.ts";

/**
 * postMessage frame type that carries a storage op from a plugin realm toward
 * the host. Relay listeners forward it upward unchanged; replies never travel
 * as frames — the host writes them into the mailbox directly.
 */
export const STORAGE_FRAME_TYPE = "maieutics-storage";

export const STORAGE_STATE_IDLE = 0;
export const STORAGE_STATE_REQUEST = 1;
export const STORAGE_STATE_RESPONSE = 2;

/** Wire payload region size. Also the effective per-value transfer limit: a
 * request (op envelope + key + value, UTF-8 JSON) must fit it. */
export const STORAGE_PAYLOAD_BYTES = 1_048_576;

export const STORAGE_HEADER_BYTES = 12;
export const STORAGE_MAILBOX_BYTES = STORAGE_HEADER_BYTES + STORAGE_PAYLOAD_BYTES;

/** Bounded park for one op; a timeout fails loudly instead of wedging the
 * plugin worker forever (the host's own failure model tears the process down). */
export const STORAGE_TIMEOUT_MS = 10_000;

/** Per-store quota in UTF-16 code units (keys + values), matching the browser
 * convention of a few mebibytes per origin. Enforced by the host for
 * localStorage and by the client for sessionStorage. */
export const STORAGE_QUOTA_LENGTH = 5_242_880;

/** The mailbox a client allocates for its realm and posts with every op. */
export interface StorageMailbox {
  readonly sab: SharedArrayBuffer;
  readonly state: Int32Array; // byte 0
  readonly requestLength: Int32Array; // byte 4
  readonly responseLength: Int32Array; // byte 8
  readonly payload: Uint8Array; // byte 12+
}

export type StorageRequest =
  | { op: "get"; key: string }
  | { op: "set"; key: string; value: string }
  | { op: "remove"; key: string }
  | { op: "clear" }
  | { op: "length" }
  | { op: "keyAt"; index: number };

export type StorageResponse =
  | { ok: true; value?: string | null; length?: number; key?: string | null }
  | { ok: false; name: string; message: string };

/** A successful response; error responses surface as thrown DOMExceptions. */
export type StorageOkResponse = Extract<StorageResponse, { ok: true }>;

/** The frame posted upward for one op. It carries no identity: the host maps
 * the sending worker to its owning plugin and binds the mailbox on first sight. */
export interface StorageRequestFrame {
  type: typeof STORAGE_FRAME_TYPE;
  sab: SharedArrayBuffer;
}

const encoder = new TextEncoder();
const decoder = new TextDecoder();

export function createStorageMailbox(): StorageMailbox {
  const sab = new SharedArrayBuffer(STORAGE_MAILBOX_BYTES);
  return storageMailboxFor(sab);
}

/** Wraps a raw SharedArrayBuffer as a mailbox. The host validates the byte
 * length before use (a short buffer is a protocol violation, not a crash). */
export function storageMailboxFor(sab: SharedArrayBuffer): StorageMailbox {
  if (sab.byteLength < STORAGE_MAILBOX_BYTES) {
    throw new Error(
      `Storage mailboxes must be at least ${STORAGE_MAILBOX_BYTES} bytes.`,
    );
  }
  return {
    sab,
    state: new Int32Array(sab, 0, 1),
    requestLength: new Int32Array(sab, 4, 1),
    responseLength: new Int32Array(sab, 8, 1),
    payload: new Uint8Array(sab, STORAGE_HEADER_BYTES),
  };
}

/** The client-side op: encode, announce, hand the frame upward, park. Throws
 * `StorageTimeoutError` when nothing answers in time; the mailbox is poisoned
 * afterwards. Throws a `QuotaExceededError` DOMException before posting when
 * the encoded request cannot fit the payload region. */
export function performStorageOp(
  mailbox: StorageMailbox,
  request: StorageRequest,
  post: (frame: StorageRequestFrame) => void,
  timeoutMs: number = STORAGE_TIMEOUT_MS,
): StorageOkResponse {
  const encoded = encoder.encode(JSON.stringify(request));
  if (encoded.length > STORAGE_PAYLOAD_BYTES) {
    throw new DOMException(
      "The storage value exceeds the per-value transfer limit.",
      "QuotaExceededError",
    );
  }
  mailbox.payload.set(encoded);
  Atomics.store(mailbox.requestLength, 0, encoded.length);
  Atomics.store(mailbox.state, 0, STORAGE_STATE_REQUEST);
  post({ type: STORAGE_FRAME_TYPE, sab: mailbox.sab });
  const wake = Atomics.wait(mailbox.state, 0, STORAGE_STATE_REQUEST, timeoutMs);
  if (wake === "timed-out") {
    throw new StorageTimeoutError(
      `The storage responder did not answer within ${timeoutMs} ms.`,
    );
  }
  const responseLength = Atomics.load(mailbox.responseLength, 0);
  const raw = decoder.decode(mailbox.payload.subarray(0, responseLength));
  Atomics.store(mailbox.state, 0, STORAGE_STATE_IDLE);
  const response = JSON.parse(raw) as StorageResponse;
  if (!response.ok) {
    throw new DOMException(response.message, response.name);
  }
  return response;
}

/** A timed-out op poisons the mailbox for its realm (see module contract). */
export class StorageTimeoutError extends Error {
  override name = "StorageTimeout";
}

/** Host-side reader for an announced request. Returns null while the mailbox
 * is not announcing a request (a spurious or duplicate frame). */
export function readStorageRequest(mailbox: StorageMailbox): StorageRequest | null {
  if (Atomics.load(mailbox.state, 0) !== STORAGE_STATE_REQUEST) return null;
  const length = Atomics.load(mailbox.requestLength, 0);
  if (length < 0 || length > STORAGE_PAYLOAD_BYTES) return null;
  return JSON.parse(
    decoder.decode(mailbox.payload.subarray(0, length)),
  ) as StorageRequest;
}

/** Host-side writer: encode the reply, announce it, wake the parked realm. A
 * reply that cannot fit the payload region (only reachable for a `get` of a
 * value that never passed the client's own fit check) is replaced by a typed
 * error instead of corrupting the mailbox. */
export function writeStorageResponse(
  mailbox: StorageMailbox,
  response: StorageResponse,
): void {
  let encoded = encoder.encode(JSON.stringify(response));
  if (encoded.length > STORAGE_PAYLOAD_BYTES) {
    encoded = encoder.encode(JSON.stringify(
      {
        ok: false,
        name: "QuotaExceededError",
        message: "The storage value exceeds the per-value transfer limit.",
      } satisfies StorageResponse,
    ));
  }
  mailbox.payload.set(encoded);
  Atomics.store(mailbox.responseLength, 0, encoded.length);
  Atomics.store(mailbox.state, 0, STORAGE_STATE_RESPONSE);
  Atomics.notify(mailbox.state, 0, 1);
}

/** The Web Storage surface the client installs. Named-property access
 * (`localStorage.token = "x"`, `delete localStorage.token`,
 * `"token" in localStorage`) works; enumeration of data keys
 * (`Object.keys(localStorage)`) intentionally does not — the RPC surface has
 * no bulk keys op, and spec-wise iteration is rarely load-bearing. */
export interface WebStorageLike {
  readonly length: number;
  key(index: number): string | null;
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
  clear(): void;
}

const API_PROPERTY_NAMES = new Set([
  "length",
  "key",
  "getItem",
  "setItem",
  "removeItem",
  "clear",
]);

/** Wraps the plain method target with the named-property Proxy surface shared
 * by the RPC and in-memory implementations. */
function withNamedProperties(target: WebStorageLike): WebStorageLike {
  return new Proxy(target, {
    get(target, property, receiver) {
      if (
        typeof property === "string" && !API_PROPERTY_NAMES.has(property) &&
        !(property in target)
      ) {
        return target.getItem(property);
      }
      return Reflect.get(target, property, receiver);
    },
    set(target, property, value) {
      if (typeof property === "string" && !API_PROPERTY_NAMES.has(property)) {
        target.setItem(property, String(value));
        return true;
      }
      return Reflect.set(target, property, value);
    },
    has(target, property) {
      if (
        typeof property === "string" && !API_PROPERTY_NAMES.has(property) &&
        target.getItem(property) !== null
      ) {
        return true;
      }
      return Reflect.has(target, property);
    },
    deleteProperty(target, property) {
      if (typeof property === "string" && !API_PROPERTY_NAMES.has(property)) {
        target.removeItem(property);
        return true;
      }
      return Reflect.deleteProperty(target, property);
    },
  });
}

/** In-memory store behind `sessionStorage`: per realm, quota-bounded, no IPC. */
export function createMemoryStorage(quota: number = STORAGE_QUOTA_LENGTH): WebStorageLike {
  const entries = new Map<string, string>();
  let totalLength = 0;
  const target: WebStorageLike = {
    get length(): number {
      return entries.size;
    },
    key(index: number): string | null {
      const position = Number(index);
      if (!Number.isInteger(position) || position < 0) return null;
      return [...entries.keys()][position] ?? null;
    },
    getItem(key: string): string | null {
      return entries.get(String(key)) ?? null;
    },
    setItem(key: string, value: string): void {
      const storageKey = String(key);
      const storageValue = String(value);
      const previous = entries.get(storageKey);
      const nextTotal = totalLength -
        (previous === undefined ? 0 : storageKey.length + previous.length) +
        storageKey.length + storageValue.length;
      if (nextTotal > quota) {
        throw new DOMException(
          `The session storage quota (${quota} code units) is exhausted.`,
          "QuotaExceededError",
        );
      }
      totalLength = nextTotal;
      entries.set(storageKey, storageValue);
    },
    removeItem(key: string): void {
      const storageKey = String(key);
      const previous = entries.get(storageKey);
      if (previous === undefined) return;
      totalLength -= storageKey.length + previous.length;
      entries.delete(storageKey);
    },
    clear(): void {
      entries.clear();
      totalLength = 0;
    },
  };
  return withNamedProperties(target);
}

/** RPC-backed store behind `localStorage`: every op round-trips the host's
 * authoritative per-plugin store through the mailbox. */
export function createRpcStorage(
  mailbox: StorageMailbox,
  post: (frame: StorageRequestFrame) => void,
  timeoutMs: number = STORAGE_TIMEOUT_MS,
): WebStorageLike {
  let poisoned = false;
  const call = (request: StorageRequest): StorageOkResponse => {
    if (poisoned) {
      throw new StorageTimeoutError(
        "The storage mailbox is unusable after a timed-out request.",
      );
    }
    try {
      return performStorageOp(mailbox, request, post, timeoutMs);
    } catch (error) {
      if (error instanceof StorageTimeoutError) poisoned = true;
      throw error;
    }
  };
  const target: WebStorageLike = {
    get length(): number {
      const response = call({ op: "length" });
      return response.length ?? 0;
    },
    key(index: number): string | null {
      const position = Number(index);
      if (!Number.isFinite(position)) return null;
      const response = call({ op: "keyAt", index: Math.trunc(position) });
      return response.key ?? null;
    },
    getItem(key: string): string | null {
      return call({ op: "get", key: String(key) }).value ?? null;
    },
    setItem(key: string, value: string): void {
      call({ op: "set", key: String(key), value: String(value) });
    },
    removeItem(key: string): void {
      call({ op: "remove", key: String(key) });
    },
    clear(): void {
      call({ op: "clear" });
    },
  };
  return withNamedProperties(target);
}

let installed = false;

/**
 * Installs the plugin storage globals on the current realm: `localStorage`
 * backed by the RPC channel plus the child-frame relay. Idempotent per realm.
 * Called by the plugin worker entry (root realms) and by the shared wrapper
 * for nested plugin realms.
 *
 * Request routing: a realm's own ops are posted to its parent (the host for a
 * root plugin worker, the creating plugin worker for a nested realm). A
 * child's frames land on the CHILD WORKER OBJECT in this realm, never on the
 * parent's global scope, so the relay is attached to each created worker
 * through the shared patch's creation hook (`onControlledWorkerCreated`) and
 * re-posts their storage frames upward. Replies never travel as frames — the
 * host writes them into the mailbox directly.
 */
export function installPluginStorage(): void {
  if (installed) return;
  installed = true;
  const scope = self as unknown as { postMessage(message: unknown): void };
  const relayFrame = (event: MessageEvent): void => {
    const frame = event.data as { type?: unknown } | null;
    if (
      frame !== null && typeof frame === "object" &&
      frame.type === STORAGE_FRAME_TYPE
    ) {
      scope.postMessage(frame);
    }
  };
  onControlledWorkerCreated((worker) => {
    worker.addEventListener("message", relayFrame);
  });
  const mailbox = createStorageMailbox();
  defineStorageGlobal(
    "localStorage",
    createRpcStorage(mailbox, (frame) => scope.postMessage(frame)),
  );
  defineStorageGlobal("sessionStorage", createMemoryStorage());
}

function defineStorageGlobal(name: "localStorage" | "sessionStorage", value: WebStorageLike): void {
  try {
    Object.defineProperty(globalThis, name, {
      value,
      enumerable: true,
      configurable: true,
      writable: false,
    });
  } catch {
    // The native binding is non-configurable on this Deno build; the plugin
    // keeps the native behavior (an access error) rather than crashing the
    // bootstrap. Tests pin the configurable behavior per supported Deno.
  }
}
