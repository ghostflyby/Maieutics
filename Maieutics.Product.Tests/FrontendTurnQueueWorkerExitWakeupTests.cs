using Harness = Maieutics.Product.Tests.FrontendApiIntegrationTests.FrontendHarness;
using GatedProvider = Maieutics.Testing.GatedModelProvider;

namespace Maieutics.Product.Tests;

[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendTurnQueueWorkerExitWakeupTests
{
    private const string Answer = "wakeup answer";

    /// <summary>
    ///     Regression for the lost worker-exit wakeup behind the intermittent CI drain
    ///     hang (<c>FrontendTurnQueueIntegrationTests</c> drains dying at their whole
    ///     budget, green on rerun). The worker cleared its Worker reference and returned
    ///     without publishing a queue change, so a waiter that woke on the per-item
    ///     settle publish, observed the still-set Worker, captured that version, and
    ///     re-subscribed before the exit was never signalled again: the queue had
    ///     quiesced, no later mutation existed to wake it, and the drain's
    ///     <c>WaitUntilIdleAsync</c> waited out its whole budget on a quiescent queue.
    ///     <para>
    ///     The pin is completion-driven on both worlds, with no timeout as the
    ///     detector. The gated provider parks the single queued run, so the settle —
    ///     the only publish between the running transition and the worker's exit — is
    ///     caught by a waiter armed before the release. The worker's own completion
    ///     task then serves as the strictly-after event: on the fixed worker the exit
    ///     publish precedes the return, so once the task completes the queue version
    ///     has moved past the settle; on a silent exit it has not, and a synchronous
    ///     state check fails immediately. The worker task is reached through the same
    ///     documented reflection the disposal tests use (the queue's internals are
    ///     visible to this assembly); the deadline stays purely as the outer budget.
    ///     </para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task WorkerExitTransitionWakesAWaiterThatSubscribedAtTheSettledVersion()
    {
        var provider = new GatedProvider(answer: Answer);
        await using var harness = await StartHarnessAsync(TestContext.Current.CancellationToken, provider);
        var sessionId = await harness.GetSessionIdAsync(TestContext.Current.CancellationToken);
        var addressed = new Agent.AgentSessionId(Guid.ParseExact(sessionId, "N"));

        // One item, parked at the gated provider: every later queue mutation is under
        // the test's control, and none can happen until the release. The worker is
        // alive while the run is parked, so its completion task is resolvable now.
        harness.TurnQueue.Enqueue(sessionId, [Answer]);
        await provider.WaitForParkedAsync(1, TestContext.Current.CancellationToken);
        if (!harness.TurnQueue._sessions.TryGetValue(addressed, out var state))
            throw new InvalidOperationException("The session has no queue state.");

        if (state.Worker is not { } worker)
            throw new InvalidOperationException("The turn queue worker was not running.");
        var workerCompletion = worker.Completion;

        // Signal-driven catch-up to the running transition: the run is parked, so the
        // running publish is the last mutation that can exist before the release — the
        // loop lands on it deterministically.
        var version = await harness.TurnQueue.WaitForChangeAsync(sessionId, 0, TestContext.Current.CancellationToken);
        while (harness.TurnQueue.FrameSnapshot(sessionId).Snapshot.Running is null)
            version = await harness.TurnQueue.WaitForChangeAsync(sessionId, version, TestContext.Current.CancellationToken);
        var runningVersion = version;

        // Subscribe before the release, then release: the next mutation is exactly the
        // settle publish (the worker publishes nothing between the running and settle
        // transitions), so the settled version is runningVersion + 1 by arithmetic. The
        // wait's own return value is deliberately not used — on the fixed worker the
        // exit publish can race into its re-lock and make it one higher.
        var settleWait = harness.TurnQueue.WaitForChangeAsync(sessionId, runningVersion, TestContext.Current.CancellationToken);
        provider.ReleaseNext();
        await settleWait;
        var settledVersion = runningVersion + 1;

        // The worker's completion is the strictly-after event for the exit transition:
        // the worker returns on both worlds (silently on the unfixed one), so awaiting
        // it is a completion wait that needs no timeout to detect anything.
        await workerCompletion;

        // With the worker gone the exit transition has happened. On the fixed worker
        // the exit publish preceded the completion, so the version moved past the
        // settle; on a silent exit it never moved — a waiter standing at the settled
        // version is never woken, which is the CI drain hang. A synchronous state
        // check: no waiting, no timeout as the detector.
        var finalVersion = harness.TurnQueue.FrameSnapshot(sessionId).Version;
        Assert.True(
            finalVersion > settledVersion,
            $"the worker exited without publishing: the queue version stayed at the settle's {settledVersion}, "
            + "so a waiter that re-subscribed at the settled version is never woken and the drain waits out "
            + "its whole budget on a quiescent queue");
    }

    private static async Task<Harness> StartHarnessAsync(
        CancellationToken cancellationToken,
        GatedProvider provider)
    {
        // The shared harness extracts the endpoint from the provider instance, gated or not.
        return await Harness.StartAsync(
            cancellationToken,
            provider,
            hanging: false);
    }
}
