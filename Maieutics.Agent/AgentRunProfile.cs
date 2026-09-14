using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Maieutics.Agent;

/// <summary>Defines the immutable model client and runtime options captured by one Agent run.</summary>
public sealed record AgentRunProfile
{
    private const AgentModelCapabilities DefaultCapabilities =
        AgentModelCapabilities.StreamingText | AgentModelCapabilities.FunctionCalling;

    private static readonly AgentModelCapabilities KnownCapabilities =
        Enum.GetValues<AgentModelCapabilities>()
            .Aggregate(
                AgentModelCapabilities.None,
                static (combined, value) => combined | value);

    /// <summary>Initializes a run profile with provider-neutral model metadata.</summary>
    /// <param name="chatClient">The model client used for every model invocation in the run.</param>
    /// <param name="options">The instructions and limits applied to the run.</param>
    /// <param name="modelIdentity">The configured provider and model identity, when known.</param>
    /// <param name="capabilities">The model behaviors available to the run.</param>
    /// <param name="hostedCapabilities">
    ///     The provider-neutral capability names hosted by the model endpoint, when known.
    /// </param>
    /// <param name="tools">The immutable tools available for the complete run.</param>
    /// <param name="hostedTools">
    ///     The provider-hosted tools attached to each request. These are declared to the model and
    ///     executed by the provider, so the runtime never invokes them and they carry no local
    ///     function schema.
    /// </param>
    public AgentRunProfile(
        IChatClient chatClient,
        AgentSessionOptions options,
        AgentModelIdentity? modelIdentity = null,
        AgentModelCapabilities capabilities = DefaultCapabilities,
        IEnumerable<string>? hostedCapabilities = null,
        IEnumerable<AIFunction>? tools = null,
        IEnumerable<AITool>? hostedTools = null)
    {
        if ((capabilities & ~KnownCapabilities) != 0)
            throw new ArgumentOutOfRangeException(nameof(capabilities), capabilities,
                "The Agent run profile contains unknown model capabilities.");

        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();
        ModelIdentity = modelIdentity;
        Capabilities = capabilities;
        HostedCapabilities = NormalizeHostedCapabilities(hostedCapabilities);
        Tools = tools?.ToImmutableArray() ?? [];
        HostedTools = NormalizeHostedTools(hostedTools, Tools);
    }

    private static IReadOnlyList<string> NormalizeHostedCapabilities(IEnumerable<string>? hostedCapabilities)
    {
        if (hostedCapabilities is null) return [];

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in hostedCapabilities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            names.Add(name);
        }

        return [.. names];
    }

    /// <summary>Hosted tools are declared to the model and executed by the provider, so they are
    /// kept apart from the local function registry: they carry no JSON schema and no call ever
    /// reaches the runtime. A name colliding with a local function would make the provider's
    /// choice ambiguous, so that is rejected here.</summary>
    private static IReadOnlyList<AITool> NormalizeHostedTools(
        IEnumerable<AITool>? hostedTools,
        IReadOnlyList<AIFunction> functions)
    {
        if (hostedTools is null) return [];

        var functionNames = new HashSet<string>(
            functions.Select(static function => function.Name),
            StringComparer.Ordinal);
        var hosted = new List<AITool>();
        foreach (var tool in hostedTools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (tool is AIFunction)
                throw new ArgumentException(
                    "Hosted tools must not be local functions; register local functions through the tools list.",
                    nameof(hostedTools));
            if (string.IsNullOrWhiteSpace(tool.Name))
                throw new ArgumentException("A hosted tool must have a name.", nameof(hostedTools));
            if (functionNames.Contains(tool.Name) ||
                hosted.Any(existing => string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"A tool named '{tool.Name}' is already registered.",
                    nameof(hostedTools));

            hosted.Add(tool);
        }

        return [.. hosted];
    }

    /// <summary>Gets the model client used for every model invocation in the run.</summary>
    public IChatClient ChatClient { get; }

    /// <summary>Gets the instructions and limits applied to the run.</summary>
    public AgentSessionOptions Options { get; }

    /// <summary>Gets the configured provider and model identity, when known.</summary>
    public AgentModelIdentity? ModelIdentity { get; }

    /// <summary>Gets the model behaviors available to the run.</summary>
    public AgentModelCapabilities Capabilities { get; }

    /// <summary>Gets the provider-neutral capability names hosted by the model endpoint.</summary>
    public IReadOnlyList<string> HostedCapabilities { get; }

    /// <summary>Gets the immutable tools available for the complete run.</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>Gets the provider-hosted tools declared on every request of the run.</summary>
    public IReadOnlyList<AITool> HostedTools { get; }
}

/// <summary>Provides an immutable profile for each newly started Agent run.</summary>
public interface IAgentRunProfileProvider
{
    /// <summary>Asynchronously acquires a profile lease owned by the new run.</summary>
    /// <param name="cancellationToken">Cancels waiting for the profile lease.</param>
    /// <returns>A lease whose profile remains valid until the run terminates.</returns>
    Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>Keeps an Agent run profile and its resources alive for one run.</summary>
public interface IAgentRunProfileLease : IAsyncDisposable
{
    /// <summary>Gets the immutable profile captured by the run.</summary>
    AgentRunProfile Profile { get; }
}
