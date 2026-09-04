# Maieutics Plugin Import Resolution

A self-contained specification for making plugin `deno.json` `imports` effective at
runtime, adding a supported dynamic-import API to the plugin SDK, and unblocking JSR
publication of `@maieutics/plugin-sdk`. Anyone reading this document top to bottom can
implement it. Every design constraint below is backed by an experiment against
Deno 2.9.5/2.9.6; the experiment record is summarized in §2 and the full repro
descriptions in §11.

Status: Implemented on `feat/plugin-import-resolution`. §5.1 records the runtime
blocker the integration test exposed and its resolution (the worker's denied
`import` grant, not the hooks pipeline).

## 1. Problem

A Maieutics plugin is a Deno package: a `deno.json` (package identity: `name` used for
specifiers, `permissions.default`, and `imports` for dependencies) plus a
`maieutics.json` (worker entrypoints, dependency declarations, isolation). Plugin
authors declare dependencies in `deno.json` `imports` and import them as bare aliases:

```ts
// deno.json: { "imports": { "@std/path": "jsr:@std/path@^1" } }
import { join } from "@std/path/join";
```

This works in the author's toolchain (`deno check`, editors) and fails at runtime. The
plugin host process is launched `deno run --config=<materialized root deno.json>` where
the root config contains exactly two mappings (`@ghostflyby/worker-actor`,
`@preact/signals-core`) plus `links` pinning the SDK to the kernel-materialized copy.
Deno resolves one process config; the plugin's own `deno.json` is never consulted at
runtime, so bare aliases fail with `Import "…" not a dependency and not in import map`.

Three clarifications that scope the problem precisely (all verified):

1. **Registry-installed plugins are unaffected.** JSR rewrites cross-package imports at
   publish time; published sources contain self-contained `jsr:` specifiers (evidence:
   `@std/streams@1.1.2/buffer.ts` ships `import { copy } from
   "jsr:@std/bytes@^1.0.6/copy"`). Dependency configuration is consumed at publish, not
   at runtime. The gap therefore applies **only to local directory plugins** — the
   development form.
2. **The loader hook is not the cause.** The SDK's `installDependencyLoadHook` passes
   every non-actor import through `nextResolve` unchanged (`mod.ts`); the failure
   happens after the hook, in native resolution, because the process map has no entry.
   The hook is also the sole mechanism that makes actor imports work (canonical match →
   `maieutics-stub:` short-circuit); it must not grow alias-rewriting responsibilities
   (§10.2).
3. **Imports need a worker `import` grant.** The native loader serves cached
   modules without a worker permission check, but the hooks pipeline's
   `jsr:`/`npm:` concretizer fetches registry version metadata and checks the
   worker's own `import` grant; a denied fetch neither falls back to the cache
   nor fails fast (§5.1). The host process carries `--allow-import` and workers
   cannot exceed the parent, so an undeclared grant defaults to the registry
   allowlist that the parent's bare flag already permits.

## 2. Verified environment facts

Each row is load-bearing for a decision below. Repro sketches in §11.

| # | Fact | Consequence |
|---|---|---|
| F1 | One process config; explicit `--config` replaces discovery; workers inherit the process config and never re-discover from their own entry (entry-dir config vs cwd config vs worker entry all tested) | Plugin aliases can only enter resolution through the process config — merging, not per-worker configuration |
| F2 | Without `--config`, discovery walks up from the **entry module** and only the main entry triggers it | Dropping the flag is at best equivalent to today (a deno.json already sits in the materialization dir); it cannot help plugin subdirectories |
| F3 | Workspace members' `deno.json` `imports` apply to modules under the member directory (static, dynamic, cross-scope graphs, worker graphs); member wins over root on conflict | The platform-native scoping mechanism — blocked structurally, see §10.1 |
| F4 | Workspace members must be nested under the workspace root; members without a config file are hard errors | A kernel-temp workspace cannot declare plugins; a pluginsRoot workspace needs kernel-written files in the config directory — rejected |
| F5 | `registerHooks` load hooks must shortCircuit or call nextLoad; declining (`undefined`/`null`/`{shortCircuit:false}`) is a contract error; `nextLoad("jsr:…")` hard-fails `Unsupported scheme "jsr"` | The hook pipeline cannot delegate foreign schemes to the native loader |
| F6 | `nextResolve` concretization of `jsr:` inputs is **permission-gated**: without a worker `import` grant it hangs on cold caches and raises `Requires import access to "jsr.io:443"` in some warm states (earlier recorded as "non-deterministic"; the §5.1 diagnosis replaced that reading). With the grant present it is deterministic across cold/warm caches and `@1`/`@^1`/exact/`npm:` forms | Never hand a rewritten `jsr:`/`npm:` specifier to `nextResolve`; workers need an import grant for registry resolution (§5.1). Bare aliases through the process map plus the §5.1 default grant are the supported form |
| F7 | Static import edges of runtime-loaded modules reach `resolve` hooks **only when a `load` hook is installed**; `node:module` `register()` exists but is a no-op | The SDK's unconditional load hook is load-bearing infrastructure |
| F8 | `links` overrides `imports` for the same alias key (tested with an imports entry pointing at a nonexistent jsr package) | Merging the root project's SDK alias into `imports` is safe; `links` keeps pinning the materialized copy |
| F9 | Trailing-slash import-map keys with `jsr:`/`npm:` values fail URL parsing natively; package-style keys with subpath extension are the supported form | Merge validation must reject that key/value combination |
| F10 | Variable dynamic imports flow through `resolve` hooks; direct static/dynamic imports of self-contained specifiers are robust through pass-through hooks | `dynamicImport` is viable; merged aliases work for both static and dynamic forms |

