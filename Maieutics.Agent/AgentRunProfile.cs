using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Defines the immutable model client and runtime options captured by one Agent run.</summary>
public sealed record AgentRunProfile
{
    private const AgentModelCapabilities CompatibilityCapabilities =
        AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

    /// <summary>Initializes a run profile with provider-neutral model metadata.</summary>
    /// <param name="chatClient">The model client used for every model invocation in the run.</param>
    /// <param name="options">The instructions and limits applied to the run.</param>
    /// <param name="modelIdentity">The configured provider and model identity, when known.</param>
    /// <param name="capabilities">The model behaviors available to the run.</param>
    /// <param name="tools">The immutable tools available for the complete run.</param>
    public AgentRunProfile(
        IChatClient chatClient,
        AgentSessionOptions options,
        AgentModelIdentity? modelIdentity = null,
        AgentModelCapabilities capabilities = CompatibilityCapabilities,
        IEnumerable<AIFunction>? tools = null)
    {
        if ((capabilities & ~CompatibilityCapabilities) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities,
                "The Agent run profile contains unknown model capabilities.");

        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        ModelIdentity = modelIdentity;
        Capabilities = capabilities;
        Tools = tools?.ToImmutableArray() ?? [];
    }

    /// <summary>Gets the model client used for every model invocation in the run.</summary>
    public IChatClient ChatClient { get; }

    /// <summary>Gets the instructions and limits applied to the run.</summary>
    public AgentSessionOptions Options { get; }

    /// <summary>Gets the configured provider and model identity, when known.</summary>
    public AgentModelIdentity? ModelIdentity { get; }

    /// <summary>Gets the model behaviors available to the run.</summary>
    public AgentModelCapabilities Capabilities { get; }

    /// <summary>Gets the immutable tools available for the complete run.</summary>
    public IReadOnlyList<AIFunction> Tools { get; }
}

/// <summary>Provides an immutable profile for each newly started Agent run.</summary>
public interface IAgentRunProfileProvider
{
    /// <summary>Acquires a profile lease owned by the new run.</summary>
    /// <returns>A lease whose profile remains valid until the run terminates.</returns>
    IAgentRunProfileLease Acquire();
}

/// <summary>Keeps an Agent run profile and its resources alive for one run.</summary>
public interface IAgentRunProfileLease : IAsyncDisposable
{
    /// <summary>Gets the immutable profile captured by the run.</summary>
    AgentRunProfile Profile { get; }
}