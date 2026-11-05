# ADR 0047: Explicit live Vue output components

Status: Accepted

## Context

The shared passive Markdown and data-template renderer cannot execute a VitePress-style counter:
script setup, Vue bindings, events and scoped CSS require a real component compiler and runtime.
The kernel must remain the shared SSR authority without executing authored code in its privileged
renderer parent. Portable notebook snapshots must not become executable because of their MIME type.

## Decision

Add an explicit live rich-output lane, `application/vnd.maieutics.vue+json`, with version 1, a complete
source snapshot and `markdown` or `sfc` format. An authorized existing REPL producer publishes it
with text fallback. Ordinary assistant Markdown and data-template evaluation keep their current
policies. Add authenticated stateless `/v1/render/vue` and optional `rendering.vueVersion:1` to the
existing frontend protocol v1; retain the existing request, admission and revision guards.

Reuse public mdit-vue component/SFC plugins for Vue-in-Markdown, Vue 3.5.13's official SFC, template
and scoped-style compilers, Sucrase 3.35.0 for TypeScript erasure, and Vue SSR/runtime-dom 3.5.13.
Compile complete input only. Static `vue` imports bind to the shipped runtime; other imports, dynamic
import, import.meta, external blocks, custom blocks, CSS modules and preprocessors are rejected.
No Vite site server or runtime package resolution is introduced.

Each evaluation owns a fresh Deno worker with no permissions, a private transferred reply port and
a five-second deadline. Its trusted runtime is prebundled as JavaScript text and loaded as a module
data URL. The compiler parent never evaluates authored code; cancellation/disposal terminates the
worker and closes its ports. Worker exceptions return fixed diagnostics. No new process permissions
are granted. The worker budget is not a memory quota or a security claim about runtime defects.

Return initial SSR HTML, compiled styles and the same component factory to the live frontend. Each
output owns a hydrated Vue app and unmounts it on replacement or disposal. Client reactive updates
stay local; complete source replacement resets component state. Setup executes for both SSR and
hydration and must produce deterministic initial state. External operations and producers stay in
the authorized REPL. Async setup/Suspense, client-only resource hydration, cross-output component
registries and state-preserving source patches remain outside this verified contract.

Browser execution follows ADR 0038's existing executable bundled-view posture, under the notebook
CSP; it is not a browser sandbox. Only kernel-compiled factories received for a current explicit
live request are mounted. A saved MIME bundle cannot supply a factory. Reading and saving notebook
v1 discard Vue source MIME and retain producer-provided fallback items. Reading also strips
existing bundled-view executable sources, matching their save-time rule. Data templates remain
persistable because they contain data and restricted expressions rather than executable modules.

## Consequences

A model can publish real interactive Vue/VitePress content through the existing tool/display lane,
shared kernel SSR works offline in NativeAOT deployments, and frontends can opt in independently.
No Agent, canonical-history or notebook-format version changes are needed. Source and component
state are live presentation only; reopening a file shows fallback until a producer redisplays it.
This increment supplies complete component snapshots, not the streaming document runtime, late
producer subscription protocol, partial script execution or structural HTML patches. The design and
normative protocol specify bounds, lifecycle and remaining work.
