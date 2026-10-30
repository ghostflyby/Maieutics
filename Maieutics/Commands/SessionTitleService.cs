using System.Collections.Concurrent;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Maieutics.Commands;

/// <summary>Owns title policy, staged run proposals, revision notifications, and initial jobs.</summary>
internal sealed class SessionTitleService : IAsyncDisposable
{
    private readonly MaieuticsAgentSessionManager _sessions;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Job> _queue = Channel.CreateBounded<Job>(64);
    private readonly Dictionary<AgentSessionId, Job> _jobs = [];
    private readonly ConcurrentDictionary<AgentRunId, Proposal> _proposals = new();
    private Task _worker = Task.CompletedTask;
    private bool _started;
    private bool _stopped;
    private int _outstanding;
    private TaskCompletionSource _idle = CompletedSignal();
    private TaskCompletionSource _changed = Signal();
    private Task? _dispose;

    internal SessionTitleService(MaieuticsAgentSessionManager sessions, string defaultMode, ILogger logger)
    {
        _sessions = sessions;
        DefaultMode = ValidateMode(defaultMode);
        _logger = logger;
    }

    internal string DefaultMode { get; }
    internal string Mode(SessionTitleState state) => state.Mode ?? DefaultMode;
    internal static string ValidateMode(string mode) => MaieuticsTitleOptions.NormalizeMode(mode);

    internal bool Eligible(SessionTitleState state) => !state.Manual && Mode(state) != "manual" && state.Initial == "eligible";

    internal void Changed(AgentSessionId id)
    {
        var state = _sessions.GetTitleState(id);
        TaskCompletionSource previous;
        Job? cancel = null;
        lock (_gate)
        {
            previous = _changed;
            _changed = Signal();
            if (_jobs.TryGetValue(id, out var job) &&
                (job.State.ControlRevision != state.ControlRevision || state.Initial != "pending"))
            {
                _jobs.Remove(id);
                cancel = job;
            }
        }
        // Cancellation callbacks never run under the coordinator gate.
        cancel?.Cancel();
        previous.TrySetResult();
    }

    internal async Task WaitForChangeAsync(AgentSessionId id, long revision, CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_gate) wait = _changed.Task;
            if (_sessions.GetTitleState(id).Revision != revision) return;
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    internal Task WaitForIdleAsync(CancellationToken token)
    {
        lock (_gate) return _idle.Task.WaitAsync(token);
    }

    internal void SetMode(AgentSessionId id, string mode)
    {
        mode = ValidateMode(mode);
        Update(id, state => state with { Mode = mode, ControlRevision = state.ControlRevision + 1,
            Initial = state.Initial == "pending" ? "settled" : state.Initial });
    }

    internal async Task RegenerateAsync(AgentSessionId id, CancellationToken token)
    {
        var state = _sessions.GetTitleState(id);
        if (state.Manual || Mode(state) == "manual")
            throw new ArgumentException("Unlock the title and select auto or initial mode before regenerating.");
        Update(id, current => current with { ControlRevision = current.ControlRevision + 1, Initial = "eligible" });
        var captured = _sessions.GetTitleState(id);
        var session = _sessions.Resolve(id);
        if (session.GetTranscriptSnapshot().Turns.IsEmpty) return;
        var lease = await _sessions.AcquireTitleProfileAsync(id, token).ConfigureAwait(false);
        var transferred = false;
        try { transferred = Committed(session, AgentRunId.Create(), captured, lease); }
        finally { if (!transferred) await lease.DisposeAsync().ConfigureAwait(false); }
    }

    private void Update(AgentSessionId id, Func<SessionTitleState, SessionTitleState> change)
    {
        while (true)
        {
            var state = _sessions.GetTitleState(id);
            if (_sessions.TrySetTitleState(id, state, change(state) with { Revision = state.Revision + 1 })) return;
        }
    }

