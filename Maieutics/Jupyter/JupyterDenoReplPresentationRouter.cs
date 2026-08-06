using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Jupyter.Kernel;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter;

internal sealed class JupyterDenoReplPresentationRouter : IDenoReplPresentationRouter
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

    internal JupyterDenoReplPresentationScope Attach(
        AgentSessionId sessionId,
        JupyterExecutionContext context)
    {
        var state = new RunState(new JupyterDenoReplPresentationSink(context));
        lock (gate)
        {
            if (!runs.TryAdd(sessionId, state))
                throw new InvalidOperationException(
                    "A Notebook presentation sink is already attached to this Agent session.");
        }

        return new JupyterDenoReplPresentationScope(this, sessionId, state);
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
            "The active Agent run does not have a Notebook presentation sink.");
    }

    internal sealed class RunState(JupyterDenoReplPresentationSink sink)
    {
        internal JupyterDenoReplPresentationSink Sink { get; } = sink;

        internal HashSet<AgentToolCallId> OpenedCalls { get; } = [];

        internal Dictionary<AgentToolCallId, TaskCompletionSource<IDenoReplPresentationSink>> Waiters { get; } = [];
    }

    internal sealed class JupyterDenoReplPresentationScope(
        JupyterDenoReplPresentationRouter owner,
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

internal sealed class JupyterDenoReplPresentationSink(JupyterExecutionContext context) : IDenoReplPresentationSink
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int active = 1;

    internal bool IsActive => Volatile.Read(ref active) != 0;

    public ValueTask DisplayAsync(
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        return SerializeAsync(token => context.DisplayAsync(data, metadata, token), cancellationToken);
    }

    public ValueTask<JupyterDisplayId> DisplayTrackedAsync(
        MimeBundle data,
        JupyterDisplayId displayId,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        return SerializeAsync(
            token => context.DisplayTrackedAsync(data, displayId, metadata, token),
            cancellationToken);
    }

    public ValueTask UpdateDisplayAsync(
        JupyterDisplayId displayId,
        MimeBundle data,
        IReadOnlyDictionary<string, JsonElement> metadata,
        CancellationToken cancellationToken)
    {
        return SerializeAsync(token => context.UpdateDisplayAsync(displayId, data, metadata, token), cancellationToken);
    }

    public ValueTask ClearOutputAsync(bool wait, CancellationToken cancellationToken)
    {
        return SerializeAsync(token => context.ClearOutputAsync(wait, token), cancellationToken);
    }

    public ValueTask WriteStderrAsync(string text, CancellationToken cancellationToken)
    {
        return SerializeAsync(token => context.WriteStderrAsync(text, token), cancellationToken);
    }

    public ValueTask PublishErrorAsync(
        string name,
        string value,
        IReadOnlyList<string> traceback,
        CancellationToken cancellationToken)
    {
        return SerializeAsync(token => context.PublishErrorAsync(name, value, traceback, token), cancellationToken);
    }

    public Task<string> RequestInputAsync(
        string prompt,
        bool password,
        CancellationToken cancellationToken)
    {
        return SerializeTaskAsync(token => context.RequestInputAsync(prompt, password, token), cancellationToken);
    }

    internal async ValueTask DeactivateAsync()
    {
        if (Interlocked.Exchange(ref active, 0) == 0) return;

        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
    }

    private async ValueTask SerializeAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsActive) await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<T> SerializeAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActive) throw new OperationCanceledException("The Notebook presentation sink is no longer active.");

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T> SerializeTaskAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActive) throw new OperationCanceledException("The Notebook presentation sink is no longer active.");

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}