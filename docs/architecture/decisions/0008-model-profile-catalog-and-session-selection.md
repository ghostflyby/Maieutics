# ADR 0008: Model Profile Catalog and Session Selection

Status: Accepted

Date: 2026-07-19

## Context

One Maieutics Kernel must use multiple model APIs without moving canonical history into a provider or replacing an
active model/tool loop. Provider credentials and endpoints are reusable connection concerns, while a user-facing model
choice also includes a concrete model identifier. Treating both as one global provider option prevents safe switching
and duplicates credentials when several profiles share one API source.

## Decision

Configuration defines case-insensitive named `Sources` and `Profiles`. A source selects a registered provider factory
and owns provider-specific connection options. A profile references one source and one model identifier. The catalog has
one configured default profile and may have one Kernel-lifetime session override.

The runtime configuration constructor binds and validates the candidate catalog but does not construct provider or MCP
generations. Generic Host startup builds the initial immutable snapshot asynchronously after plugin-host startup and
awaits it as a readiness barrier before starting the Jupyter Kernel. Startup cancellation or construction failure
retires every generation created by the rejected snapshot before the failure reaches the Host. There is no synchronous
startup wrapper.

Each successful `AcquireAsync(CancellationToken)` returns a reference-counted generation lease containing an immutable
`IChatClient`, model identity, capabilities, Agent options, and the MCP/plugin tool generations available at the
operation boundary. Acquisition may wait for plugin readiness and rolls back partial leases asynchronously without
holding configuration locks. A run never reacquires or switches this lease during provider or tool iterations.
Successful transcript turns record the profile, provider, and model used, but history replay sends only
provider-neutral messages.

Notebook control cells provide explicit selection using the flat syntax established by ADR 0012:

```text
%model [current|list]
%model use <profile>
%model reset
```

They are Kernel control operations, not Agent turns. They do not call a provider or modify the transcript. Selection is
not persisted across Kernel restarts and does not write configuration. Selection is an asynchronously cancellable
operation because an automatic profile may need to construct and then retire a provider generation. A failed or
canceled selection leaves the previous selection unchanged and awaits retirement of the uncommitted generation. No
synchronous selection wrapper is retained.

## Consequences

- OpenAI Responses, OpenAI Chat Completions, and Anthropic Messages can be selected without changing Agent APIs.
- Active runs remain stable across configuration reload and Notebook selection changes.
- Catalog reload is atomic across every profile client, rather than partially applying healthy entries.
- Host readiness, rather than DI resolution, is the boundary after which a runtime snapshot is available.
- Removing a manually selected profile falls back to the configured default with an observable warning.
- Google and future providers extend the source-factory and capability boundary without changing the schema.
- Automatic routing, fallback, load balancing, and Agent-controlled switching remain out of scope.
