# ADR 0001: Provider-Neutral Model Boundary

Status: Accepted

Date: 2026-07-16

## Context

Maieutics must support OpenAI Responses, OpenAI Chat Completions, Anthropic Messages, and potentially multiple Google
model APIs. These APIs differ in request shape, streaming events, tool calls, reasoning metadata, continuation state,
usage reporting, and multimodal support.

The current first-stage runtime injects `Microsoft.Extensions.AI.IChatClient` and models messages and events primarily as
text. That is sufficient for a streaming text prototype, but it must not become the permanent Agent Core boundary.

## Decision

Agent Core owns a provider-neutral model protocol. Conceptually:

```csharp
public interface IModelClient
{
    ModelCapabilities Capabilities { get; }

    IAsyncEnumerable<ModelEvent> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken);
}
```

The exact names may change during API design, but the following semantics are required:

- `ModelRequest` contains provider-neutral messages, content parts, available tools, output constraints, and request
  metadata.
- `ModelEvent` is a discriminated event model capable of representing text, structured content, tool-call arguments,
  usage, provider-supported reasoning summaries, completion, and typed failure.
- `ModelCapabilities` declares tools, multimodal input, structured output, reasoning summaries, continuation IDs, and
  other optional behavior.
- Each provider API has a dedicated adapter. OpenAI Responses and OpenAI Chat Completions are separate adapters even if
  they share an SDK or configuration.
- `IChatClient` may be wrapped by a compatibility adapter, but provider-specific advanced behavior must not be forced
  through its least-common-denominator surface.
- SDK response types, authentication types, raw JSON objects, and provider exception types stop at the adapter boundary.

## Conversation authority

The canonical transcript stored by the Agent session is authoritative. Provider-side identifiers such as previous
response or interaction IDs are opaque optional checkpoints associated with transcript state.

The runtime must remain able to reconstruct a provider request from the canonical transcript when a checkpoint is
missing, expired, incompatible with a selected provider, or intentionally discarded. Provider checkpoints may improve
latency or caching but must not be required for correctness.

## Provider selection

Configuration selects a model profile rather than directly selecting an SDK client:

```text
ModelProfile
    Provider
    ApiFlavor
    Model
    Endpoint
    CredentialReference
    ProviderOptions
```

Provider-specific options remain inside the selected adapter. The runtime uses capability negotiation and fails early
when a requested feature is unsupported.

## Consequences

- Agent Core can add or switch providers without changing transcript, tool, or notebook contracts.
- Provider-specific features remain available without polluting common models.
- Cross-provider continuation uses the canonical transcript rather than opaque provider state.
- A new adapter requires conformance tests for normalized streaming events, cancellation, tool calls, usage, and errors.
- The current `AgentSession(IChatClient, ...)` constructor is transitional and should be replaced before tool calling or
  multimodal input becomes public API.

## References

- OpenAI Responses API: https://platform.openai.com/docs/api-reference/responses
- OpenAI Chat Completions API: https://platform.openai.com/docs/api-reference/chat
- Anthropic Messages API: https://platform.claude.com/docs/en/api/messages
- Google Interactions API: https://ai.google.dev/gemini-api/docs/interactions
- Google `generateContent`: https://ai.google.dev/api/generate-content

