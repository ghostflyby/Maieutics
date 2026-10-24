using System.Runtime.CompilerServices;
using Maieutics.Agent;
using Microsoft.Extensions.AI;

namespace Maieutics.Testing;

/// <summary>One scripted <see cref="IChatClient" /> for the suite: streams each queued
/// response factory in order, capturing every request (cloned) and its instructions. A
/// non-streaming call is not supported — the suite drives streaming turns.</summary>
public sealed class ScriptedChatClient(
    params Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] responses)
    : IChatClient
{
    private readonly Lock gate = new();
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, CancellationToken,
        IAsyncEnumerable<ChatResponseUpdate>>> responses = new(responses);

    /// <summary>The requests streamed so far, cloned at the call boundary.</summary>
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    /// <summary>The instructions of each streamed request, null when unset.</summary>
    public List<string?> Instructions { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<ChatResponse>(new NotSupportedException());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = messages.Select(message => message.Clone()).ToArray();
        Func<IReadOnlyList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> response;
        lock (gate)
        {
            Requests.Add(request);
            Instructions.Add(options?.Instructions);
            response = responses.Dequeue();
        }

        return response(request, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
    }
}

/// <summary>A chat client whose streaming turns park until cancelled: for scenarios that
/// need a run forever in flight, deterministically ended by the caller's token.</summary>
public sealed class ParkedChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<ChatResponse>(new NotSupportedException());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ParkedAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ParkedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
    }
}

/// <summary>A chat client nothing ever drives: for wiring a profile without a model. A
/// driven call fails loudly instead of silently hanging.</summary>
public sealed class StubChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<ChatResponse>(
            new InvalidOperationException("The stub chat client is not driven."));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("The stub chat client is not driven.");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
    }
}

/// <summary>A run-profile provider returning one fixed profile: either a prebuilt
/// profile, or a chat client wrapped in a default profile (a
/// <see cref="StubChatClient" /> when omitted, for wiring-only scenarios).</summary>
public sealed class FixedProfileProvider : IAgentRunProfileProvider
{
    private readonly AgentRunProfile profile;

    /// <summary>Returns the given profile on every acquire.</summary>
    public FixedProfileProvider(AgentRunProfile profile)
    {
        this.profile = profile;
    }

    /// <summary>Wraps the given client (or a stub) in a default profile.</summary>
    public FixedProfileProvider(IChatClient? chatClient = null)
    {
        profile = new AgentRunProfile(chatClient ?? new StubChatClient(), new AgentSessionOptions());
    }

    public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IAgentRunProfileLease>(new Lease(profile));
    }

    private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
    {
        public AgentRunProfile Profile { get; } = profile;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
