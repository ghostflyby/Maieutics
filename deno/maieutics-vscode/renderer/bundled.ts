/**
 * Bundled-family materialization (custom-UI framework, ADR 0038 stage 1b).
 *
 * A producer family may embed its component source in the announcement
 * (`esmSource`). VS Code's notebook webview CSP allows inline/eval scripts
 * but no `blob:`/`data:` module sources and no http fetch (design doc
 * Appendix A), so the source is materialized by evaluation with an
 * injected, deliberately tiny API — `registerViewFamily` plus preact's
 * `h`/`Fragment` — not by URL import. The producer already runs arbitrary
 * code kernel-side; the injected surface adds no privileged capability.
 *
 * Results are memoized per source: a broken module degrades every view of
 * that family to the fallback state JSON (invariants 17/18) without
 * re-evaluating the source per output.
 */

import { Fragment, h } from "preact";
import { registerViewFamily, viewFamily } from "./registry.ts";

/** The bundled payload an announcement carries (subset of the SDK record). */
export interface BundledSource {
  readonly esmSource: string;
  readonly cssSource?: string;
}

export type MaterializeResult = { ok: true } | { ok: false; error: string };

const attempted = new Map<string, MaterializeResult>();

/** Evaluate a bundled family module; ok only when the named family is
 * registered afterwards. Idempotent per source text. */
export function materializeBundledFamily(family: string, source: BundledSource): MaterializeResult {
  const prior = attempted.get(source.esmSource);
  if (prior !== undefined) return prior;
  let result: MaterializeResult;
  try {
    // Producer code runs in the renderer webview sandbox (CSP applies);
    // the only injected bindings are the family registration API and
    // preact's element factories.
    const factory = new Function(
      "registerViewFamily",
      "h",
      "Fragment",
      `"use strict";\n${source.esmSource}`,
    );
    factory(registerViewFamily, h, Fragment);
    result = viewFamily(family) === undefined
      ? { ok: false, error: `the bundled module did not register family '${family}'` }
      : { ok: true };
  } catch (error) {
    result = { ok: false, error: String(error) };
  }
  attempted.set(source.esmSource, result);
  return result;
}

/** Test seam: forget memoized outcomes (unit tests only). */
export function resetBundledMaterialization(): void {
  attempted.clear();
}