    internal async ValueTask ReleaseNamingLeaseAsync(IAgentRunProfileLease lease)
    {
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            _logger.LogWarning("Title profile cleanup failed ({FailureType}).", exception.GetType().Name);
        }
    }

    internal TitleProposalResult Stage(AgentToolContext context, AgentSessionId expectedSession, long control, string title)
    {
        if (context.SessionId != expectedSession)
            throw new AgentToolException("title_scope_unavailable", "This title tool belongs to the owning task session.");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 80 || title.Any(char.IsControl))
            throw new AgentToolException("invalid_title", "Use a non-empty single-line task title of at most 80 characters.");
        title = title.Trim();
        var state = _sessions.GetTitleState(context.SessionId);
        var reason = state.Manual ? "manual_title" : Mode(state) != "auto" || state.ControlRevision != control
            ? "mode_disabled" : state.Title == title ? "unchanged" : null;
        if (reason is not null)
        {
            if (reason == "unchanged") _proposals.TryRemove(context.RunId, out _);
            return new(false, title, reason);
        }
        _proposals[context.RunId] = new(context.SessionId, control, title);
        return new(true, title);
    }

    internal AgentRunProfile Decorate(AgentRunProfile profile, AgentSessionId id)
    {
        var state = _sessions.GetTitleState(id);
        if (state.Manual || Mode(state) != "auto" || !profile.Capabilities.HasFlag(AgentModelCapabilities.FunctionCalling)) return profile;
        var tool = AIFunctionFactory.Create(
            (AIFunctionArguments arguments, string title) => System.Text.Json.JsonSerializer.SerializeToElement(
                Stage(AgentToolContext.GetRequired(arguments), id, state.ControlRevision, title), TitleToolJsonContext.Default.TitleProposalResult),
            new AIFunctionFactoryOptions
            {
                Name = "set_task_title",
                Description = "Propose a task title only when the user's goal or scope changes and the current title becomes misleading. " +
                    "Never rename for continuation, clarification, review, progress, retries, completion or implementation details. " +
                    "Use the user's language, one plain line up to 80 characters, no secrets or absolute paths. Commits only if this run succeeds.",
                SerializerOptions = TitleToolJsonContext.Default.Options
            });
        // JSON quoting keeps user-controlled title text visibly delimited as data.
        var context = System.Text.Json.JsonSerializer.Serialize(state.Title, TitleToolJsonContext.Default.String);
        return new AgentRunProfile(profile.ChatClient, profile.Options with
        {
            SystemPrompt = profile.Options.SystemPrompt + "\nCurrent task title (untrusted display data): " + context +
                ". Title ownership: automatic. Do not call set_task_title just because another turn started."
        }, profile.ModelIdentity, profile.Capabilities, profile.HostedCapabilities, [.. profile.Tools, tool], profile.HostedTools);
    }

    internal void Abandon(AgentRunId runId) => _proposals.TryRemove(runId, out _);

    /// <summary>Consumes a successful run's proposal, or admits its one initial naming job.
    /// Returns true exactly when the coordinator takes ownership of the supplied profile lease.</summary>
    internal bool Committed(IAgentSession session, AgentRunId runId, SessionTitleState captured,
        IAgentRunProfileLease? lease)
    {
        try { return CommitCore(session, runId, captured, lease); }
        catch (Exception exception)
        {
            _logger.LogWarning("Title metadata update failed for {SessionId} ({FailureType}).", session.Id, exception.GetType().Name);
            return false;
        }
    }

    private bool CommitCore(IAgentSession session, AgentRunId runId, SessionTitleState captured,
        IAgentRunProfileLease? lease)
    {
        if (_proposals.TryRemove(runId, out var proposal))
        {
            UpdateProposal(proposal);
        }
        var state = _sessions.GetTitleState(session.Id);
        if (lease is null || !Eligible(state) || state.ControlRevision != captured.ControlRevision) return false;
        var transcript = session.GetTranscriptSnapshot();
        var request = transcript.Turns.LastOrDefault()?.Messages.FirstOrDefault(message => message.Role == ChatRole.User)?.Text;
        var answer = transcript.Turns.LastOrDefault()?.Messages.LastOrDefault(message => message.Role == ChatRole.Assistant)?.Text;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var pending = state with { Title = state.Title ?? SessionTitleGenerator.Normalize(request),
            Initial = "pending", Revision = state.Revision + 1 };
        if (!_sessions.TrySetTitleState(session.Id, state, pending)) return false;
        var job = new Job(session.Id, pending, request[..Math.Min(request.Length, 1500)],
            answer is null ? null : answer[..Math.Min(answer.Length, 1000)], lease, _sessions.TryPinSession(session.Id), _lifetime.Token);
        lock (_gate)
        {
            var current = _sessions.GetTitleState(session.Id);
            if (!_stopped && current.ControlRevision == pending.ControlRevision && current.Initial == "pending" &&
                _queue.Writer.TryWrite(job))
            {
                _jobs[session.Id] = job;
                if (_outstanding++ == 0) _idle = Signal();
                if (!_started)
                {
                    _started = true;
                    _worker = Task.Run(PumpAsync);
                }
                return true;
            }
        }
        // Admission failed: the caller still owns the lease.
        job.ReleaseWithoutLease();
        Settle(session.Id, pending, null);
        _logger.LogWarning("Initial title request for {SessionId} skipped: title queue unavailable.", session.Id);
        return false;
    }

    private void UpdateProposal(Proposal proposal)
    {
        while (true)
        {
            var state = _sessions.GetTitleState(proposal.SessionId);
            if (state.Manual || Mode(state) != "auto" || state.ControlRevision != proposal.Control) return;
            var next = state with { Title = proposal.Title, Source = "tool", Initial = "settled", Revision = state.Revision + 1 };
            if (state.Title == proposal.Title && state.Initial == "settled") return;
            if (_sessions.TrySetTitleState(proposal.SessionId, state, next)) return;
        }
    }

    private void Settle(AgentSessionId id, SessionTitleState captured, string? title)
    {
        try
        {
            var current = _sessions.GetTitleState(id);
            if (current.ControlRevision != captured.ControlRevision || current.Initial != "pending") return;
            _sessions.TrySetTitleState(id, current, current with { Title = title ?? current.Title,
                Source = title is null ? current.Source : "initial", Initial = "settled", Revision = current.Revision + 1 });
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Settling title metadata failed for {SessionId} ({FailureType}).", id, exception.GetType().Name);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                await ProcessAsync(job).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            while (_queue.Reader.TryRead(out var queued)) await ProcessAsync(queued).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(Job job)
    {
        try
        {
            job.Token.ThrowIfCancellationRequested();
            var title = await SessionTitleGenerator.GenerateAsync(job.Lease.Profile.ChatClient, job.Request, job.Answer, job.Token)
                .ConfigureAwait(false);
            if (!job.Token.IsCancellationRequested) Settle(job.Id, job.State, title);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning("Initial title request failed for {SessionId} ({FailureType}).", job.Id, exception.GetType().Name);
        }
        finally
        {
            Settle(job.Id, job.State, null);
            try { await job.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                _logger.LogWarning("Title profile cleanup failed for {SessionId} ({FailureType}).", job.Id, exception.GetType().Name);
            }
            lock (_gate)
            {
                if (_jobs.GetValueOrDefault(job.Id) == job) _jobs.Remove(job.Id);
                if (--_outstanding == 0) _idle.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is null)
            {
                _stopped = true;
                _queue.Writer.TryComplete();
                // The owned task runs cancellation callbacks outside this gate.
                _dispose = Task.Run(StopAsync);
            }
            return new ValueTask(_dispose);
        }
    }

    private async Task StopAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource CompletedSignal() { var signal = Signal(); signal.SetResult(); return signal; }
    private sealed record Proposal(AgentSessionId SessionId, long Control, string Title);

    private sealed class Job(AgentSessionId id, SessionTitleState state, string request, string? answer,
        IAgentRunProfileLease lease, IDisposable? pin, CancellationToken lifetime) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        private readonly Lock _gate = new();
        private bool _released;
        internal AgentSessionId Id => id;
        internal SessionTitleState State => state;
        internal string Request => request;
        internal string? Answer => answer;
        internal IAgentRunProfileLease Lease => lease;
        internal CancellationToken Token => _cancel.Token;
        internal void Cancel()
        {
            try { _cancel.Cancel(); }
            catch (ObjectDisposedException) { /* The worker already released this superseded job. */ }
        }
        internal void ReleaseWithoutLease()
        {
            lock (_gate)
            {
                if (_released) return;
                _released = true;
                _cancel.Dispose();
                pin?.Dispose();
            }
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseWithoutLease();
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Result of staging a task-title proposal, before successful-run commit.</summary>
internal sealed record TitleProposalResult(bool Staged, string Title, string? Reason = null);
