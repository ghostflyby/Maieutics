/**
 * View-family contracts for the custom-UI framework (ADR 0038).
 *
 * A view family names a component set plus the comm-plane state dialect its
 * models speak. The family contract is the single place that knows the
 * family's wire shapes: how `comm_open` carries initial state, how `comm_msg`
 * carries updates uplink and downlink, and how a model announces itself as a
 * display-mime member. The runtime (`runtime.ts`) is family-agnostic and
 * dispatches every frame through these functions.
 *
 * The `jupyter.widget` family is the compatibility precedent and stays on its
 * own runtime (`widgets/runtime.ts`) with its ipywidgets-classic dialect;
 * native families (`maieutics/form`, …) speak the dialect defined here:
 *
 *   comm_open data : { state }
 *   comm_msg down  : { method: "update", state: {key: value}, buffer_paths: [] }
 *   comm_msg up    : { method: "update", state: {…} }   (state deltas)
 *                  | { method: "event",  name, payload? } (one-shot actions)
 */

/** A broadcast function compatible with `Deno.jupyter.broadcast`'s comm surface. */
export type UiBroadcast = (
  messageType: string,
  content: Record<string, unknown>,
  extra?: {
    metadata?: Record<string, unknown>;
    buffers?: Uint8Array[];
  },
) => Promise<void>;

/** An incoming comm event delivered by the host (mirrors `maieutics.comm.on`). */
export interface UiIncomingMessage {
  kind: number;
  commId: string;
  targetName?: string;
  data?: unknown;
  buffers: Uint8Array[];
}

/** What a family decodes from an uplink `comm_msg` data payload. */
export interface UiIncomingDispatch {
  /** State deltas to apply (fires the model's onChange per key). */
  updates?: Record<string, unknown>;
  /** A one-shot action (fires the model's onEvent). */
  event?: { name: string; payload?: unknown };
}

/**
 * The wire contract of one view family. `State` is the producer-side shape
 * the family validates; the runtime stores it opaquely beyond decodeIncoming.
 */
export interface ViewFamilyContract<
  State extends Record<string, unknown> = Record<string, unknown>,
> {
  /** Catalog name, e.g. `maieutics/form`. */
  readonly family: string;
  /** comm_open target name, e.g. `maieutics.view/maieutics.form`. */
  readonly target: string;
  /** Display-mime member key, e.g. `application/vnd.maieutics.view+json`. */
  readonly displayMime: string;
  /** Family protocol version, echoed in the announcement. */
  readonly version: string;
  /** The `comm_open` data payload for a model's initial state. */
  commOpenData(state: State): Record<string, unknown>;
  /** The `comm_msg` data payload for one producer-side state key sync. */
  commUpdateData(key: string, value: unknown): Record<string, unknown>;
  /** Decode an uplink payload; null/undefined means "not for this family". */
  decodeIncoming(data: unknown): UiIncomingDispatch | undefined;
  /** The display-mime member value announcing a model. */
  announcement(commId: string, state: State): Record<string, unknown>;
}

/** Native-family target names live under this namespace (`jupyter.widget` stays verbatim). */
export function nativeTarget(family: string): string {
  return `maieutics.view/${family}`;
}

/**
 * The REPL display hook (same registry key as `widgets/index.ts`): a cell
 * result carrying this symbol displays its mime bundle. Declared here so the
 * symbol's type identity is shared by the module and its tests.
 */
export const JUPYTER_DISPLAY = Symbol.for("Jupyter.display");

/** The shared display mime every native family announces through. */
export const NATIVE_DISPLAY_MIME = "application/vnd.maieutics.view+json";

/** The native dialect shared by built-in families. */
export abstract class NativeFamilyContract implements ViewFamilyContract {
  readonly family: string;
  readonly target: string;
  readonly displayMime = NATIVE_DISPLAY_MIME;
  readonly version: string;

  constructor(family: string, version: string) {
    this.family = family;
    this.target = nativeTarget(family);
    this.version = version;
  }

  commOpenData(state: Record<string, unknown>): Record<string, unknown> {
    return { state };
  }

  commUpdateData(key: string, value: unknown): Record<string, unknown> {
    return { method: "update", state: { [key]: value }, buffer_paths: [] };
  }

  decodeIncoming(data: unknown): UiIncomingDispatch | undefined {
    if (typeof data !== "object" || data === null) return undefined;
    const record = data as Record<string, unknown>;
    if (record.method === "event" && typeof record.name === "string") {
      return { event: { name: record.name, payload: record.payload } };
    }
    if (record.method === "update") {
      const state = record.state;
      if (typeof state !== "object" || state === null || Array.isArray(state)) {
        return undefined;
      }
      return { updates: state as Record<string, unknown> };
    }
    return undefined;
  }

  announcement(commId: string, state: Record<string, unknown>): Record<string, unknown> {
    // The initial state rides the announcement so a snapshot restore (no live
    // comm) still renders the frozen view (ADR 0038 §5).
    return { modelId: commId, viewFamily: this.family, version: this.version, state };
  }
}
