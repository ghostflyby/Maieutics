# 0050 — Retire custom component families and use Vue

Date: 2026-11-12

Status: accepted. Supersedes ADR 0038's component runtime/authoring decisions and ADR 0024's shipped
classic widget producer/renderer. Generic comm transport, plugin approval and HTTP page ownership
remain unchanged.

## Context

The backend reactive core already uses Vue, and assistant rendering already ships a standalone Vue
ESM. Keeping an independent component engine, generic family registry and widget JSX authoring
framework leaves two incompatible UI models and duplicated browser runtimes.

## Decision

1. Delete the old browser engine, family registry, dependency collection/paint scheduling and
   injected bundled component-source API. Do not retain renamed compatibility wrappers.
2. Delete classic widget factories/runtime, the widget JSX dialect and REPL TSX transform/imports.
   The REPL returns to the upstream ordinary TypeScript transform.
3. Preserve required product form interactions with ordinary Vue components in the shared browser
   ESM. The VS Code adapter owns only renderer messaging and disposal calls.
4. Use `application/vnd.maieutics.form+json` version 1 and comm target `maieutics.form` for plain form
   state. Advertise UI capability version 2 with formVersion 1. Old clients get the textual fallback;
   generic comm framing and the frontend protocol version remain unchanged.
5. Snapshot creation/read whitelists form data and marks it permanently readonly. Decode historical
   form output only as frozen data, discard old component source and never reopen a model from a
   file. Other retired outputs retain readable JSON/text.
6. Publish SDK 0.3 as the deliberate removal boundary. Await form creation/state publication and
   bound owned models/updates; native Vue refs express the worker-side form state.

## Consequences

Built-in forms keep submit/cancel, typed fields, local drafts, multi-view state and deterministic
cleanup. The old custom component authoring and classic widget APIs are intentionally unavailable.
A general approved Vue component catalog, model projection protocol and action-result RPC remain
explicit subsequent work; normal Vue components are the target and no replacement family dialect
is introduced. See the current [UI design](../../custom-ui-framework-design.md).
