using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.DenoRepl;
using Maieutics.Jupyter;
using Maieutics.Jupyter.Shared;

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
    private int active = 1;

    internal bool IsActive => Volatile.Read(ref active) != 0;

    public ValueTask DisplayAsync(
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        return PublishAsync("repl.display", null, data, cancellationToken);
    }

    public ValueTask<JupyterDisplayId> DisplayTrackedAsync(
        MimeBundle data,
        JupyterDisplayId displayId,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        return PublishTrackedAsync("repl.display", displayId, data, cancellationToken);
    }

    public async ValueTask UpdateDisplayAsync(
        JupyterDisplayId displayId,
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        await PublishTrackedAsync("repl.updateDisplay", displayId, data, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ClearOutputAsync(bool wait, CancellationToken cancellationToken)
    {
        return PublishAsync("repl.clear", null, MimeBundle.Empty, cancellationToken);
    }

    public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken)
    {
        return PublishAsync(
            "repl.display",
            null,
            MimeBundle.FromText(text),
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
            new MimeBundle(new Dictionary<string, JsonElement>
            {
                ["text/plain"] = JsonSerializer.SerializeToElement(
                    $"{name}: {value}",
                    JupyterJsonContext.Default.String)
            }),
            cancellationToken);
    }

    public Task<string> RequestInputAsync(
        string prompt,
        bool password,
        CancellationToken cancellationToken)
    {
        throw new AgentToolException(
            "repl_input_unsupported",
            "The frontend presentation path does not support REPL input requests.");
    }

    internal async ValueTask DeactivateAsync()
    {
        if (Interlocked.Exchange(ref active, 0) == 0) return;

        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
    }

    private async ValueTask PublishAsync(
        string type,
        JupyterDisplayId? displayId,
        MimeBundle data,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActive) throw new OperationCanceledException("The frontend presentation sink is no longer active.");

            target.PublishPresentation(
                type,
                displayId?.Value,
                JsonSerializer.SerializeToElement(data.Data, FrontendJsonContext.Default
                    .IReadOnlyDictionaryStringJsonElement),
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<JupyterDisplayId> PublishTrackedAsync(
        string type,
        JupyterDisplayId displayId,
        MimeBundle data,
        CancellationToken cancellationToken)
    {
        await PublishAsync(type, displayId, data, cancellationToken).ConfigureAwait(false);
        return displayId;
    }
}

/// <summary>
///     Picks the presentation router of whichever frontend currently owns the session's run,
///     so the REPL tool resolves one sink during the Jupyter-to-frontend transition.
/// </summary>
internal sealed class CompositeDenoReplPresentationRouter(
    JupyterDenoReplPresentationRouter jupyter,
    FrontendDenoReplPresentationRouter frontend) : IDenoReplPresentationRouter
{
    public async ValueTask<IDenoReplPresentationSink> WaitForCallAsync(
        AgentSessionId sessionId,
        AgentToolCallId callId,
        CancellationToken cancellationToken)
    {
        if (jupyter.IsAttached(sessionId))
            return await jupyter.WaitForCallAsync(sessionId, callId, cancellationToken).ConfigureAwait(false);

        return await frontend.WaitForCallAsync(sessionId, callId, cancellationToken).ConfigureAwait(false);
    }

    public bool TryGetCurrentSink(
        AgentSessionId sessionId,
        [NotNullWhen(true)] out IDenoReplPresentationSink? sink)
    {
        if (jupyter.TryGetCurrentSink(sessionId, out sink)) return true;

        return frontend.TryGetCurrentSink(sessionId, out sink);
    }
}
