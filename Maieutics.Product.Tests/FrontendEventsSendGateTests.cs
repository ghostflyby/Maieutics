using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Frontend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ParkingChatClient = Maieutics.Testing.ParkedChatClient;

namespace Maieutics.Product.Tests;

/// <summary>
///     Pins the events socket's send serialization (review finding F8). The events socket
///     has writers on independent flows: the run frame stream <c>HandleEvents</c> serves,
///     and the <c>ide.request</c> push that the announce registration hands to
///     <c>FrontendIdeSockets</c> (a context-less REPL or plugin invocation writes through
///     it from another task). A WebSocket permits a single outstanding send; an
///     unserialized second send faults the socket — on the pre-fix code the fault lands in
///     the run stream, escapes <c>ServeStreamAsync</c> and the handler's catches, and makes
///     <c>FrontendIdeSockets</c> withdraw the announcer as if the socket had died.
///     The overlap is forced deterministically with a fake transport whose
///     <c>SendAsync</c> parks on a task completion source the test
///     releases and records any entry made while a previous send is still outstanding —
///     the exact condition a real WebSocket faults on. The run writer's send is parked
///     inside the transport (its replay frames are finite and already published, so
///     nothing else advances it), the ide push is issued from another task, and the
///     overlap probe waits for a second parked send: on the pre-fix code the push must
///     enter the transport beside the parked run frame, so the probe records a violation
///     and the assertion fires; on the fixed code every write waits behind the handler's
///     send gate, the push parks at the gate where the transport cannot observe it, and
///     the probe's budget expires once instead. The probe then ends the parking phase —
///     the fake switches to pass-through, releasing every parked send and completing
///     every later send on arrival — so delivery is bounded by the push's own completion
///     rather than by a quiet window whose silence a loaded machine can outlast. A
///     real-transport version of this scenario could not pin the defect: sequential sends
///     never race, and no payload size forces a real send to stay outstanding.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FrontendEventsSendGateTests
{
    /// <summary>How long the overlap probe waits for the ide push to become the
    /// transport's second parked send. On the pre-fix code the arrival is independent of
    /// the test and lands within thread-pool scheduling time; on the fixed code the push
    /// parks at the send gate, the transport legitimately never sees it, and the probe
    /// burns this budget once before pass-through releases the writers.</summary>
    private static readonly TimeSpan OverlapProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>Budget for the waits whose arrival is deterministic on both code versions
    /// (the hello frame and the first run-side frame the events loop writes).</summary>
    private static readonly TimeSpan SendArrivalBudget = TimeSpan.FromSeconds(10);

    /// <summary>Budget for the ide push's delivery once the probe has ended: the push
    /// waits behind the send gate and the run-side frame, which pass-through releases, so
    /// on any healthy build it completes within scheduling time and the budget only
    /// bounds a genuinely stalled writer.</summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentEventsWritersNeverOverlapInsideTheTransport()
    {
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(20));
        await using var fixture = await SendGateFixture.StartAsync(deadline.Token).ConfigureAwait(true);

        // Writer registration: the events request announces IDE capability, so the handler
        // registers its ide.push delegate with FrontendIdeSockets. The socket it accepts is
        // the fixture's fake transport: sends park until the test releases them (or the
        // probe ends), and every entry is probed for overlap with a still-outstanding send.
        var transport = fixture.ArmEventsSocket();
        var eventsUri = new UriBuilder(fixture.BaseUrl)
        {
            Scheme = "ws",
            Path = $"/v1/agent/sessions/{fixture.SessionId}/events",
            Query = $"sinceSequence=0&ide=1&token={fixture.Token}"
        }.Uri;
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(eventsUri, deadline.Token).ConfigureAwait(true);
        Assert.True(
            await transport.TryWaitParkedSendsAsync(1, SendArrivalBudget, deadline.Token).ConfigureAwait(true),
            "the hello frame never reached the transport");

        // Release the hello and start a run whose provider parks before producing output:
        // the run stream's replay is finite and already published, so the run writer's
        // first send parks inside the transport and nothing else can complete it.
        transport.ReleaseAllParked();
        var accepted = await fixture.Service.StartTurnAsync(
            fixture.SessionId,
            "park at the provider",
            deadline.Token).ConfigureAwait(true);
        Assert.True(
            await transport.TryWaitParkedSendsAsync(1, SendArrivalBudget, deadline.Token).ConfigureAwait(true),
            "the run stream's first frame never reached the transport");

        // Second writer: the context-less ide push the announce registration serves. The
        // run-side frame is provably parked, and nothing is released until the probe ends —
        // on the pre-fix code the push becomes the second parked send (its arrival is the
        // only way the count can reach two), so its send entered the transport while a send
        // was outstanding; on the fixed code it parks at the send gate and the probe's
        // budget runs out instead.
        // The probe task is awaited (or observed through the parked-send signal) before the
        // fixture's using scope ends.
        // ReSharper disable AccessToDisposedClosure
        var push = Task.Run(
            () => fixture.IdeSockets.TrySendToAnnouncingAsync(ProbeIdeRequest(), deadline.Token),
            deadline.Token);
        // ReSharper restore AccessToDisposedClosure
        await transport.TryWaitParkedSendsAsync(2, OverlapProbeBudget, deadline.Token).ConfigureAwait(true);

        // Pin the overlap before anything is released: an entry made while a send was
        // still outstanding is the exact condition a real WebSocket rejects, and on the
        // pre-fix code the parked pair makes the push's entry one of them.
        Assert.Equal(0, transport.OverlappingSendEntries);

        // The probe ends the parking phase: pass-through releases the parked writers and
        // completes every later send on arrival, so delivery is bounded by the push's own
        // completion — no quiet window whose one second of silence a loaded machine can
        // outlast, leaving a writer parked with nobody left to release it.
        transport.EnterPassThrough();
        Assert.True(
            await push.WaitAsync(DeliveryBudget, deadline.Token).ConfigureAwait(true),
            "the ide push was never delivered through the announcing delegate");

        // The drain phase stayed serialized too: pass-through sends still enter one at a
        // time behind the send gate, so the counter cannot have moved after the probe.
        Assert.Equal(0, transport.OverlappingSendEntries);

        var sends = transport.Sends;
        Assert.True(
            sends.Count >= 3,
            $"expected the hello, a run frame, and the ide push; recorded {sends.Count} sends");
        Assert.Equal("hello", sends[0].FrameType);
        Assert.Contains("ide.request", sends.Select(static send => send.FrameType).ToList());
        foreach (var send in sends)
        {
            Assert.True(
                send.EndOfMessage,
                $"frame '{send.FrameType}' was not delivered as one complete message");
            Assert.Equal(WebSocketMessageType.Text, send.MessageType);
        }

        // Teardown: abort the events connection (the server observes RequestAborted and
        // tears the handler down through the same paths as a peer disconnect), then cancel
        // the parked run through shutdown, and observe it settle instead of leaking a
        // parked provider or a parked receive loop.
        ws.Abort();
        fixture.Service.BeginShutdown();
        if (fixture.Service.TryGetRun(accepted.RunId, out var stream) && stream is not null)
        {
            await stream.Settled.WaitAsync(deadline.Token).ConfigureAwait(true);
        }
    }

    /// <summary>Builds the frame a context-less ide invocation pushes: the shape only
    /// matters to the recording assertions, since the fake transport never answers it.</summary>
    private static FrontendEventFrame ProbeIdeRequest() => new(
        "ide.request",
        RequestId: "send-gate-probe",
        Data: JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["method"] = "probe/ping"
        }));

    /// <summary>A server-side events-socket fake whose sends park on a completion the test
    /// releases until the probe ends, after which pass-through completes every send on
    /// arrival. Every entry is probed while a previous send is still outstanding — the
    /// single-outstanding-send rule a real WebSocket enforces by throwing — and every
    /// payload is retained whole so the assertions can pin delivery.</summary>
    private sealed class BlockableEventsSocket : WebSocket
    {
        /// <summary>One recorded send: the whole payload with its framing flags, plus the
        /// release the test completes to let the owning writer continue. The payload is
        /// retained whole for delivery fidelity even where the assertions read the type.</summary>
        // ReSharper disable NotAccessedPositionalProperty.Local
        public sealed record RecordedSend(
                byte[] Payload,
                string FrameType,
                bool EndOfMessage,
                WebSocketMessageType MessageType)
            // ReSharper restore NotAccessedPositionalProperty.Local
        {
            private readonly TaskCompletionSource _release = NewSignal();

            /// <summary>The task the sending writer awaits until the test releases it.</summary>
            public Task Parking => _release.Task;

            public bool Released { get; private set; }

            public void MarkReleased()
            {
                Released = true;
                _release.TrySetResult();
            }
        }

        private readonly Lock _gate = new();
        private readonly List<RecordedSend> _sends = [];
        private TaskCompletionSource _arrival = NewSignal();
        private long _inFlight;
        private long _overlapping;

        // Set once when the probe ends: later sends complete on arrival instead of
        // parking, so the delivery phase needs no release protocol a slow writer could
        // fall behind.
        private bool _passThrough;

        /// <summary>Entries observed while a previous send had not completed: the overlap
        /// a real WebSocket faults on, recorded instead of thrown so the assertion is the
        /// failure rather than a racing exception.</summary>
        public long OverlappingSendEntries => Interlocked.Read(ref _overlapping);

        public IReadOnlyList<RecordedSend> Sends
        {
            get
            {
                lock (_gate) return _sends.ToArray();
            }
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (buffer.Array is not { } array)
            {
                throw new InvalidOperationException("The events transport fake requires a filled buffer.");
            }

            Interlocked.Increment(ref _inFlight);
            if (Interlocked.Read(ref _inFlight) > 1)
            {
                Interlocked.Increment(ref _overlapping);
            }

            try
            {
                var payload = new byte[buffer.Count];
                Array.Copy(array, buffer.Offset, payload, 0, buffer.Count);
                var recorded = new RecordedSend(
                    payload,
                    FrameTypeOf(payload),
                    endOfMessage,
                    messageType);
                lock (_gate)
                {
                    if (_passThrough) recorded.MarkReleased();
                    _sends.Add(recorded);
                    _arrival.TrySetResult();
                    _arrival = NewSignal();
                }

                await recorded.Parking.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        /// <summary>Waits until at least <paramref name="count" /> sends are parked, or the
        /// budget runs out: signal-driven on the transport's arrival, with the budget only
        /// bounding the fixed code's expected non-arrival (a writer parked at the send gate
        /// never reaches the transport).</summary>
        public async Task<bool> TryWaitParkedSendsAsync(
            int count,
            TimeSpan budget,
            CancellationToken cancellationToken)
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(budget);
            while (true)
            {
                TaskCompletionSource signal;
                lock (_gate)
                {
                    if (CountParked() >= count) return true;
                    signal = _arrival;
                }

                try
                {
                    await signal.Task.WaitAsync(budgetCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (budgetCts.IsCancellationRequested)
                {
                    lock (_gate) return CountParked() >= count;
                }
            }
        }

        public void ReleaseAllParked()
        {
            lock (_gate)
            {
                foreach (var send in _sends)
                {
                    if (!send.Released) send.MarkReleased();
                }
            }
        }

        /// <summary>Ends the parking phase: every parked send is released and every later
        /// send completes on arrival. Delivery assertions then ride the writers' own task
        /// completions instead of a quiet window that bets no further send arrives within
        /// its budget.</summary>
        public void EnterPassThrough()
        {
            lock (_gate)
            {
                _passThrough = true;
                foreach (var send in _sends)
                {
                    if (!send.Released) send.MarkReleased();
                }
            }
        }

        private int CountParked()
        {
            var count = 0;
            foreach (var send in _sends)
            {
                if (!send.Released) count++;
            }

            return count;
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static string FrameTypeOf(byte[] payload)
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.GetProperty("type").GetString()
                   ?? throw new InvalidOperationException("The events frame carried no type.");
        }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            // The events stream is half-duplex, so the receive drain only has to park until
            // teardown cancels it — the cancellation DrainReceiveAsync already tolerates.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException("The receive drain can only leave via cancellation.");
        }
    }

    /// <summary>Presents the fake transport through the standard WebSocket acceptance seam:
    /// the decorated feature completes the real upgrade handshake (so the connecting
    /// client's <c>ClientWebSocket</c> settles) and then hands the handler the fake socket,
    /// so <c>HandleEvents</c>, the announce registration, and every writer run exactly as
    /// routed in production — the only substitution is the socket the handler accepts.</summary>
    private sealed class FixedWebSocketFeature(IHttpWebSocketFeature inner, WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;

        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext acceptContext)
        {
            await inner.AcceptAsync(acceptContext).ConfigureAwait(false);
            return socket;
        }
    }

    /// <summary>A bare <c>WebApplication</c> hosting the real frontend endpoint table over
    /// the real session service, turn queue, and IDE socket registry; the events handler
    /// is reached as routed and accepts the fake transport through the feature seam.</summary>
    private sealed class SendGateFixture : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly FrontendOptions _options;
        private readonly FrontendHost _host;
        private readonly HttpClient _client;
        private volatile BlockableEventsSocket? _eventsSocket;

        private SendGateFixture(
            WebApplication application,
            FrontendOptions options,
            FrontendHost host,
            MaieuticsAgentSessionManager manager,
            FrontendSessionService service,
            FrontendIdeSockets ideSockets)
        {
            this._application = application;
            this._options = options;
            this._host = host;
            Manager = manager;
            Service = service;
            IdeSockets = ideSockets;
            _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{options.Port}") };
        }

        public MaieuticsAgentSessionManager Manager { get; }

        public FrontendSessionService Service { get; }

        public FrontendIdeSockets IdeSockets { get; }

        public string Token => _options.Token;

        public string BaseUrl => $"http://127.0.0.1:{_options.Port}";

        public string SessionId => Manager.Id.Value.ToString("N");

        public BlockableEventsSocket ArmEventsSocket()
        {
            var socket = new BlockableEventsSocket();
            _eventsSocket = socket;
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            Service.BeginShutdown();
            await _application.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _host.DisposeAsync().ConfigureAwait(false);
            Manager.Dispose();
            await _application.DisposeAsync().ConfigureAwait(false);
        }

        public static async Task<SendGateFixture> StartAsync(CancellationToken cancellationToken)
        {
            // FrontendOptions.Create reserves the loopback port Kestrel binds below, the
            // same arrangement the executable's composition root uses; the discovery file
            // is only a token carrier here and is never written.
            var options = FrontendOptions.Create(Path.Combine(
                Path.GetTempPath(), $"maieutics-frontend-send-gate-{Guid.NewGuid():N}.json"));
            var manager = new MaieuticsAgentSessionManager(
                new FixedProfileProvider(
                    new AgentRunProfile(new ParkingChatClient(), new AgentSessionOptions())),
                familiesRoot: null,
                storeFactory: null,
                NullLogger<MaieuticsAgentSessionManager>.Instance);
            var service = new FrontendSessionService(
                manager,
                new MaieuticsCommandExecutor(manager, null, null, null, null),
                new FrontendDenoReplPresentationRouter(),
                NullLogger<FrontendSessionService>.Instance);
            var turnQueue = new FrontendTurnQueue(service, manager, NullLogger<FrontendTurnQueue>.Instance);
            var ideSockets = new FrontendIdeSockets();
            var host = new FrontendHost(
                options,
                service,
                turnQueue,
                NullLogger<FrontendHost>.Instance,
                ideSockets: ideSockets);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
            var application = builder.Build();
            var fixture = new SendGateFixture(application, options, host, manager, service, ideSockets);
            host.MapEndpoints(application);
            application.UseWebSockets();
            // Registered after UseWebSockets so the decoration replaces the middleware's
            // default feature: the events handler accepts the fake socket through the
            // standard seam while the real upgrade handshake still completes for the
            // client, and the receive drain keeps observing RequestAborted as usual.
            application.Use(async (context, next) =>
            {
                if (context.Request.Path.Value?.EndsWith("/events", StringComparison.Ordinal) == true &&
                    fixture._eventsSocket is { } events &&
                    context.Features.Get<IHttpWebSocketFeature>() is { } inner)
                {
                    context.Features.Set<IHttpWebSocketFeature>(new FixedWebSocketFeature(inner, events));
                }

                await next(context).ConfigureAwait(false);
            });
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return fixture;
        }
    }
}
