# ADR 0043: Model-Callable Plugin Tools

Status: Draft

Date: 2026-10-20

Related: ADR 0004 (extension protocol; the explicit-adapter rule), ADR 0010
(`AIFunction` as the only provider-visible function contract), ADR 0016
(symbol-identified extension points), ADR 0020 (extension-host actor boundary),
ADR 0033/0034 (plugin-declared MCP servers; the tool-surface precedent),
ADR 0037 (declaration approval), ADR 0040 (contribution framework; why tools are
not a contribution kind), ADR 0042 decision 9 (connection-gated tool families and
`ReservedToolNames`)

## Context

ADR 0004 draws a deliberate line: extension APIs are not provider tool
definitions, and "a capability becomes model-callable only when a separate
adapter explicitly exposes it as an Agent tool." ADR 0004 also commits to that
adapter existing. Today no such adapter exists. A TypeScript plugin can call
kernel tools (`capabilities.invokeTool`, REPL `tools.invoke`), adjust an existing
MCP tool listing (`mcp.adjust`), observe invocations (`tools.preInvoke` /
`tools.postInvoke`), and contribute prompt-visible skills — but it cannot give
the model a new callable function. The only route is to ship a separate MCP
server, which forces a second process, a second protocol, and a second
lifecycle for what is often one pure function.

The pieces needed for the direct route already exist:

- the plugin host scans worker exports for versioned extension-point markers and
  reports `(pluginId, exportName, extensionPoint)` registrations (ADR 0016);
- `InvokeExtensionPointAsync` is a request-response channel with a per-call
  budget, an approval gate, and host-disconnect failure semantics;
- `AIFunction` is the one provider-visible function contract, and `AIFunction`
  subclasses already wrap MCP tools for progress and naming;
- `FinishAcquisitionAsync` assembles each run's tool list (built-in, terminal,
  IDE, MCP) with one global exposed-name guard.

## Decision

1. **One new extension point, `Tool`, with a phase-tagged request.** A tool
   export is an ordinary extension-point implementation marked with
   `Symbol.for("maieutics/extensionPoint/v1/tool")`. The host's existing
   registry, reload diff, approval gate, and `host.invoke` transport carry it
   unchanged. The wire name joins `ReplExtensionPointName`; no capability is
   added, because a tool is dispatched by the kernel, not requested by a worker.

2. **One export is one tool.** Registrations are keyed by
   `(pluginId, exportName)`, and the SDK's export scanner is keyed by extension
   point, so a module may not declare two tools. A second marked export logs a
   diagnostic and replaces the first. Tool identity is the export, so a reload
   of one module never disturbs another tool's sticky state, and no
   name→export table exists on either side.

3. **The declaration is the schema, plus optional classification hints.**
   `defineTool({ name, description, input, timeoutMs?, annotations?, handler })`
   derives the provider-visible input schema from the same value that validates
   arguments and infers the handler's parameter type. The builder emits
   `additionalProperties: false` by default, does no type coercion, and exposes
   `schema.raw` for JSON Schema the builder cannot express. The kernel validates
   the declaration once (name grammar, description length, top-level object
   schema, 64 KiB schema bound, timeout range, annotation shape) before it can
   reach `AgentSession.CreateToolRegistry`.

   `annotations` follows MCP's `ToolAnnotations`: `title`, `readOnly`,
   `destructive`, `idempotent`, `openWorld`, with MCP's pessimistic defaults for
   absent members (non-read-only, possibly destructive, non-idempotent,
   open-world). They are **hints, not enforcement** — no authorization decision
   reads them, and MCP's own guidance applies: a consumer must not make tool-use
   decisions on annotations received from an untrusted source. The kernel carries
   them on `AIFunction.AdditionalProperties` under
   `maieutics.pluginTool.annotations`, which provider adapters never render, so
   they reach approval and presentation surfaces without widening the provider
   wire. A declared-but-malformed hint fails the declaration rather than silently
   keeping the default: a mistyped hint would otherwise be read as an assertion.

4. **Describe is sticky last-good; call is per-invocation.** The kernel describes
   a `Tool` export when it first appears and again when the plugin is forced
   (reload, approval transition), caches the definition by
   `(pluginId, exportName)`, and keeps the last good definition when a pass
   fails. Never-described or still-failing exports are absent from the tool
   surface. Definitions are immutable per run: a run acquires a snapshot, and a
   later frame only changes the next run's tool list — the same lease discipline
   MCP generations use.

