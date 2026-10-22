using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Harness = Maieutics.Product.Tests.FrontendApiIntegrationTests.FrontendHarness;
using Maieutics.Frontend;

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
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(50));
        var provider = new GatedProvider();
        await using var harness = await StartHarnessAsync(deadline.Token, provider);
        var sessionId = await harness.GetSessionIdAsync(deadline.Token);
        var addressed = new Agent.AgentSessionId(Guid.ParseExact(sessionId, "N"));

        // One item, parked at the gated provider: every later queue mutation is under
        // the test's control, and none can happen until the release. The worker is
        // alive while the run is parked, so its completion task is resolvable now.
        harness.TurnQueue.Enqueue(sessionId, [Answer]);
        await provider.WaitForParkedAsync(1, deadline.Token);
        var workerCompletion = ResolveWorkerCompletion(harness.TurnQueue, addressed);

        // Signal-driven catch-up to the running transition: the run is parked, so the
        // running publish is the last mutation that can exist before the release — the
        // loop lands on it deterministically.
        var version = await harness.TurnQueue.WaitForChangeAsync(sessionId, 0, deadline.Token);
        while (harness.TurnQueue.FrameSnapshot(sessionId).Snapshot.Running is null)
            version = await harness.TurnQueue.WaitForChangeAsync(sessionId, version, deadline.Token);
        var runningVersion = version;

        // Subscribe before the release, then release: the next mutation is exactly the
        // settle publish (the worker publishes nothing between the running and settle
        // transitions), so the settled version is runningVersion + 1 by arithmetic. The
        // wait's own return value is deliberately not used — on the fixed worker the
        // exit publish can race into its re-lock and make it one higher.
        var settleWait = harness.TurnQueue.WaitForChangeAsync(sessionId, runningVersion, deadline.Token);
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

    /// <summary>Resolves the session queue worker's completion task while the worker is
    /// running. The queue's internals are visible to this assembly; the nested private
    /// state is reached through reflection the same way the disposal tests reach their
    /// probes — the shape is pinned by the assertions, a rename fails loudly here
    /// rather than silently weakening the test.</summary>
    private static Task ResolveWorkerCompletion(FrontendTurnQueue queue, Agent.AgentSessionId sessionId)
    {
        var sessionsField = typeof(FrontendTurnQueue).GetField(
            "sessions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (sessionsField is null || sessionsField.GetValue(queue) is not IDictionary sessions)
            throw new InvalidOperationException("FrontendTurnQueue has no sessions dictionary.");

        if (sessions[sessionId] is not { } state)
            throw new InvalidOperationException("The session has no queue state.");

        var workerProperty = state.GetType().GetProperty(
            "Worker",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (workerProperty is null || workerProperty.GetValue(state) is not { } worker)
            throw new InvalidOperationException("The turn queue worker was not running.");

        var completionProperty = worker.GetType().GetProperty(
            "Completion",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (completionProperty is null || completionProperty.GetValue(worker) is not Task completion)
            throw new InvalidOperationException("The turn queue worker has no completion task.");

        return completion;
    }

    private static async Task<Harness> StartHarnessAsync(
        CancellationToken cancellationToken,
        GatedProvider provider)
    {
        // The shared harness extracts the endpoint only from its known provider types, so
        // the gated server injects its endpoint through the configuration transform.
        return await Harness.StartAsync(
            cancellationToken,
            provider,
            hanging: false,
            transformConfiguration: body => body.Replace(
                "\"Endpoint\": \"\"",
                $"\"Endpoint\": \"{provider.Endpoint}\"",
                StringComparison.Ordinal));
    }

    /// <summary>A chat-completions SSE provider that parks every request until the test
    /// releases it, so run settlements become explicit steps and the queue's settle
    /// publish is a deterministic observation point (the shape of the turn queue tests'
    /// gated server, trimmed to this test's single parked run).</summary>
    private sealed class GatedProvider : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task serveLoop;
        private readonly Lock gate = new();
        private readonly List<TaskCompletionSource> parked = [];
        private TaskCompletionSource parkedChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> connections = [];

        public GatedProvider()
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
            serveLoop = ServeAsync(cancellation.Token);
        }

        public Uri Endpoint { get; }

        /// <summary>Waits until at least <paramref name="minimum" /> provider requests
        /// have arrived and are waiting for release. Signal-driven: every parked-count
        /// mutation completes a fresh source, so the wait needs no polling.</summary>
        public async Task WaitForParkedAsync(int minimum, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Task signal;
                lock (gate)
                {
                    if (parked.Count >= minimum) return;

                    signal = parkedChanged.Task;
                }

                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Releases the oldest provider request still awaiting release. The
        /// completed source is fired outside the lock: the entry is already unlisted, so
        /// only this call can complete it.</summary>
        public void ReleaseNext()
        {
            TaskCompletionSource? release = null;
            lock (gate)
            {
                while (parked.Count > 0)
                {
                    var next = parked[0];
                    parked.RemoveAt(0);
                    if (next.Task.IsCompleted) continue;
                    release = next;
                    break;
                }

                if (release is not null)
                {
                    parkedChanged.TrySetResult();
                    parkedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            release?.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            parkedChanged.TrySetResult();
            Task[] connectionTasks;
            lock (gate)
            {
                connectionTasks = [.. connections];
            }

            try
            {
                await Task.WhenAll(connectionTasks).ConfigureAwait(false);
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            try
            {
                await serveLoop.ConfigureAwait(false);
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    Task connection = ServeClientAsync(client, cancellationToken);
                    lock (gate)
                    {
                        connections.Add(connection);
                    }
                }
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
            }
        }

        private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = client.GetStream();
                // The request must be read in full before parking, so a release can never
                // land while the client is still sending: answering a partially-sent
                // request resets the connection and surfaces as a provider error instead
                // of this test's answer.
                await FakeOpenAiServer.ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gate)
                {
                    parked.Add(release);
                    parkedChanged.TrySetResult();
                    parkedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                try
                {
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                    {
                        parked.Remove(release);
                    }
                }

                var body = Encoding.UTF8.GetBytes(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"" + Answer + "\"}}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                var headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n\r\n");
                await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when
                (exception is OperationCanceledException or EndOfStreamException or SocketException or IOException
                    or ObjectDisposedException)
            {
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
