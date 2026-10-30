using Maieutics.Agent;
using Maieutics.Persistence;
using Microsoft.Extensions.AI;

namespace Maieutics.Commands;

/// <summary>Product session boundary that settles metadata for every owning run.</summary>
internal sealed class TitledAgentSession(IAgentSession inner, MaieuticsAgentSessionManager sessions) : IAgentSession
{
    private int _reserved;
    internal IAgentSession Inner => inner;
    public AgentSessionId Id => inner.Id;
    public bool IsRunInProgress => Volatile.Read(ref _reserved) != 0 || inner.IsRunInProgress;
    public AgentTranscript GetTranscriptSnapshot() => inner.GetTranscriptSnapshot();

    public async Task<IAgentRun> StartTurnAsync(AgentTurn turn, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _reserved, 1, 0) != 0) throw new AgentTurnInProgressException();
        IAgentRunProfileLease? namingLease = null;
        try
        {
            var captured = sessions.GetTitleState(Id);
            if (sessions.Titles.Eligible(captured))
                namingLease = await sessions.AcquireTitleProfileAsync(Id, cancellationToken).ConfigureAwait(false);
            var run = await inner.StartTurnAsync(turn, cancellationToken).ConfigureAwait(false);
            return Run.Start(this, run, sessions, captured, namingLease);
        }
        catch
        {
            if (namingLease is not null) await sessions.Titles.ReleaseNamingLeaseAsync(namingLease).ConfigureAwait(false);
            Volatile.Write(ref _reserved, 0);
            throw;
        }
    }

    private sealed class Run : IAgentRun
    {
        private readonly IAgentRun _inner;
        private Task<AgentRunResult>? _completion;
        private Run(IAgentRun inner) { _inner = inner; }
        internal static Run Start(TitledAgentSession owner, IAgentRun inner, MaieuticsAgentSessionManager sessions,
            SessionTitleState captured, IAgentRunProfileLease? namingLease)
        {
            var run = new Run(inner);
            run._completion = run.FinishAsync(owner, sessions, captured, namingLease);
            return run;
        }
        public AgentRunId Id => _inner.Id;
        public AgentSessionId SessionId => _inner.SessionId;
        public IAsyncEnumerable<AgentEvent> Events => _inner.Events;
        public Task<AgentRunResult> Completion => _completion ?? throw new InvalidOperationException("The product run has not started.");
        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            await _inner.CancelAsync(cancellationToken).ConfigureAwait(false);
            try { await Completion.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Match the runtime's cancel contract: Completion retains its terminal cause.
            }
        }
        private async Task<AgentRunResult> FinishAsync(TitledAgentSession owner, MaieuticsAgentSessionManager sessions,
            SessionTitleState captured, IAgentRunProfileLease? namingLease)
        {
            try
            {
                var result = await _inner.Completion.ConfigureAwait(false);
                if (sessions.Titles.Committed(owner, Id, captured, namingLease)) namingLease = null;
                return result;
            }
            finally
            {
                sessions.Titles.Abandon(Id);
                try { if (namingLease is not null) await sessions.Titles.ReleaseNamingLeaseAsync(namingLease).ConfigureAwait(false); }
                finally { Volatile.Write(ref owner._reserved, 0); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            try { await _inner.DisposeAsync().ConfigureAwait(false); }
            finally { try { await Completion.ConfigureAwait(false); } catch (Exception) { /* Completion owns the terminal cause. */ } }
        }
    }
}
