# ADR 0029: MCP Client Reverse-Request Surface

Status: Draft. The `mcp.json` references below now denote the plugin-scoped MCP
data file (ADR 0033); capability semantics are unchanged.


Date: 2026-09-17

## Context

Maieutics is a pure MCP client with a complete forward surface (tools, resources,
progress, cancellation — ADR 0013 plus the ADR 0026 resource plane) but declares no
client capabilities: no roots, no elicitation, no resource subscription handling.
Servers that want filesystem boundaries, user input mid-tool-call, or catalog-change
signaling cannot interoperate. The 2026-07-28 protocol revision (SEP-2575) also
removed `resources/subscribe`/`resources/unsubscribe` in favor of a single long-lived
`subscriptions/listen` request with per-notification-type opt-in, which is the only
sanctioned server-to-client notification channel a server may use without a
capability handshake for it.

Concrete requirements exist for all three capabilities. They share one property:
they are server-to-client *reverse requests*, so they share one trust question —
they let a configured server observe workspace facts (roots) or drive user-facing
prompts (elicitation).

## Decision

1. **Capability follows handler presence and a per-server flag.** `mcp.json` server
   blocks gain two Maieutics extension keys, `roots` and `elicitation`
   (`bool?`; unknown to Claude/Cursor-format files, which ignore them). A null
   resolves by transport: **stdio defaults to true, HTTP defaults to false** — the
   same trust line as process spawn: a stdio server is already launched through the
   permission module (ADR 0018 §7), while a remote HTTP server gets nothing it can
   use for observation or social engineering until it is opted in. The SDK declares
   a capability exactly when the corresponding handler is set, so the flag and the
   handler are one decision point in `McpServerGeneration`.

2. **Roots are the live workspace root, exposed through a narrow seam.** `RootsHandler`
   returns one root: the current workspace root as a `file://` URI with the directory
   name. The handler reads the workspace root on every request, so `%workspace
   open/close` takes effect at the server's next query with no notification
   machinery. The path disclosure is the feature's purpose, not a leak to prevent.
   `Maieutics.Mcp` sees only an `IMcpWorkspaceRootsSource` seam (same layering as
   `IMaieuticsMcpController`); the composition root binds it to `Workspace`. No
   workspace (or a vanished root) yields an empty result, never an error.

3. **Elicitation routes through the existing frontend input loop, with strict
   attribution.** The elicitation handler maps `elicitation/create` onto the
   kernel's pending-input surface: the REPL-owned input id minting and completion
   routing sink into a kernel-side `PendingInputRegistry` (requestId → completion),
   of which the REPL stdin flow and elicitation become the two clients. The wire
   frame `input.request` gains additive optional fields (`schema`, `serverId`);
   clients that predate them can still decline. Attribution uses the connection's
   in-flight tool-call registry (the tool wrapper registers the calling Agent
   session at invoke boundaries): exactly one attributable session → route the form
   there; zero or ambiguous → answer `cancel` and log — a form is never shown to a
   guessed session. Schema is untrusted input: primitive-kind whitelist, message and
   size caps, `format: password` maps to the frame's existing secret flag, and
   content never reaches logs (action, field count, byte count only). Limits: at
   most one in-flight elicitation per connection, per-call and process-wide pending
   caps; exceeded requests answer `cancel`. The wait is bounded and consumes the
   tool call's existing request-timeout budget (timeout → `cancel`); pausing that
   budget for user input is deliberately out of scope.

4. **Resource subscriptions are catalog-level only, with best-effort listen.**
   `notifications/resources/list_changed` joins the refresh-signal path (same
   bounded channel as `tools/list_changed`), so a catalog change re-runs the
   existing idempotent refresh. After initialize, the connection also issues one
   `subscriptions/listen` request (SEP-2575) opting into `toolsListChanged` and
   `resourcesListChanged`, held for the connection generation's lifetime and
   cancelled with it; a server that rejects the method (older revisions) or the
   opt-in set is tolerated with a debug log — the unsolicited-notification path
   keeps working where it exists. Per-URI `resourceSubscriptions` are deferred: the
   read plane reads fresh on every read, so nothing is stale, and no consumer needs
   per-URI push.

5. **Both reverse-request handlers never block the connection's message loop on each
   other.** Handlers run under the SDK's per-message fire-and-forget dispatch;
   awaiting user input in the elicitation handler holds one in-flight server
   request, which the protocol permits, while other traffic continues.

## Consequences

- Servers configured with stdio defaults gain workspace-boundary and user-input
  interoperability without config; HTTP servers need explicit opt-in per key.
- The frontend protocol changes additively (`input.request` optional fields);
  the `PendingInputRegistry` becomes the single input-completion path for REPL
  stdin and elicitation.
- `McpServerDefinition` grows the two flags and folds them into the generation key,
  so flipping a flag recycles the connection like any other definition change.
- `subscriptions/listen` is issued through the public raw session request surface;
  if the SDK later ships a high-level wrapper, the adapter is the only change.
- Elicitation attribution failure is a typed, logged `cancel`, never a prompt on a
  guessed session and never a silent hang.
