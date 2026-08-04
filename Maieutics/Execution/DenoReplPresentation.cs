using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Execution;

internal interface IDenoReplPresentationSink
{
    ValueTask DisplayAsync(
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken);

    ValueTask<JupyterDisplayId> DisplayTrackedAsync(
        MimeBundle data,
        JupyterDisplayId displayId,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken);

    ValueTask UpdateDisplayAsync(
        JupyterDisplayId displayId,
        MimeBundle data,
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
}

internal interface IDenoReplPresentationRouter
{
    ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
        AgentSessionId sessionId,
        AgentToolCallId callId,
        CancellationToken cancellationToken);

    bool TryGetCurrentSink(AgentSessionId sessionId, [NotNullWhen(true)] out IDenoReplPresentationSink? sink);
}