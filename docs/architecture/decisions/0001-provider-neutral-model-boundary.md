# ADR 0001: Provider-Neutral Model Boundary

Status: Accepted

Date: 2026-07-16

Updated by: ADR 0010 removes Microsoft Agent Framework while preserving this provider boundary.

## Context

Maieutics must support OpenAI Responses, OpenAI Chat Completions, Anthropic Messages, and potentially multiple Google
model APIs. These APIs differ in request shape, streaming events, tool calls, reasoning metadata, continuation state,
usage reporting, and multimodal support.

The runtime injects `Microsoft.Extensions.AI.IChatClient`. Microsoft.Extensions.AI already provides provider-neutral
`ChatMessage`, `ChatRole`, and polymorphic `AIContent` contracts for messages, tools, reasoning, usage, opaque provider
state, additional properties, and service discovery. OpenAI Chat Completions and Responses both expose `IChatClient`
adapters, and other providers can implement the same contract.

Adding another model-client abstraction with nearly the same responsibilities would create a permanent translation
layer without establishing a meaningfully different ownership boundary.

## Decision

`IChatClient` is the primary model-provider boundary used by the Agent runtime. Provider APIs are adapted to this
interface directly:

```text
OpenAI Chat Completions ----\
OpenAI Responses -----------+--> IChatClient <-- RecordingChatClient <-- FunctionInvokingChatClient <-- AgentSession
Anthropic Messages ---------+
Google model APIs ----------/
```

Maieutics does not introduce an `IModelClient` compatibility layer unless a concrete provider requirement cannot be
represented through `IChatClient`, its extension points, or a narrowly scoped adjacent abstraction.

The following semantics remain required:

- `ChatMessage`, `ChatRole`, and `AIContent` are the message and content representation throughout the Agent facade and
  model integration layer. Maieutics does not maintain a parallel closed content hierarchy.
- Microsoft.Extensions.AI owns the message/content taxonomy and its JSON contracts. Maieutics owns session and run
  lifecycles, transcript transactions, events and correlation IDs, policy filtering, and the versioned transcript
  envelope.
- Provider SDK types do not become Agent contracts. `ChatMessage` and `AIContent` also do not become Jupyter, worker,
  extension, or notebook persistence wire contracts; those boundaries adapt them to their own versioned
  representations.
- A Maieutics capability descriptor declares tools, multimodal input, structured output, reasoning summaries,
  continuation behavior, and other optional features not safely inferred from the common interface alone.
- Each provider API has a dedicated adapter. OpenAI Responses and OpenAI Chat Completions are separate adapters even if
  they share an SDK or configuration.
- Provider-specific behavior may use supported `AIContent`, JSON-compatible `AdditionalProperties`,
  `RawRepresentation`, `GetService`, or a dedicated provider options object confined to the adapter. Unsupported custom
  content cannot enter the canonical transcript unless its JSON contract is explicitly supported.
- SDK response types, authentication types, raw JSON objects, and provider exception types stop at the adapter boundary.

## Conversation authority

The canonical transcript stored by the Agent session is authoritative. It uses a Maieutics-owned versioned envelope
whose messages and content are serialized with the Microsoft.Extensions.AI JSON contract. Provider-side identifiers
such as previous response or interaction IDs are opaque optional checkpoints associated with transcript state.

The runtime must remain able to reconstruct a provider request from the canonical transcript when a checkpoint is
missing, expired, incompatible with a selected provider, or intentionally discarded. Provider checkpoints may improve
latency or caching but must not be required for correctness.

The default operating mode reconstructs requests directly from Maieutics-owned committed history and disables
provider-side conversation storage where supported. A non-empty provider conversation ID is rejected. A future
provider-managed acceleration mode must still record the canonical transcript independently and must have explicit
replay and fallback semantics.

## Provider selection

Configuration separates provider connection sources from selectable model profiles:

```text
Maieutics:Sources:<SourceId>
    Provider
    provider-specific API flavor, endpoint, credentials, and options

Maieutics:Profiles:<ProfileId>
    Source
    Model
```

The executable resolves sources through an immutable factory registry and publishes an atomically validated profile
catalog. Provider-specific options remain inside the selected adapter. Active runs retain their profile generation
lease. The runtime uses capability negotiation and fails before a provider request when a required feature is absent.

## Current adapters

The executable-owned `Maieutics.Providers.OpenAI` namespace uses the `Microsoft.Extensions.AI.OpenAI` adapters for both
OpenAI API shapes. The configured `ApiFlavor` selects `Responses` or `ChatCompletions`; `Responses` is the default.
OpenAI SDK types remain inside the provider factory and do not cross the `IChatClient` boundary into Agent Core.

This adapter is not a separate project because it currently has one product consumer and no independent publication
target. It may be extracted later if another executable or library consumes it independently.

Both flavors explicitly send `store: false`. The current implementation does not use Responses
`previous_response_id` or Conversations. Every turn is reconstructed from the committed Maieutics transcript, and
provider response identifiers are not conversation authority. Prompt caching remains independent of this storage
choice.

The executable-owned `Maieutics.Providers.Anthropic` namespace implements the Messages API directly behind
`IChatClient`. It writes request JSON explicitly and parses streaming SSE without reflection so the adapter remains
compatible with the executable's NativeAOT requirement. Credentials and optional endpoint configuration remain inside
its source factory. Provider wire types do not cross the `IChatClient` boundary.

The OpenAI .NET Responses client and its `IChatClient` adapter are marked experimental by the current SDK. The required
`OPENAI001` acknowledgement is isolated to the OpenAI provider factory. Upgrading that SDK requires the provider
conformance tests and NativeAOT publish check to pass before the version is accepted.

## Consequences

- Agent Core can add or switch providers without translating messages through a second content hierarchy.
- Provider-specific features remain available without polluting common models.
- Cross-provider continuation uses the canonical transcript rather than opaque provider state.
- A new `IChatClient` implementation requires conformance tests for streaming content, cancellation, tool calls, usage,
  provider identifiers, and errors.
- `IChatClient` remains injectable and directly testable behind the internal function-invocation and recording
  decorators.
- Maieutics avoids maintaining a second provider abstraction that mirrors Microsoft.Extensions.AI.
- Changes to the Microsoft.Extensions.AI JSON contract or content taxonomy are explicit transcript compatibility inputs.

## References

- OpenAI Responses API: https://platform.openai.com/docs/api-reference/responses
- OpenAI Chat Completions API: https://platform.openai.com/docs/api-reference/chat
- Anthropic Messages API: https://platform.claude.com/docs/en/api/messages
- Google Interactions API: https://ai.google.dev/gemini-api/docs/interactions
- Google `generateContent`: https://ai.google.dev/api/generate-content
- Microsoft.Extensions.AI `IChatClient`: https://learn.microsoft.com/dotnet/api/microsoft.extensions.ai.ichatclient
