using System.Text.Json;

namespace Maieutics.Frontend;

/// <summary>The terminal outcome of one IDE request: the frontend's value, or its typed
/// error. Exactly one side is populated; cancellation and timeout never land here (they
/// surface as exceptions in the awaiting tool call).</summary>
internal sealed record IdeRequestAnswer(JsonElement? Value, string? ErrorCode, string? ErrorMessage)
{
    public static IdeRequestAnswer FromValue(JsonElement value) => new(value, null, null);

    public static IdeRequestAnswer FromError(string code, string message) => new(null, code, message);
}

/// <summary>
///     Correlates pending IDE requests (ADR 0042 decision 1) between the tools that
///     announce them on the run's event stream and the answer endpoint
///     (<c>POST /v1/agent/ide/{requestId}</c>). Entries are owned by the initiating tool
///     call: the awaiting tool removes its entry on completion, timeout, or cancellation,
///     so a late or duplicate answer is always a typed 404 and a cancelled run never
///     leaves a resumable request behind. Re-answering a completed request returns false.
/// </summary>
internal sealed class IdeRequestRegistry
{
    private readonly Lock gate = new();

    private readonly Dictionary<string, TaskCompletionSource<IdeRequestAnswer>> pending =
        new(StringComparer.Ordinal);

    /// <summary>Registers a new pending request and returns its identifier with the
    /// completion the caller awaits.</summary>
    public IdeRequestRegistration Begin()
    {
        var requestId = $"ide-{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<IdeRequestAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pending.Add(requestId, completion);
        }

        return new IdeRequestRegistration(requestId, completion.Task);
    }

    /// <summary>Completes a pending request with the frontend's answer; false when the
    /// request is unknown, already answered, or already abandoned (all render as 404).</summary>
    public bool TryComplete(string requestId, IdeRequestAnswer answer)
    {
        TaskCompletionSource<IdeRequestAnswer>? completion;
        lock (gate)
        {
            if (!pending.Remove(requestId, out completion)) return false;
        }

        return completion.TrySetResult(answer);
    }

    /// <summary>Drops a pending request without an answer (tool call ended another way);
    /// a later answer still 404s.</summary>
    public void Abandon(string requestId)
    {
        lock (gate)
        {
            pending.Remove(requestId);
        }
    }
}

/// <summary>One pending request's identity and its answer task.</summary>
internal sealed record IdeRequestRegistration(string RequestId, Task<IdeRequestAnswer> Completion);
