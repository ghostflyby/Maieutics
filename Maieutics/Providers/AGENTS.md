# Maieutics.Providers instructions

Use `.agents/skills/maieutics-agent-runtime/SKILL.md` for provider boundaries and
`.agents/skills/maieutics-dotnet-testing/SKILL.md` for conformance and NativeAOT checks.

## Ownership

This folder contains executable-owned provider factories and `IChatClient` adapters. It currently supports OpenAI
Responses, OpenAI Chat Completions, and Anthropic Messages. The registry is provider-neutral so future providers can be
added without changing Agent contracts or the top-level source/profile schema.

## Boundary rules

- Translate `ChatMessage`, `AIContent`, tools, options, streamed updates, usage, and opaque continuation identifiers at
  this boundary.
- Provider SDK response and authentication types must not escape this folder.
- Factories bind and validate only their provider's source fields. Reject unknown or inapplicable fields.
- Declare capabilities explicitly; never infer identical behavior across providers.
- Map provider failures into safe runtime failures without exposing credentials, response bodies containing secrets, or
  sensitive request content.
- Honor cancellation through HTTP, response streaming, and tool-call continuation.

## Provider-specific constraints

- OpenAI Responses and Chat Completions are separate source flavors. Both send `store: false`; local transcript replay
  remains canonical.
- Anthropic uses the internal NativeAOT-safe Messages adapter. Keep its JSON writing and SSE parsing trimming-safe and
  accept EOF as an event boundary when a stream omits a final blank line.
- Custom endpoints and authentication headers come from the selected source generation, not ambient mutable state.
- Provider changes require deterministic fake-server conformance tests and a warning-free NativeAOT publish path.
- Do not add broad trimming or dynamic-code warning suppressions to accommodate a provider SDK.
