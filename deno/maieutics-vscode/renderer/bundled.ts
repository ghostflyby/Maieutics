/**
 * Bundled-family materialization (custom-UI framework, ADR 0038 stage 1b).
 *
 * A producer family may embed its component source in the announcement
 * (`esmSource`). VS Code's notebook webview CSP (when the experimental
 * notebook CSP is on) allows inline/eval scripts but no `blob:`/`data:`
 * module sources and no http fetch (design doc Appendix A), so the source
 * is materialized by evaluation with an injected, deliberately tiny API —
 * `registerViewFamily` plus preact's `h`/`Fragment` — not by URL import.
 *
 * Trust boundary: the announcement reaches the renderer only as live output
 * of a running session (the serializer strips bundled sources when
 * persisting snapshots, so an untrusted `.maieuticsnb` never carries
 * executable code here). The producer already runs arbitrary code
 * kernel-side; the injected surface adds no privileged capability, and the
 * guarded registration validates module shape and refuses to override
 * already-registered (built-in) families.
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

/** Mirrors the SDK-side ceiling (MAX_BUNDLED_SOURCE_BYTES): producers cannot
 * exceed it, and this guards the renderer against non-SDK producers. */
const MAX_SOURCE_LENGTH = 1024 * 1024;

const attempted = new Map<string, MaterializeResult>();

/** Evaluate a bundled family module; ok only when the named family is
 * registered (with a valid module shape) afterwards. Idempotent per source. */
export function materializeBundledFamily(family: string, source: BundledSource): MaterializeResult {
  const prior = attempted.get(source.esmSource);
  if (prior !== undefined) return prior;
  let result: MaterializeResult;
  if (source.esmSource.length > MAX_SOURCE_LENGTH) {
    result = {
      ok: false,
      error:
        `bundled source is ${source.esmSource.length} characters; the ceiling is ${MAX_SOURCE_LENGTH}`,
    };
  } else {
    try {
      // Producer code runs in the renderer webview; the only injected
      // bindings are the guarded family registration API and preact's
      // element factories.
      const factory = new Function(
        "registerViewFamily",
        "h",
        "Fragment",
        `"use strict";\n${source.esmSource}`,
      );
      factory(guardedRegistration, h, Fragment);
      result = viewFamily(family) === undefined
        ? { ok: false, error: `the bundled module did not register family '${family}'` }
        : { ok: true };
    } catch (error) {
      result = { ok: false, error: String(error) };
    }
  }
  if (!result.ok) {
    console.warn(`maieutics: bundled family '${family}' degraded to state view — ${result.error}`);
  }
  attempted.set(source.esmSource, result);
  return result;
}

/** Registration as exposed to bundled modules: validated, first-wins (a
 * bundle cannot override built-in or previously registered families), and
 * shape-checked so a bad module fails materialization instead of throwing
 * later inside paint(). */
function guardedRegistration(family: string, module: unknown): void {
  if (typeof family !== "string" || family.length === 0) {
    throw new Error("registerViewFamily requires a non-empty family name");
  }
  if (viewFamily(family) !== undefined) {
    throw new Error(`refuses to override the already-registered family '${family}'`);
  }
  const candidate = module as { component?: unknown; collectDeps?: unknown };
  if (typeof candidate?.component !== "function") {
    throw new Error(`family '${family}' requires a functional 'component'`);
  }
  if (
    candidate.collectDeps !== undefined && typeof candidate.collectDeps !== "function"
  ) {
    throw new Error(`family '${family}' collectDeps must be a function when present`);
  }
  registerViewFamily(family, candidate as Parameters<typeof registerViewFamily>[1]);
}

/** Test seam: forget memoized outcomes (unit tests only). */
export function resetBundledMaterialization(): void {
  attempted.clear();
}
