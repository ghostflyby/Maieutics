# ADR 0001: Provider-Neutral Model Boundary

Status: Accepted

Date: 2026-07-16

## Context

Maieutics must support OpenAI Responses, OpenAI Chat Completions, Anthropic Messages, and potentially multiple Google
model APIs. These APIs differ in request shape, streaming events, tool calls, reasoning metadata, continuation state,
usage reporting, and multimodal support.

The current runtime injects `Microsoft.Extensions.AI.IChatClient` and models public messages and events primarily as
text. `IChatClient` already provides provider-neutral messages, polymorphic content, tools, streaming responses, usage,
opaque provider state, additional properties, and service discovery. OpenAI Chat Completions and Responses both expose
`IChatClient` adapters, and other providers can implement the same contract.

Adding another model-client abstraction with nearly the same responsibilities would create a permanent translation
layer without establishing a meaningfully different ownership boundary.

## Decision

`IChatClient` is the primary model-provider boundary used by Agent Core and Microsoft Agent Framework. Provider APIs are
adapted to this interface directly:

```text
OpenAI Chat Completions ----\
OpenAI Responses -----------+--> IChatClient --> ChatClientAgent
Anthropic Messages ---------+
Google model APIs ----------/
```

Maieutics does not introduce an `IModelClient` compatibility layer unless a concrete provider requirement cannot be
represented through `IChatClient`, its extension points, or a narrowly scoped adjacent abstraction.

The following semantics remain required:

- `ChatMessage` and `AIContent` are accepted as the provider-facing message and content representation inside the model
  integration layer.
- Provider-neutral Agent events and transcript types remain Maieutics-owned API and are mapped from framework response
  updates; Microsoft types do not become the Jupyter, worker, extension, or persistence wire contract.
- A Maieutics capability descriptor declares tools, multimodal input, structured output, reasoning summaries,
  continuation behavior, and other optional features not safely inferred from the common interface alone.
- Each provider API has a dedicated adapter. OpenAI Responses and OpenAI Chat Completions are separate adapters even if
  they share an SDK or configuration.
- Provider-specific behavior may use custom `AIContent`, `AdditionalProperties`, `RawRepresentation`, `GetService`, or a
  dedicated provider options object confined to the adapter.
- SDK response types, authentication types, raw JSON objects, and provider exception types stop at the adapter boundary.

## Conversation authority

The canonical transcript stored by the Agent session is authoritative. Provider-side identifiers such as previous
response or interaction IDs are opaque optional checkpoints associated with transcript state.

The runtime must remain able to reconstruct a provider request from the canonical transcript when a checkpoint is
missing, expired, incompatible with a selected provider, or intentionally discarded. Provider checkpoints may improve
latency or caching but must not be required for correctness.

The default operating mode uses Maieutics-owned local history supplied through a framework history provider and disables
provider-side conversation storage when an adapter would otherwise make local history and a provider conversation ID
mutually exclusive. A future provider-managed acceleration mode must still record the canonical transcript independently
and must have explicit replay and fallback semantics.

## Provider selection

Configuration selects a provider-neutral model definition rather than directly selecting an SDK client:

```text
Maieutics:Model
    Provider
    Name

Maieutics:Providers:<ProviderName>
    provider-specific API flavor, endpoint, credentials, and options
```

The executable resolves the selected provider through an immutable factory registry. Provider-specific options remain
inside the selected adapter. A configuration reload constructs a replacement client before publishing the new model
profile; active runs retain their existing client lease. The runtime uses capability negotiation and fails early when a
requested feature is unsupported.

## Current OpenAI adapter

The executable-owned `Maieutics.Providers.OpenAI` namespace uses the `Microsoft.Extensions.AI.OpenAI` adapters for both
OpenAI API shapes. The configured `ApiFlavor` selects `Responses` or `ChatCompletions`; `Responses` is the default.
OpenAI SDK types remain inside the provider factory and do not cross the `IChatClient` boundary into Agent Core.

This adapter is not a separate project because it currently has one product consumer and no independent publication
target. It may be extracted later if another executable or library consumes it independently.

Both flavors explicitly send `store: false`. The current implementation does not use Responses
`previous_response_id` or Conversations. Every turn is reconstructed from the committed Maieutics transcript, and
provider response identifiers are not conversation authority. Prompt caching remains independent of this storage
choice.

The OpenAI .NET Responses client and its `IChatClient` adapter are marked experimental by the current SDK. The required
`OPENAI001` acknowledgement is isolated to the OpenAI provider factory. Upgrading that SDK requires the provider
conformance tests and NativeAOT publish check to pass before the version is accepted.

## Consequences

- Agent Core can add or switch providers without changing transcript, tool, or notebook contracts.
- Provider-specific features remain available without polluting common models.
- Cross-provider continuation uses the canonical transcript rather than opaque provider state.
- A new `IChatClient` implementation requires conformance tests for streaming content, cancellation, tool calls, usage,
  provider identifiers, and errors.
- `IChatClient` remains injectable and directly testable even when `ChatClientAgent` performs the internal orchestration.
- Maieutics avoids maintaining a second provider abstraction that mirrors Microsoft.Extensions.AI.

## References

- OpenAI Responses API: https://platform.openai.com/docs/api-reference/responses
- OpenAI Chat Completions API: https://platform.openai.com/docs/api-reference/chat
- Anthropic Messages API: https://platform.claude.com/docs/en/api/messages
- Google Interactions API: https://ai.google.dev/gemini-api/docs/interactions
- Google `generateContent`: https://ai.google.dev/api/generate-content
- Microsoft.Extensions.AI `IChatClient`: https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient
