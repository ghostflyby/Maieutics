/**
 * Notebook-renderer view-family registry (custom-UI framework, stage 0).
 *
 * A view family names a component set plus the state dialect its models
 * speak on the comm plane. Families register their Preact component here;
 * the renderer script (script.ts) dispatches each output to its family's
 * component and falls back to live state JSON for unknown families — the
 * degradation today's hand-written widget renderer already has.
 *
 * Pure module: no DOM, no vscode imports. Components are plain functions
 * of (model state, shared state store, post sink), so tests call them
 * directly and assert vnode trees.
 */

import { type FunctionalComponent, h } from "preact";

/** Props every view-family component receives. */
export interface ViewProps {
  readonly modelId: string;
  /** Latest known state of this model; `{}` when none has arrived. */
  readonly state: Record<string, unknown>;
  /** Per-renderer shared state store (modelId -> latest state), so nested
   * model references (e.g. IPY_MODEL_) resolve across outputs. */
  readonly models: ReadonlyMap<string, Record<string, unknown>>;
  /** Post a message to the extension host; the family builds the envelope. */
  post(message: unknown): void;
  /** False until the bridge delivered a live state snapshot for the model. */
  readonly hasState: boolean;
}

export interface ViewFamilyModule {
  readonly component: FunctionalComponent<ViewProps>;
  /** Model ids whose state changes must re-render a view of this family
   * (defaults to the model itself; layout families add nested children). */
  collectDeps?(
    modelId: string,
    models: ReadonlyMap<string, Record<string, unknown>>,
  ): Set<string>;
}

/** The built-in fallback family: unknown families render as live state JSON. */
export const FALLBACK_FAMILY = "maieutics/state";

const families = new Map<string, ViewFamilyModule>();

export function registerViewFamily(family: string, module: ViewFamilyModule): void {
  families.set(family, module);
}

export function viewFamily(family: string): ViewFamilyModule | undefined {
  return families.get(family);
}

const StateView: FunctionalComponent<ViewProps> = ({ state }) =>
  h("div", { class: "state" }, JSON.stringify(state, null, 2));

registerViewFamily(FALLBACK_FAMILY, { component: StateView });
