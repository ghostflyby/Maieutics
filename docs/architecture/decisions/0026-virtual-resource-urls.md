# ADR 0026: Virtual Resource URLs

Status: Draft

Date: 2026-09-13

## Context

`workspace://local/...` is currently the only address space the kernel can read:
`read_text`, `list_directory`, and `search_text` resolve it inside
`Maieutics.Execution.Workspace` with strict path grammar, symlink rejection, and
openat-based containment. Three consumers now need more than files:

1. **MCP servers expose resources.** The MCP client (ADR 0013) surfaces only
   tools today; `resources/list`, `resources/templates/list`, and
   `resources/read` are unused. Server resources carry their own URIs
   (`postgres://...`, `notes://...`, occasionally `file://...`) and RFC 6570
   templates.
2. **Deno children cannot reach kernel data.** REPL cells can call script tools
   through the control bus (`maieutics.tools.invoke("read_text", ...)`), but the
   natural JS surface — `fetch()` — only speaks `http(s)`, so virtual
   workspace state and MCP resources are invisible to idiomatic cell code.
3. **Custom URL schemes have no seam.** A user-configured "virtual workspace"
   (an internal notes server, a dataset catalog) has no way to join the read
   surface that files already use.

Reads must keep one entry: the model already knows `read_text`; giving each
scheme a separate tool multiplies tool surface and schema churn. Binary data
must stay binary until its target representation requires encoding
(invariant 26), transfers stay bounded, and the REPL child's permission
footprint must not grow for this.

## Decision

1. **One resource plane behind the existing read entry.**
   `Maieutics.Execution` gains a `ResourceRegistry` of `IResourceProvider`s.
   `read_text` keeps its signature and line semantics; its `uri` parameter now
   accepts any registered resource URI. `workspace://local/...` URIs keep the
   existing zero-copy `Workspace` path and all existing error codes; other
   schemes resolve through the registry into a stream and flow through the same
   bounded UTF-8 line reader (so UTF-8 validation, binary rejection, line and
   byte caps are uniform). A new `list_resources` tool returns the catalog of
   non-file resources (MCP resources and templates) plus any registration
   conflicts; files keep `list_directory`. Binary reads are served to code via
   the fetch bridge, not to the model.

2. **Claims, precedence, and conflict resolution.** Every provider declares
   claims `(scheme, authority | wildcard)`. Resolution for one URI considers
   all claiming providers and picks the winner by, in order:
   (a) claim specificity — an `authority` match beats a scheme-wide wildcard;
   (b) provider class — `BuiltIn` beats `Custom` beats `Mcp`;
   (c) registration order — workspace first, then custom providers in config
   order, then MCP in `mcp.json` server order.
   Identical claim tuples within one class are conflicts: the later registration
   is disabled and recorded in `ResourceRegistry.Conflicts`, surfaced by
   `list_resources` and logged — resolution never silently overrides.
   Schemes `workspace`, `mcp`, and `file` are reserved: non-built-in claims on
   them are disabled with a conflict record (the built-in workspace plane cannot
   be shadowed; `file://` stays unallocated so OS paths cannot sneak past
   workspace containment). Within the single MCP provider, ambiguity between
   servers is resolved deterministically by `mcp.json` server order with exact
   resources before template matches; the unambiguous escape hatch
   `mcp://<serverId>/<encoded resource uri>` always reads from one named server.

3. **MCP resources join as one provider.** `McpServerGeneration` refreshes
   `resources/list` and `resources/templates/list` alongside tools (a server
   without the capability contributes an empty catalog instead of failing the
   connection; refresh failures keep the previous catalog). Reads call
   `resources/read` under the existing lease and request-timeout discipline.
   Template matching supports RFC 6570 level 1–2 (`{var}`, `{+var}`), which
   covers real-world MCP templates; richer forms do not match and surface as
   unknown URIs.

4. **Custom schemes are config-declared bridge providers.** A
   `Maieutics:Resources:CustomProviders` entry (`name`, `scheme`, `kind:
   httpBridge`, `endpoint`) registers a provider that resolves
   `<scheme>://...` by `GET <endpoint>?uri=<encoded>` and streams the response
   body with its `Content-Type`. The endpoint host is the trust and network
   boundary (same posture as an MCP `http` server); the bridge adds no
   rewriting. Custom providers are startup-bound like terminal options; MCP
   resource claims follow configuration reloads. New provider kinds plug in by
   implementing `IResourceProvider`; nothing else in the plane changes.

5. **Deno children fetch virtual URLs through the control channel.** The
   control host maps `GET /v1/resource?uri=<encoded>` behind the existing peer
   identity middleware, streaming the provider's bytes and `Content-Type`, with
   typed JSON errors (`resource_invalid_uri`, `resource_unknown_scheme`,
   `resource_not_found`, `resource_too_large`, `resource_provider_failed`,
   `resource_provider_unavailable`) and status codes 400/404/413/502. The
   REPL worker patches `globalThis.fetch`: requests whose scheme is not
   `http(s)/ws(s)/blob/data/about` are translated to one GET on the control
   channel — Unix via the existing `Deno.createHttpClient({proxy:
   {transport:"unix"}})` loopback-path grant, Windows via the loopback
   `host:port` plus bearer credential — both already granted by the REPL
   baseline, so the permission footprint is unchanged. Only `GET` is supported;
   other methods on virtual schemes throw `TypeError`. The patch installs in
   the REPL worker global only (nested workers are profile-neutral); plugin
   workers keep the bus path (`maieutics.tools.invoke`) and the HTTP endpoint
   is reachable for a later bridge. `maieutics.resources.{list,read}` is added
   to the client namespace over the same endpoints.

6. **Authorization posture.** The control endpoint authenticates exactly like
   its siblings (Unix socket peer credentials, Windows bearer bootstrap), so a
   resource read is as privileged as a script tool call from the same child.
   The kernel-side read tools remain the only path for the model; providers get
   the same request-timeout and byte-bound treatment as existing reads.

## Consequences

- The model reads files and virtual resources with one tool and one result
  shape; `list_resources` is additive and absent when no providers register.
- The registry is stateless: resolution recomputes from live provider claims,
  so MCP reloads and reconnects take effect without registry invalidation.
- `read_text` on non-workspace resources buffers at most one resource body
  (MCP SDK materializes content); workspace reads stay zero-copy.
- The fetch patch changes REPL-cell behavior for unknown schemes from
  immediate `TypeError` to a kernel round trip; failures still surface as
  rejected promises with the typed code in the message.
- Reserved-scheme conflicts and bridge misconfigurations are startup- or
  catalog-visible, never silent.
