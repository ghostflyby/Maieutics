using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Maieutics.Agent;

namespace Maieutics.DenoRepl;

internal interface IDenoReplPresentationSink
{
    ValueTask DisplayAsync(
        ReplDisplayBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken);

    ValueTask<ReplDisplayId> DisplayTrackedAsync(
        ReplDisplayBundle data,
        ReplDisplayId displayId,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken);

    ValueTask UpdateDisplayAsync(
        ReplDisplayId displayId,
        ReplDisplayBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken);

    ValueTask ClearOutputAsync(bool wait, CancellationToken cancellationToken);

    ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken);

    ValueTask PublishErrorAsync(
        string name,
        string value,
        IReadOnlyList<string> traceback,
        CancellationToken cancellationToken);

    Task<string> RequestInputAsync(
        string prompt,
        bool password,
        CancellationToken cancellationToken);

    /// <summary>Delivers the answer for a pending input request. Returns false when no
    /// request with that id is outstanding (already answered or no longer waiting).</summary>
    bool TryCompleteInput(string requestId, string value);
}

internal interface IDenoReplPresentationRouter
{
    ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
        AgentSessionId sessionId,
        AgentToolCallId callId,
        CancellationToken cancellationToken);

    bool TryGetCurrentSink(AgentSessionId sessionId, [NotNullWhen(true)] out IDenoReplPresentationSink? sink);
}