## 3. Decisions

- **D1 — Launch seam unchanged.** The host stays on
  `deno run --config=<materialized root deno.json>`. The file stays kernel-owned inside
  the per-process materialization directory (`mc-modules-*`); the plugins root remains a
  pristine configuration directory with no kernel-written files.
- **D2 — Merge plugin `imports` into the root config.** At scan time the kernel merges
  each discovered plugin's `deno.json` `imports` into the root config's `imports`
  (§4). This is a local-development shim: registry-installed plugins do not need it
  (§1.1).
- **D3 — Hook stays stub-only.** `installDependencyLoadHook` keeps exactly its current
  behavior: canonical/entry-URL match → `maieutics-stub:` short-circuit; everything
  else → `return nextResolve(specifier, context)`. No alias rewriting in the hook (§6).
- **D4 — `dynamicImport` SDK wrapper.** A typed thin wrapper over `import()` for
  runtime-computed specifiers (§6.1). Actor acquisition by runtime specifier remains
  `defineDependency` / `depActor` (no module loading; unaffected).
- **D5 — Publish hygiene as a parallel workstream.** Fix the 24 slow-type errors and
  gate with `deno publish --dry-run` in CI (§7). The dynamic imports in the
  SDK produce `unanalyzable-dynamic-import` **warnings**, which do not block
  publication.
- **D6 — Registry-installed plugin discovery is phase 2.** `ReadLocalImportTargets`
  skips `jsr:`/`npm:` targets today, so installed plugins are not discovered. The
  mechanism sketch and open questions are in §9; it is explicitly out of scope for this
  implementation round.

## 4. Kernel merge algorithm

New pure type `PluginImportMerger` in `Maieutics.Plugins`. Input: the finalized plugin
descriptors (after `PluginDependencyGraph.Validate`). Output: merged import-map entries
plus a diagnostics list reusing the graph-exclusion shape (`PluginId`, `Reason`,
`Detail`).

### 4.1 Rules (applied per plugin, in plugin-id ordinal order; output keys sorted)