5. **The adapter is an `AIFunction`, and it joins the one merge point.** A
   plugin tool is wrapped as `PluginToolAIFunction`, which returns
   `JsonElement` envelopes through the existing `ToolJson` helpers. It is added
   after MCP in `FinishAcquisitionAsync` under the same global name guard, so a
   plugin tool can never shadow a built-in, terminal, IDE, or MCP tool.
   Reserved built-in and gated-family names are additionally rejected at
   describe time.

6. **Cancellation and timeout are real, not abandoned waits.** Each tool call
   carries the Agent call id and session id. The declaration's `timeoutMs`
   (default 120 s) is the **handler's** budget: the worker starts the clock when
   the handler begins and enforces it with its own abort, so a cold worker start
   (an on-demand spawn per ADR 0035) is never charged to the handler. The kernel's
   own wait adds a separate startup allowance on top, so its bound can only fire
   after the worker's — a short declaration cannot time out because the worker was
   cold. A caller cancellation or the worker's budget expiry posts
   `host.invokeCancel`, the host posts a `tool.cancel` worker frame, and the SDK
   aborts the handler's `AbortSignal`; a cancel that arrives before the dispatch
   registers is remembered rather than dropped. A cancelled call never reports
   success, and an over-budget one reports `tool_timeout` rather than a bare
   cancellation. This keeps invariant 9 (cooperative cancellation) true for plugin
   tools instead of shipping a tool the turn cannot interrupt.

7. **Tool failures are recoverable model input.** `ToolError(code, message)`
   maps to `{"status":"error","code","message"}`; an unexpected exception maps
   to `tool_failed` with its message; a malformed or timed-out response maps to
   `tool_failed` / `tool_timeout`. A plugin bug ends one tool call, not the
   turn, matching MCP remote-tool failure semantics.

8. **Approval needs no new fingerprint domain.** The fingerprint already hashes
   every worker's export name and entry URL (ADR 0037), so adding a tool export
   changes the fingerprint and requires re-approval. Module code stays outside
   the fingerprint, exactly as it does for `mcp.discover` and the skill
   generator; tool declarations are code, not manifest surface.

9. **Non-goals.** No declarative `extensions.Tool` form (schema and handler must
   be co-located or the manifest can describe a handler that does not exist). No
   progress channel in v1 (`host.invoke` is one-way request-response; progress
   needs a new frame family and a per-call budget). No model-side hook chain:
   model tool calls do not pass `ToolPreInvoke`/`ToolPostInvoke` today for any
   tool family, and making them do so is a separate, cross-cutting decision. No
   entry in the script-tool registry: scripts call kernel tools, the model calls
   plugin tools.

## Consequences

- A TypeScript function that declares its schema is immediately model-callable,
  with no second process and no MCP server.
- The plugin contribution framework (ADR 0040) is untouched: tools are a
  worker-computed extension point with a kernel-side consumer, not a
  declarative contribution kind, and no fingerprint domain or reconcile pass is
  added.
- A tool that ignores its `AbortSignal` keeps running after the kernel stops
  waiting. Its result is discarded, and the cancel frame is delivered even to a
  worker that is still starting, so a handler that observes the signal unwinds;
  one that ignores it is bounded only by the host's own lifecycle, not by a
  per-call termination (which would kill the plugin's other state).
- Because one export is one tool, a module that wants several tools exports
  several modules. That is the price of keeping registration identity and
  sticky semantics aligned; a name→export multiplexer can be added later
  without changing the wire.
- ADR 0044's approval gate consumes the classification this ADR publishes. A
  plugin tool that declares `destructive`, or declares no annotations at all
  (the defaults are pessimistic), requires approval; an MCP tool publishes no
  classification and never asks. A plugin author who wants a tool to run
  unprompted must declare `readOnly: true` truthfully, or the operator must
  exempt it in policy.
- The schema beyond its top-level object shape is not validated by design (the
  builder guarantees the shape on the recommended path; `schema.raw` does not).
  A malformed raw schema passes describe and then fails at the provider request,
  which is a turn-wide failure rather than a tool-scoped one.
- Progress reporting and model-side tool hooks remain known gaps, recorded here
  rather than silently implied.
