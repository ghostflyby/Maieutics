using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.DenoRepl;

namespace Maieutics.Frontend;

/// <summary>
///     Routes Deno REPL rich output into a frontend run's event stream while that run is
///     live. Mirrors <c>JupyterDenoReplPresentationRouter</c> so the REPL tool finds exactly
///     one active sink per session regardless of which frontend owns the run; REPL display
///     frames use the same display-id tracking the Jupyter path renders onto iopub.
/// </summary>
internal sealed class FrontendDenoReplPresentationRouter : IDenoReplPresentationRouter
{
    private readonly Lock gate = new();
    private readonly Dictionary<AgentSessionId, RunState> runs = [];

    public async ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
        AgentSessionId sessionId,
        AgentToolCallId callId,
        CancellationToken cancellationToken)
    {
        Task<IDenoReplPresentationSink> task;
        lock (gate)
        {
            if (!runs.TryGetValue(sessionId, out var state)) throw PresentationUnavailable();

            if (state.OpenedCalls.Contains(callId)) return state.Sink;

            if (!state.Waiters.TryGetValue(callId, out var waiter))
            {
                waiter = new TaskCompletionSource<IDenoReplPresentationSink>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                state.Waiters.Add(callId, waiter);
            }

            task = waiter.Task;
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetCurrentSink(
        AgentSessionId sessionId,
        [NotNullWhen(true)] out IDenoReplPresentationSink? sink)
    {
        lock (gate)
        {
            if (runs.TryGetValue(sessionId, out var state) && state.Sink.IsActive)
            {
                sink = state.Sink;
                return true;
            }
        }

        sink = null;
        return false;
    }

    internal bool IsAttached(AgentSessionId sessionId)
    {
        lock (gate)
        {
            return runs.ContainsKey(sessionId);
        }
    }

    internal FrontendPresentationScope Attach(AgentSessionId sessionId, IFrontendPresentationTarget target)
    {
        var state = new RunState(new FrontendDenoReplPresentationSink(target));
        lock (gate)
        {
            if (!runs.TryAdd(sessionId, state))
                throw new InvalidOperationException(
                    "A frontend presentation sink is already attached to this Agent session.");
        }

        return new FrontendPresentationScope(this, sessionId, state);
    }

    internal void OpenCall(AgentSessionId sessionId, AgentToolCallId callId)
    {
        TaskCompletionSource<IDenoReplPresentationSink>? waiter;
        IDenoReplPresentationSink? sink;
        lock (gate)
        {
            if (!runs.TryGetValue(sessionId, out var state)) return;

            state.OpenedCalls.Add(callId);
            state.Waiters.Remove(callId, out waiter);
            sink = state.Sink;
        }

        waiter?.TrySetResult(sink);
    }

    private async ValueTask DetachAsync(AgentSessionId sessionId, RunState expected)
    {
        TaskCompletionSource<IDenoReplPresentationSink>[] waiters;
        lock (gate)
        {
            if (!runs.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, expected)) return;

            runs.Remove(sessionId);
            waiters = current.Waiters.Values.ToArray();
            current.Waiters.Clear();
            current.OpenedCalls.Clear();
        }

        foreach (var waiter in waiters) waiter.TrySetException(PresentationUnavailable());

        await expected.Sink.DeactivateAsync().ConfigureAwait(false);
    }

    private static AgentToolException PresentationUnavailable()
    {
        return new AgentToolException(
            "repl_presentation_unavailable",
            "The active Agent run does not have a frontend presentation sink.");
    }

    internal sealed class RunState(FrontendDenoReplPresentationSink sink)
    {
        internal FrontendDenoReplPresentationSink Sink { get; } = sink;

        internal HashSet<AgentToolCallId> OpenedCalls { get; } = [];

        internal Dictionary<AgentToolCallId, TaskCompletionSource<IDenoReplPresentationSink>> Waiters { get; } = [];
    }

    internal sealed class FrontendPresentationScope(
        FrontendDenoReplPresentationRouter owner,
        AgentSessionId sessionId,
        RunState state) : IAsyncDisposable
    {
        private int disposeState;

        internal IDenoReplPresentationSink Sink => state.Sink;

        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref disposeState, 1) == 0
                ? owner.DetachAsync(sessionId, state)
                : ValueTask.CompletedTask;
        }
    }
}

/// <summary>A target that accepts REPL presentation frames.</summary>
internal interface IFrontendPresentationTarget
{
    void PublishPresentation(string type, string? displayId, JsonElement data, CancellationToken cancellationToken);
}

