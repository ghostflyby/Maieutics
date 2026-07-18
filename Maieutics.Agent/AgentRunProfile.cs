using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Defines the immutable model client and runtime options captured by one Agent run.</summary>
public sealed record AgentRunProfile
{
    /// <summary>Initializes a run profile.</summary>
    /// <param name="chatClient">The model client used for every model invocation in the run.</param>
    /// <param name="options">The instructions and limits applied to the run.</param>
    public AgentRunProfile(IChatClient chatClient, AgentSessionOptions options)
    {
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
    }

    /// <summary>Gets the model client used for every model invocation in the run.</summary>
    public IChatClient ChatClient { get; }

    /// <summary>Gets the instructions and limits applied to the run.</summary>
    public AgentSessionOptions Options { get; }
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