| Rule | Condition | Action |
|---|---|---|
| R1 | Key is reserved: `@ghostflyby/worker-actor`, `@preact/signals-core`, `@maieutics/plugin-sdk` | Skip, Information log. Reserved keys are owned by the host materialization (imports + `links`) |
| R2 | Key normalizes (strip `jsr:` prefix, strip `@version` segment — same rule as the SDK hook) to a discovered plugin specifier (`<name>/<entrypoint>`) | Skip, Warning. Actor imports are owned by the load hook's canonical match, which precedes native resolution; a map entry would create a second, competing resolution path |
| R3 | Value starts with `./` or `../` | Absolutize against the plugin root: `new Uri(Path.GetFullPath(Path.Combine(root, value))).AbsoluteUri` (import-map relative values resolve against the config's own directory, which is the kernel temp dir — always wrong for plugins) |
| R4 | Value is scheme-ful (`jsr:`, `npm:`, `https:`, `node:`, `data:`) or absolute | Keep verbatim |
| R5 | Value is another bare specifier (alias chain) | Exclude the plugin, error. Chained aliases resolve against the merged map and would import cross-plugin ambiguity |
| R6 | Value is a string array | Take the first element, Warning (tolerant reader; fallback arrays are rare and their semantics do not survive a flat merge) |
| R7 | Trailing-slash key with `jsr:`/`npm:` value | Exclude the plugin, error (F9: broken natively). Trailing-slash keys with `file://` values (after R3) are fine |
| R8 | Key already present | Identical value → dedupe. Different value → exclude the **lexicographically later** plugin id, error naming both plugins and both values (deterministic; two plugins mapping one alias to different targets is a genuine ambiguity that must be loud) |

Excluded plugins do not start this cycle, matching the dependency-graph exclusion
precedent (log Warning with reason and detail).

### 4.2 Config write relocation

Today `PluginHostModule`'s constructor writes the root `deno.json`. Relocate the write
into a `PluginHostModule.WriteRootConfig(…)` invoked from `PluginHostManager.Start`
after `PluginDependencyGraph.Validate` and the merge, because the merged entries depend
on scan results. The constructor keeps materializing modules only. File shape
unchanged otherwise:

```json
{
  "imports": {
    "@ghostflyby/worker-actor": "jsr:@ghostflyby/worker-actor@0.6.0",
    "@preact/signals-core": "npm:@preact/signals-core@1.14.4",
    "@acme/widget": "jsr:@acme/widget@^0.3",
    "@std/path": "jsr:@std/path@^1"
  },
  "links": ["<materialized sdk dir>"],
  "minimumDependencyAge": 0
}
```

`links` keeps overriding the merged `@maieutics/plugin-sdk` entry (F8), so the root
project's own SDK alias merges harmlessly.

### 4.3 Lifecycle

- The config is rewritten on every host start; the plugin set equals the host-process
  lifecycle (a new plugin directory is not picked up by the watcher today either —
  `ReloadChangedPluginAsync` returns when no known descriptor owns the path).
- **Reload policy (§4.3 decision): reloads are not automatic by default.** Watched
  changes are recorded as pending (`AutomaticReload = false`, the default); an explicit
  apply (`ApplyPendingReloadsAsync`, exposed through the control surface) runs them.
  Opting in (`AutomaticReload = true`, a dynamic option — command/config hot-update)
  restores automatic in-process reloads for local edits.
- **Install-level changes are never automatic**, opted in or not: import-map changes
  warn "Restart the host process" (the process map is baked at host start) and registry
  version changes require the explicit apply to re-query the toolchain and rebuild —
  no background version detection.
- `ReloadChangedPluginAsync` re-resolves the descriptor and compares the old and new
  `imports` sets (`PluginImportMerger.SameMapping`); on change it logs the
  restart-required Warning while the worker rebuild proceeds for permission/source
  changes.

## 5. Runtime resolution paths after this change

```text
actor import (canonical form of a declared dependency's specifier)
    → hook canonical match → maieutics-stub: synthesized module → lazy acquire proxy

bare alias declared in some plugin's deno.json
    → hook pass-through → nextResolve → native resolution → process import map
    → jsr/npm registry client (robust for all version forms; verified matrix)

self-contained jsr:/npm:/relative import
    → hook pass-through → native resolution (unchanged)

runtime-computed specifier
    → SDK dynamicImport<T>(specifier) → import(specifier) → same paths as above
```

Declared-dependency aliases are stubbed even when a merged map entry exists, because
the canonical match runs before the fallback — actor semantics win by construction.

### 5.1 Resolved blocker (found by the §8 integration test)

The merge and its write are verified (the root config contains the merged
entries), but the end-to-end test initially showed the plugin worker's alias
import **never completing** inside the real host topology — no ready frame and
no visible error within the timeout, while minimal replicas of the same shapes
passed. Bisection over the worker's import form plus a manual host replica
(same materialized modules, flags, and environment) localized it:

- **Root cause — the worker's denied `import` grant.** `buildWorkerPermissions`
  denied `import` unless the manifest declared it, and the kernel config
  serializes an undeclared grant as an empty array (deny-all). Deno's
  `jsr:`/`npm:` concretizer fetches version metadata (e.g. `jsr.io`) while
  concretizing a constraint form; with that fetch denied, the hooked worker
  neither falls back to the cache nor fails fast — cold caches hang, and some
  warm-cache states raise `Requires import access to "jsr.io:443"`. The cache
  dependence made the failure look non-deterministic (F6's earlier reading).
  The native loader (no hooks) serves cached modules regardless of the worker
  grant, which is why the SDK-via-`links` probe always passed.
- **Fix** — `buildWorkerPermissions` defaults an undeclared (or empty) import
  grant to the registry-domain allowlist `jsr.io`, `npm.jsr.io`,
  `registry.npmjs.org` — exactly the set the host's bare `--allow-import`
  already permits, so the worker never exceeds its parent. A declared grant
  narrows or denies it. With the grant, alias-through-map, direct `jsr:@…@1`,
  and exact-version imports all resolve deterministically in the real topology,
  cold cache included.
- **Test fixture note** — current `@std/bytes@1` changed `concat` to accept a
  single iterable; the variadic call silently produced an empty `Uint8Array`
  and a bogus expected value in the discovery assertion.

The readiness gate is the `WorkerReadinessAcrossImportForms` theory
(sdk-only / sdk-alias / sdk-direct-jsr) in `PluginHostIntegrationTests`.

## 6. SDK changes

### 6.1 Dynamic imports — no wrapper

Plain `await import(specifier)` is the supported dynamic form and needs no SDK
wrapper: actor specifiers hit the load hook's canonical match (stub redirect),
bare aliases resolve via the process import map, and self-contained
`jsr:`/`npm:` specifiers resolve via the registry — the same pipeline as static
imports. The `unanalyzable-dynamic-import` warning at publish is expected and
benign (the specifier is provided at runtime and not rewritten by JSR). An
earlier `dynamicImport<T>` wrapper was removed: it was a pass-through with no
behavior the runtime does not already apply to every `import()` call.

### 6.2 Hook documentation (no behavior change)

`installDependencyLoadHook` gains comments recording two non-obvious invariants:

1. The unconditional `load` hook is load-bearing: static import edges of
   runtime-loaded modules reach `resolve` only while a `load` hook is installed (F7).
   Removing the "empty" load handler silently breaks the stub redirect for static
   imports.
2. The `resolve` fallback must remain a pass-through. Rewriting aliases to
   `jsr:`/`npm:` specifiers inside the hook is a documented non-starter: the hooks
   pipeline cannot decline unresolvable URLs and its `jsr:` concretization is
   non-deterministic (F5, F6, §11).

## 7. Publish hygiene (`@maieutics/plugin-sdk`)

Current `deno publish --dry-run` fails with 24 `missing-explicit-type` errors — all 24
are the exported control factories in `widgets/index.ts:105-128`
(`export const IntSlider = controlFactory("IntSlider")` …) — plus one
`missing-license` error. The 3 `unanalyzable-dynamic-import` warnings are non-fatal.

1. Annotate: `export const IntSlider: ControlFactory = controlFactory("IntSlider");`
   (the `ControlFactory` type is already exported at `widgets/index.ts:141`).
2. License: a project-level decision, deliberately out of scope here. Note that
   `deno publish --dry-run` fails on a missing license, so the CI gate below is
   completed by that decision; until then the step reports the license error.
3. CI: add a step after the existing deno checks:
   `deno publish --dry-run --allow-dirty --no-provenance` in
   `deno/maieutics-plugin-sdk`. Warnings pass; errors fail the build.

## 8. Test plan

.NET (xUnit v3, following `Maieutics.Agent.Tests` / product integration conventions):

- `PluginImportMerger` unit tests: dedupe of identical entries; conflict exclusion
  (later id, both named); `./`-absolutization on POSIX and Windows separators; reserved
  keys skipped; actor-specifier keys skipped; trailing-slash + `jsr:` rejected;
  bare-chain values rejected; array value tolerance; deterministic sorted output.
- Relocation integration: `PluginHostManager.Start` writes the merged config; a local
  plugin whose extension point handler computes a value through a `@std/bytes` alias
  resolves at runtime (end-to-end proof of §5). Ships as the
  `WorkerReadinessAcrossImportForms` theory gating worker readiness for every
  import form (sdk-only / sdk-alias / sdk-direct-jsr), with the §5.1 import-grant
  default in place.
- Reload: changing a plugin's `imports` triggers the restart-required warning and the
  worker still rebuilds.

Deno (deno test, following the existing suites):

- Sentinel `jsr`-dependency matrix: an SDK-shaped hooked worker (pass-through
  resolve + load) loading static and dynamic imports across constraint forms
  (`@1`, `@^1`, exact, `npm:`) through process-config aliases and as direct
  specifiers. Guards the F6 flakiness surface: if it starts failing on a Deno
  upgrade, the failure maps to the upstream issue in §11 rather than to plugin code.

Repo acceptance after implementation: `dotnet test Maieutics.slnx`,
`dotnet build Maieutics.slnx --no-restore -warnaserror`, `deno task --config
deno/deno.json check && deno task --config deno/deno.json test`, `git diff --check`.

## 9. Phase 2 (out of scope): registry-installed plugin discovery

The discovery contract is uniform over one unit: a **package directory holding the
sibling manifest pair** — `deno.json` (identity, permissions) and `maieutics.json`
(entrypoints, dependencies, isolation). `ScanProject` already implements it: the root
project is read as the sibling pair, and each path-valued `imports` target resolves to
a package directory whose sibling pair is read by the same `PluginManifest.TryLoad`
path. Today `ReadLocalImportTargets` follows only path-valued targets;
`jsr:`/`npm:` targets are skipped — the scan comment assigns their resolution to the
Deno toolchain at install time.

Phase 2 therefore adds no declaration surface and no manifest changes: it wires
registry-valued targets into the same contract by asking the Deno toolchain to resolve
the specifier to the package directory, after which discovery proceeds identically.
Discovery stays **one level** — the installed set is exactly the root's direct
`jsr:`/`npm:` dependencies — because transitive plugin-to-plugin references need no
kernel-side walking: the worker's loader hook judges actor imports at runtime,
redirects them to acquire stubs, and the host routes acquires to the (already
started) owner worker. `maieutics.json` `dependencies` remains the contract for start
ordering, cascade, and the missing-dependency exclusion when a referenced plugin is
not installed.
Design questions for that follow-up: the toolchain query used at scan time, the
worker read-grant scope for toolchain-managed locations, and reload semantics when an
installed version changes.

## 10. Alternatives considered

### 10.1 Workspace members rooted at the plugins root — rejected structurally

Members must nest under the workspace root (F4) and the root config must therefore
live at the plugins root, which the plugin root's config-directory constraint forbids.
A kernel-temp workspace cannot declare the plugins (also F4). Additionally, members
require a config file per plugin while `deno.json` is optional for plugins
(`maieutics.json` is the identity file), and the member list would need maintenance in
a file the kernel does not own.

### 10.2 Alias rewriting inside the load hook — rejected on verified Deno defects

The hook would rewrite aliases to `jsr:`/`npm:` and hand them to `nextResolve`. Three
pipeline defects block it (F5, F6, §11): non-deterministic `jsr:` concretization, no
decline path for unresolvable URLs, and a loader that hard-fails foreign schemes. It
would also violate the D3 boundary: the hook's only rewrite target is the actor stub,
which short-circuits and never touches `nextResolve`.

### 10.3 Launch without `--config` — rejected as equivalent-at-best

Discovery is entry-based and single-shot; workers inherit the process config (F1, F2).
The materialization directory already contains the deno.json, so dropping the flag
changes nothing about which entries are visible, and loses the explicit pin to a
kernel-owned file. `deno run <directory>` is not supported
(`ERR_UNSUPPORTED_DIR_IMPORT`); `file://` entries are already in use and behave
identically.

### 10.4 Self-contained specifiers only — rejected on author cost

Requiring plugins to write `jsr:@std/path@^1/join` inline moves resolution state into
every import site, breaks offline/locked workflows, and is precisely the authoring gap
this spec closes.

## 11. Upstream issue material (Deno)

Minimal repros, Deno 2.9.6, to file against `deno` (node-compat `module` hooks):

1. **`registerHooks` load hooks cannot decline.** Any return without
   `shortCircuit: true` and without `nextLoad` raises
   `load hook must return { shortCircuit: true } or call nextLoad`; `nextLoad` on a
   `jsr:`/`npm:` URL raises `Unsupported scheme "jsr" … Supported schemes: blob, data,
   file`. There is no way to delegate an unresolvable URL to the native loader, so a
   resolve hook that rewrites a bare specifier to a registry scheme fails at load.
2. **(Superseded by §5.1)** `nextResolve` concretization of `jsr:` inputs
   appeared non-deterministic — the same specifier string succeeded as a direct
   static/dynamic import (native path) but succeeded/failed as a `nextResolve`
   input across processes. The variance was the denied worker `import` grant
   interacting with cache state, not a concretizer race: with the §5.1 default
   grant, the same inputs concretize deterministically. Do not file this as an
   upstream issue.
3. **`node:module` `register()` is a no-op.** The API exists but registered hooks are
   never invoked (no resolve/load logs; bare specifiers fail natively).
4. **(Documented behavior question)** Static import edges of dynamically imported
   modules reach `resolve` hooks only when a `load` hook is also installed.

## 12. Implementation order

| Step | Scope | Content |
|---|---|---|
| 1 | .NET | `PluginImportMerger` (pure) + unit tests (§8) |
| 2 | .NET | Config-write relocation to `Start`; merge + write; integration test (§8); reload import-change warning (§4.3) |
| 3 | SDK | `dynamicImport` + `installDependencyLoadHook` comments (§6) |
| 4 | SDK | Publish hygiene: 24 annotations (§7) |
| 5 | CI | `deno publish --dry-run` step; completed by the license decision (§7) |
| 6 | Deno | jsr dependency matrix sentinel test (§8) |
| 7 | Docs | Upstream issues filed (§11); pointers from `docs/README.md` if needed |