/// <summary>Writes REPL presentation calls into the owning run's event stream.</summary>
internal sealed class FrontendDenoReplPresentationSink(IFrontendPresentationTarget target) : IDenoReplPresentationSink
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<string>> pendingInputs =
        new(StringComparer.Ordinal);
    private int inputSequence;
    private int active = 1;

    internal bool IsActive => Volatile.Read(ref active) != 0;

    public ValueTask DisplayAsync(
        ReplDisplayBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        return PublishAsync("repl.display", null, data, cancellationToken);
    }

    public ValueTask<ReplDisplayId> DisplayTrackedAsync(
        ReplDisplayBundle data,
        ReplDisplayId displayId,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        return PublishTrackedAsync("repl.display", displayId, data, cancellationToken);
    }

    public async ValueTask UpdateDisplayAsync(
        ReplDisplayId displayId,
        ReplDisplayBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        await PublishTrackedAsync("repl.updateDisplay", displayId, data, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ClearOutputAsync(bool wait, CancellationToken cancellationToken)
    {
        return PublishAsync("repl.clear", null, ReplDisplayBundle.Empty, cancellationToken);
    }

    public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken)
    {
        return PublishAsync(
            "repl.display",
            null,
            ReplDisplayBundle.FromText(text),
            cancellationToken);
    }

    public ValueTask PublishErrorAsync(
        string name,
        string value,
        IReadOnlyList<string> traceback,
        CancellationToken cancellationToken)
    {
        return PublishAsync(
            "repl.error",
            null,
            new ReplDisplayBundle(new Dictionary<string, JsonElement>
            {
                ["text/plain"] = JsonSerializer.SerializeToElement(
                    $"{name}: {value}",
                    ReplJsonContext.Default.String)
            }),
            cancellationToken);
    }

    /// <summary>Publishes an <c>input.request</c> frame and waits for the frontend's answer
    /// (delivered through <see cref="TryCompleteInput" />). The wait honours the caller's
    /// cancellation; deactivating the sink fails every outstanding request.</summary>
    public async Task<string> RequestInputAsync(
        string prompt,
        bool password,
        CancellationToken cancellationToken)
    {
        string requestId;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!IsActive) throw CreateInactive();
            requestId = $"input-{++inputSequence}";
            pendingInputs[requestId] = completion;
        }

        target.PublishPresentation(
            "input.request",
            null,
            JsonSerializer.SerializeToElement(
                new FrontendInputRequest(requestId, prompt, password),
                FrontendJsonContext.Default.FrontendInputRequest),
            cancellationToken);

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                pendingInputs.Remove(requestId);
            }
        }
    }

    /// <inheritdoc />
    public bool TryCompleteInput(string requestId, string value)
    {
        TaskCompletionSource<string>? completion;
        lock (gate)
        {
            pendingInputs.TryGetValue(requestId, out completion);
            pendingInputs.Remove(requestId);
        }

        return completion is not null && completion.TrySetResult(value);
    }

    private static OperationCanceledException CreateInactive()
    {
        return new OperationCanceledException("The frontend presentation sink is no longer active.");
    }

    internal async ValueTask DeactivateAsync()
    {
        if (Interlocked.Exchange(ref active, 0) == 0) return;

        List<TaskCompletionSource<string>> pending;
        lock (gate)
        {
            pending = pendingInputs.Values.ToList();
            pendingInputs.Clear();
        }

        var cancelled = new OperationCanceledException("The frontend presentation sink is no longer active.");
        foreach (var completion in pending) completion.TrySetException(cancelled);

        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
    }

    private async ValueTask PublishAsync(
        string type,
        ReplDisplayId? displayId,
        ReplDisplayBundle data,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActive) throw new OperationCanceledException("The frontend presentation sink is no longer active.");

            target.PublishPresentation(
                type,
                displayId?.Value,
                JsonSerializer.SerializeToElement(
                    StripBinaryMimes(data.Data),
                    FrontendJsonContext.Default.IReadOnlyDictionaryStringJsonElement),
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<ReplDisplayId> PublishTrackedAsync(
        string type,
        ReplDisplayId displayId,
        ReplDisplayBundle data,
        CancellationToken cancellationToken)
    {
        await PublishAsync(type, displayId, data, cancellationToken).ConfigureAwait(false);
        return displayId;
    }

    /// <summary>Replaces inline binary mime payloads (base64 strings for small displays)
    /// with textual placeholders; object references ({"$object": sha}) pass through as
    /// structured JSON. Either way the frontend wire never carries binary as base64 text
    /// (invariant 26).</summary>
    private static IReadOnlyDictionary<string, JsonElement> StripBinaryMimes(
        IReadOnlyDictionary<string, JsonElement> data)
    {
        var hasBinary = data.Any(static entry => IsBinaryMime(entry.Key));
        if (!hasBinary) return data;

        var filtered = new Dictionary<string, JsonElement>(data.Count, StringComparer.Ordinal);
        foreach (var (mime, value) in data)
        {
            if (IsBinaryMime(mime) &&
                value.ValueKind == JsonValueKind.String)
            {
                // Inline base64 (small payloads): placeholder.
                filtered[mime] = JsonSerializer.SerializeToElement(
                    $"[binary {mime} display omitted]",
                    ReplJsonContext.Default.String);
                continue;
            }

            // Object references and non-binary mimes pass through verbatim.
            filtered[mime] = value.Clone();
        }

        return filtered;
    }

    private static bool IsBinaryMime(string mime)
    {
        return mime.StartsWith("image/", StringComparison.Ordinal) ||
               mime.StartsWith("video/", StringComparison.Ordinal) ||
               mime.StartsWith("audio/", StringComparison.Ordinal) ||
               string.Equals(mime, "application/pdf", StringComparison.Ordinal);
    }
}
