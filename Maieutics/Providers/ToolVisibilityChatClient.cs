using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maieutics.Providers;

/// <summary>Hides provider-specific tools from the wire without removing them
/// from the runtime tool registry: the OpenAI Responses flavor hides the
/// general edit functions in favor of the built-in apply_patch tool, while
/// every other flavor hides the apply_patch function, which only has meaning
/// on the Responses wire. Invocation is unaffected — the function orchestrator
/// resolves tools above this decorator.</summary>
internal sealed class ToolVisibilityChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly IReadOnlySet<string> _hiddenToolNames;

    public ToolVisibilityChatClient(IChatClient inner, IReadOnlySet<string> hiddenToolNames)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hiddenToolNames = hiddenToolNames ?? throw new ArgumentNullException(nameof(hiddenToolNames));
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await _inner.GetResponseAsync(messages, HideTools(options), cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _inner
                           .GetStreamingResponseAsync(messages, HideTools(options), cancellationToken)
                           .ConfigureAwait(false))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return _inner.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }

    private ChatOptions? HideTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools) return options;

        var filtered = tools.Where(tool => !_hiddenToolNames.Contains(tool.Name)).ToList();
        if (filtered.Count == tools.Count) return options;

        var clone = options.Clone();
        clone.Tools = filtered;
        return clone;
    }
}
