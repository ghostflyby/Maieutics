using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Maieutics.Testing;

/// <summary>The API flavor the fake provider speaks; selects the request path, the built-in
/// request checks, and the answer stream shape.</summary>
public enum ModelApiFlavor
{
    /// <summary>OpenAI-style <c>/v1/chat/completions</c> SSE.</summary>
    ChatCompletions,

    /// <summary>OpenAI-style <c>/v1/responses</c> SSE.</summary>
    Responses,

    /// <summary>Anthropic-style <c>/v1/messages</c> SSE.</summary>
    Anthropic,
}

/// <summary>How the fake provider hands out answers.</summary>
public enum ProviderGateMode
{
    /// <summary>Answers immediately; the server retires once the expected request count
    /// has been served.</summary>
    Scripted,

    /// <summary>Every provider request parks until the test releases it, so run lifetimes
    /// and queue transitions can be staged deterministically.</summary>
    Gated,

    /// <summary>Every provider request parks on one global release; after answering, the
    /// connection is held open and drained until either side closes.</summary>
    Hanging,
}

/// <summary>One OpenAI/Anthropic-compatible SSE fake provider for the whole suite,
/// unifying the gated, hanging, and scripted doubles. Every request is read in full
/// before it parks (a release can never land while the client is still sending), the
/// parked bookkeeping is signal-driven, and a parked request whose client vanished is
/// removed by its own connection task so it cannot starve the head of the release
/// queue.</summary>
public sealed class GatedModelProvider : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _serveLoop;
    private readonly Lock _gate = new();
    private readonly List<TaskCompletionSource> _parked = [];
    private readonly TaskCompletionSource _globalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _connections = [];
    private readonly ModelApiFlavor _flavor;
    private readonly ProviderGateMode _mode;
    private readonly string _answer;
    private readonly string _model;
    private readonly string? _reasoning;
    private readonly int _requestCount;
    private readonly bool _toolFlow;
    private readonly bool _applyPatchFlow;
    private readonly string? _applyPatchOperationJson;
    private readonly bool _webSearchFlow;
    private readonly int _webSearchCalls;
    private readonly string _toolName;
    private readonly string _toolArgumentsJson;
    private readonly string? _expectedToolResultText;
    private int _served;

    private int _connectionsOpened;
    private int _parkedCount;
    private readonly TaskCompletionSource _scriptedComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _parkedChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="flavor">The API flavor the fake speaks.</param>
    /// <param name="mode">How answers are handed out.</param>
    /// <param name="answer">The answer text embedded in the SSE stream.</param>
    /// <param name="model">The model the built-in request check expects.</param>
    /// <param name="reasoning">An optional private chat-completions reasoning delta or displayable
    /// Responses summary before the answer text.</param>
    /// <param name="requestCount">Scripted mode only: how many requests to serve before
    /// retiring; flow shapes default to their two-request round trip. Other modes serve
    /// until disposal.</param>
    /// <param name="toolFlow">Serve a tool-call round trip before the final answer.</param>
    /// <param name="toolName">Tool flow: the called function's name.</param>
    /// <param name="toolArgumentsJson">Tool flow: the called function's arguments JSON.</param>
    /// <param name="expectedToolResultText">Tool flow: a substring the tool-result request
    /// must carry.</param>
    /// <param name="applyPatchFlow">Responses only: serve an apply-patch call round
    /// trip.</param>
    /// <param name="applyPatchOperationJson">Apply-patch flow: the operation JSON.</param>
    /// <param name="webSearchFlow">Serve a server-executed web-search answer.</param>
    /// <param name="webSearchCalls">Web-search flow: how many search items to emit.</param>
    public GatedModelProvider(
        ModelApiFlavor flavor = ModelApiFlavor.ChatCompletions,
        ProviderGateMode mode = ProviderGateMode.Gated,
        string answer = "gated answer",
        string model = "test-model",
        string? reasoning = null,
        int? requestCount = null,
        bool toolFlow = false,
        string toolName = "echo",
        string toolArgumentsJson = "{\"text\":\"hello\"}",
        string? expectedToolResultText = null,
        bool applyPatchFlow = false,
        string? applyPatchOperationJson = null,
        bool webSearchFlow = false,
        int webSearchCalls = 1)
    {
        if (applyPatchFlow && applyPatchOperationJson is null)
            throw new ArgumentException("The apply-patch flow requires an operation JSON.", nameof(applyPatchOperationJson));

        _flavor = flavor;
        _mode = mode;
        _answer = answer;
        _model = model;
        _reasoning = reasoning;
        _toolFlow = toolFlow;
        _toolName = toolName;
        _toolArgumentsJson = toolArgumentsJson;
        _expectedToolResultText = expectedToolResultText;
        _applyPatchFlow = applyPatchFlow;
        _applyPatchOperationJson = applyPatchOperationJson;
        _webSearchFlow = webSearchFlow;
        _webSearchCalls = webSearchCalls;
        _requestCount = requestCount ?? (toolFlow || applyPatchFlow || webSearchFlow && flavor == ModelApiFlavor.Anthropic ? 2 : 1);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        // The OpenAI SDK joins relative to the /v1/ base; the Anthropic SDK joins to the
        // bare host, so the flavor decides the endpoint shape.
        Endpoint = flavor == ModelApiFlavor.Anthropic
            ? new Uri($"http://127.0.0.1:{endpoint.Port}")
            : new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
        _serveLoop = ServeAsync(_cancellation.Token);
    }

    /// <summary>The provider's base endpoint, for configuration injection.</summary>
    public Uri Endpoint { get; }

    /// <summary>Scripted mode: completes when every expected request has been answered.
    /// The other modes: the serve loop's task, completing at disposal.</summary>
    public Task Completion => _mode == ProviderGateMode.Scripted ? _scriptedComplete.Task : _serveLoop;

    /// <summary>The raw listen port, for tests that drive the TCP surface directly.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The bodies of the requests served so far, in arrival order.</summary>
    public ConcurrentQueue<JsonElement> RequestBodies { get; } = new();

    /// <summary>The single request body, for the one-request scenarios; throws when more
    /// or fewer than one request has been served.</summary>
    public JsonElement RequestBody => RequestBodies.Single();

    /// <summary>How many provider requests have arrived and are waiting for release.
    /// Gated mode only: Hanging-mode requests are counted once but never listed, because
    /// they wait on the global release instead of the per-request queue.</summary>
    public int ParkedRequests
    {
        get
        {
            lock (_gate)
            {
                return _parked.Count;
            }
        }
    }

    /// <summary>Waits until at least one provider request has arrived and is waiting for
    /// release.</summary>
    public Task WaitForParkedAsync(CancellationToken cancellationToken)
        => WaitForParkedAsync(1, cancellationToken);

    /// <summary>Waits until at least <paramref name="minimum" /> provider requests have
    /// arrived and are waiting for release. Signal-driven: every parked-count mutation
    /// (park, release, or a vanished client) completes a fresh source, so the wait needs
    /// no polling of <see cref="ParkedRequests" />.</summary>
    public async Task WaitForParkedAsync(int minimum, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task signal;
            lock (_gate)
            {
                if (_parked.Count >= minimum) return;

                signal = _parkedChanged.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until every parked provider request has left the gate (released, or
    /// its client vanished). Signal-driven like <see cref="WaitForParkedAsync(CancellationToken)" />;
    /// Gated mode only.</summary>
    public async Task WaitForParkedDrainedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task signal;
            lock (_gate)
            {
                if (_parked.Count == 0) return;

                signal = _parkedChanged.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Completes the parked-count signal and swaps in a fresh source, under the
    /// caller's hold of <see cref="_gate" />.</summary>
    private void NotifyParkedChangedLocked()
    {
        _parkedChanged.TrySetResult();
        _parkedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Releases the oldest provider request that is still awaiting release
    /// (Gated mode). The released entry leaves the parked list under the same lock that
    /// releases it, so the list holds exactly the requests still awaiting a release; a
    /// stale (already completed) head is skipped instead of silently consuming the
    /// release.</summary>
    public void ReleaseNext()
    {
        TaskCompletionSource? release = null;
        lock (_gate)
        {
            while (_parked.Count > 0)
            {
                var next = _parked[0];
                _parked.RemoveAt(0);
                if (next.Task.IsCompleted) continue;
                release = next;
                break;
            }

            if (release is not null) NotifyParkedChangedLocked();
        }

        release?.TrySetResult();
    }

    /// <summary>Releases every provider request currently awaiting release (Gated
    /// mode).</summary>
    public void ReleaseAll()
    {
        TaskCompletionSource[] releases;
        lock (_gate)
        {
            releases = [.. _parked];
            _parked.Clear();
            if (releases.Length > 0) NotifyParkedChangedLocked();
        }

        foreach (var release in releases) release.TrySetResult();
    }

    /// <summary>Hanging mode: releases every parked request at once through the global
    /// release.</summary>
    public void Release()
    {
        _globalRelease.TrySetResult();
    }

    /// <summary>Parks a synthetic request so the release bookkeeping can be exercised
    /// without a live socket. The returned source completes when a release reaches it.</summary>
    public TaskCompletionSource ParkForTest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _parked.Add(release);
            NotifyParkedChangedLocked();
        }

        return release;
    }

    /// <summary>Failure diagnostics: how many provider connections were accepted, how
    /// many reached the park point, and how the gate stands.</summary>
    public string DescribeState()
    {
        return $"connections={Volatile.Read(ref _connectionsOpened)} "
               + $"parkedTotal={Volatile.Read(ref _parkedCount)} parkedNow={ParkedRequests} "
               + $"released={(_mode == ProviderGateMode.Hanging ? _globalRelease.Task.IsCompleted.ToString() : "n/a")}";
    }

    /// <summary>Stops the listener, releases every parked request, and observes all
    /// connection and serve-loop tasks; each is observed in its own try so a faulting
    /// connection task can never leave the serve loop unobserved.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        ReleaseAll();
        _globalRelease.TrySetResult();
        _scriptedComplete.TrySetResult();
        Task[] connectionTasks;
        lock (_gate)
        {
            connectionTasks = [.. _connections];
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
            await _serveLoop.ConfigureAwait(false);
        }
        catch (Exception exception) when
            (exception is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
        }

        _cancellation.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Scripted mode retires once the expected request count has been served; the
            // other modes serve until disposal. The provider client's HttpClient pools
            // connections (keep-alive by default), so connections are served
            // concurrently, each staying open for reuse until the expected count is
            // reached.
            while (_mode != ProviderGateMode.Scripted || Volatile.Read(ref _served) < _requestCount)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                Task connection = ServeClientAsync(client, cancellationToken);
                lock (_gate)
                {
                    _connections.Add(connection);
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
            Interlocked.Increment(ref _connectionsOpened);
            while (!cancellationToken.IsCancellationRequested &&
                   (_mode != ProviderGateMode.Scripted || Volatile.Read(ref _served) < _requestCount))
            {
                // The request is read in full before the gate registers, so release order
                // matches request order and a release can never land while the client is
                // still sending (answering a partially-sent request resets the connection
                // under the client's response read).
                HttpRequest request;
                try
                {
                    request = await HttpRequestReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    // The client closed this connection (dispose, or a fresh connection
                    // for the next request).
                    return;
                }

                // Model discovery (the configured source's model listing): answered inline,
                // never parked, never counted toward the scripted request total, and not
                // recorded in RequestBodies (it carries no body).
                if (request.Method == "GET" && (request.Path == "/v1/models" || request.Path == "/models"))
                {
                    var body = Encoding.UTF8.GetBytes(
                        "{\"object\":\"list\",\"data\":[{\"id\":" + JsonSerializer.Serialize(_model) +
                        ",\"object\":\"model\",\"owned_by\":\"test\"}]}");
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n");
                    await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                RequestBodies.Enqueue(request.Body);
                Task wait;
                if (_mode == ProviderGateMode.Hanging)
                {
                    Interlocked.Increment(ref _parkedCount);
                    wait = _globalRelease.Task;
                }
                else if (_mode == ProviderGateMode.Gated)
                {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate)
                    {
                        _parked.Add(release);
                        Interlocked.Increment(ref _parkedCount);
                        NotifyParkedChangedLocked();
                    }

                    // A client that walks away while parked owns removing its own entry: a
                    // vanished connection can never observe its release, so its entry
                    // would sit at the head of the queue unreleasable and ReleaseNext
                    // would spend every pop on it while the live request starved behind.
                    using var parkedWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var disconnected = WaitDisconnectedAsync(client, parkedWait.Token);
                    var settled = await Task.WhenAny(release.Task, disconnected).ConfigureAwait(false);
                    await parkedWait.CancelAsync().ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_parked.Remove(release)) NotifyParkedChangedLocked();
                    }

                    if (cancellationToken.IsCancellationRequested || !ReferenceEquals(settled, release.Task)) return;

                    wait = release.Task;
                }
                else
                {
                    // The global served ordinal claims this request's index before the
                    // answer is selected, so a flow's two-step sequence stays ordered as
                    // long as its calls do.
                    var index = Interlocked.Increment(ref _served) - 1;
                    var validate = ValidateBuiltIn;
                    validate(request, index);
                    await WriteSseAsync(stream, SelectStream(index), cancellationToken).ConfigureAwait(false);
                    // The accept loop keeps waiting for further connections, so scripted
                    // retirement is signalled here, after the last expected answer is
                    // fully written. With concurrent connections the count is satisfied by
                    // the last claim while an earlier-index write may still be in flight —
                    // Completion means "all expected answers claimed and written by their
                    // own tasks", not a barrier across connections.
                    if (Volatile.Read(ref _served) >= _requestCount) _scriptedComplete.TrySetResult();
                    continue;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                await AnswerAsync(stream, cancellationToken).ConfigureAwait(false);
                if (_mode == ProviderGateMode.Hanging)
                {
                    // Hold the connection open (keep-alive, like a real HTTP server)
                    // instead of disposing it right after the response: the close races
                    // the client's response read. The drain ends at the client's EOF or
                    // server disposal.
                    var drain = new byte[4096];
                    while (await stream.ReadAsync(drain, cancellationToken).ConfigureAwait(false) > 0)
                    {
                    }
                }
            }
        }
        catch (Exception exception) when
            (exception is OperationCanceledException or EndOfStreamException or SocketException or IOException
                 or ObjectDisposedException or InvalidDataException or JsonException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>The built-in scripted-mode request checks, preserved from the scripted
    /// fakes: method, per-flavor path, model, streaming, store:false, and the tool round
    /// trip's shape. A violation throws; the faulted connection task surfaces through
    /// disposal instead of hanging the client.</summary>
    private void ValidateBuiltIn(HttpRequest request, int requestIndex)
    {
        var expectedPath = _flavor switch
        {
            ModelApiFlavor.Responses => "/v1/responses",
            ModelApiFlavor.ChatCompletions => "/v1/chat/completions",
            ModelApiFlavor.Anthropic => "/v1/messages",
            _ => throw new InvalidOperationException($"Unsupported test API flavor '{_flavor}'."),
        };
        if (request.Method != "POST" || request.Path != expectedPath)
            throw new InvalidOperationException(
                $"the provider expected POST {expectedPath}, got {request.Method} {request.Path}");
        if (request.Body.GetProperty("model").GetString() != _model)
            throw new InvalidOperationException(
                $"the provider expected model '{_model}', got '{request.Body.GetProperty("model").GetString()}'");
        if (!request.Body.GetProperty("stream").GetBoolean())
            throw new InvalidOperationException("the provider request did not ask to stream.");
        if (_flavor != ModelApiFlavor.Anthropic &&
            (!request.Body.TryGetProperty("store", out var store) || store.GetBoolean()))
        {
            // The product disables stored output on every OpenAI path, so the field is
            // always on the wire; an omitted field would let the Responses API default
            // to storing server-side — the regression the strict check catches.
            throw new InvalidOperationException("the provider request must ask store:false explicitly.");
        }

        if (!_toolFlow) return;

        if (request.Body.GetProperty("tools").GetArrayLength() == 0)
            throw new InvalidOperationException("the tool flow expected tools in the request.");
        if (!request.Body.GetRawText().Contains(_toolName, StringComparison.Ordinal))
            throw new InvalidOperationException($"the tool flow expected tool '{_toolName}' in the request.");
        if (requestIndex == 0) return;

        if (!request.Body.GetRawText().Contains("status", StringComparison.Ordinal) ||
            !request.Body.GetRawText().Contains("ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "the tool round trip's second request must carry the tool result with status ok.");
        }

        if (_expectedToolResultText is { } expected &&
            !request.Body.GetRawText().Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"the tool round trip's second request must carry '{expected}'.");
        }
    }

    /// <summary>Selects the answer stream for scripted request <paramref name="index" />:
    /// a flow's first request serves the call (tool, web search, apply patch) and its
    /// second the final answer.</summary>
    private string SelectStream(int index) => (flavor: _flavor, index) switch
    {
        (ModelApiFlavor.Responses, 0) when _toolFlow =>
            ModelStreams.CreateResponsesToolStream(_toolName, _toolArgumentsJson),
        (ModelApiFlavor.Responses, 0) when _webSearchFlow =>
            ModelStreams.CreateResponsesWebSearchStream(_webSearchCalls),
        (ModelApiFlavor.Responses, 0) when _applyPatchFlow && _applyPatchOperationJson is { } operation =>
            ModelStreams.CreateResponsesApplyPatchStream(operation),
        (ModelApiFlavor.Responses, _) =>
            ModelStreams.CreateResponsesStream(_toolFlow ? "tool-backed answer" : _answer, _reasoning),
        (ModelApiFlavor.ChatCompletions, 0) when _toolFlow =>
            ModelStreams.CreateChatCompletionsToolStream(_toolName, _toolArgumentsJson),
        (ModelApiFlavor.ChatCompletions, _) =>
            ModelStreams.CreateChatCompletionsStream(_toolFlow ? "tool-backed answer" : _answer, _reasoning),
        (ModelApiFlavor.Anthropic, 0) when _toolFlow => ModelStreams.CreateToolStream(),
        (ModelApiFlavor.Anthropic, 0) when _webSearchFlow => ModelStreams.CreateWebSearchStream(),
        (ModelApiFlavor.Anthropic, _) => ModelStreams.CreateTextStream(_answer),
        _ => throw new InvalidOperationException($"Unsupported test API flavor '{_flavor}'."),
    };

    private async Task AnswerAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(_answer) + "}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes one SSE response with keep-alive (no connection close), so the
    /// client's pooled connection can carry the next request.</summary>
    private static async Task WriteSseAsync(NetworkStream stream, string data, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(data);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes when the client closes its end (EOF) or the server shuts down: a
    /// parked request whose connection is gone can never observe a release, so its gate
    /// entry must be removed instead of blocking the head of the queue. A peek receive
    /// completes on the socket state change itself — the peer sends nothing while its
    /// request is parked, so the wait needs no timer; a reset is a disconnect like an
    /// EOF.</summary>
    private static async Task WaitDisconnectedAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            while (true)
            {
                var received = await client.Client.ReceiveAsync(
                    buffer,
                    SocketFlags.Peek,
                    cancellationToken).ConfigureAwait(false);
                if (received == 0) return;
            }
        }
        catch (SocketException)
        {
        }
    }
}

/// <summary>One fully read provider request: the request line, the header dictionary, and
/// the JSON body clone.</summary>
// The header dictionary is preserved so the fake request mirrors the wire's shape even
// where current assertions only read method, path, and body.
// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record HttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    JsonElement Body);
// ReSharper restore NotAccessedPositionalProperty.Global

/// <summary>The single HTTP/1.1 request reader for the fake providers: Content-Length
/// framing with the header dictionary preserved and the body parsed as JSON. A request
/// larger than <see cref="MaxRequestBytes" /> fails fast instead of growing the buffer
/// without bound.</summary>
public static class HttpRequestReader
{
    /// <summary>The request cap: a well-formed provider request is a few KiB, so anything
    /// past this is garbage from a broken or hostile client.</summary>
    private const int MaxRequestBytes = 1024 * 1024;

    /// <summary>Reads one request in full; throws <see cref="EndOfStreamException" /> when
    /// the connection closes mid-request.</summary>
    public static async Task<HttpRequest> ReadAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var request = new MemoryStream();
        var headerLength = -1;
        var contentLength = 0;

        while (headerLength < 0 || request.Length < headerLength + contentLength)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException("The provider request ended before its body was complete.");

            request.Write(buffer, 0, count);
            if (request.Length > MaxRequestBytes)
                throw new InvalidDataException($"The provider request exceeded {MaxRequestBytes} bytes.");

            if (headerLength >= 0) continue;

            var bytes = request.GetBuffer().AsSpan(0, checked((int)request.Length));
            var delimiter = "\r\n\r\n"u8;
            var delimiterIndex = bytes.IndexOf(delimiter);
            if (delimiterIndex < 0) continue;

            headerLength = delimiterIndex + delimiter.Length;
            var headers = Encoding.ASCII.GetString(bytes[..delimiterIndex]);
            contentLength = ParseContentLength(headers);
        }

        var requestBytes = request.GetBuffer().AsSpan(0, checked((int)request.Length));
        var requestLineEnd = requestBytes.IndexOf("\r\n"u8);
        if (requestLineEnd < 0)
            throw new InvalidDataException("The provider request did not contain a request line.");

        var requestLine = Encoding.ASCII.GetString(requestBytes[..requestLineEnd]);
        var requestLineParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLineParts.Length != 3)
            throw new InvalidDataException($"Invalid provider request line: '{requestLine}'.");

        var headerSpan = Encoding.ASCII.GetString(
            request.GetBuffer(),
            requestLineEnd + 2,
            headerLength - requestLineEnd - 4);
        var headerDictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in headerSpan.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            headerDictionary[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var bodyBytes = request.GetBuffer().AsMemory(headerLength, contentLength);
        // A bodyless request (model discovery's GET) has no JSON to parse; its Body is an
        // undefined element no consumer reads.
        var body = contentLength == 0
            ? default
            : JsonDocument.Parse(bodyBytes).RootElement.Clone();
        return new HttpRequest(requestLineParts[0], requestLineParts[1], headerDictionary, body);
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                return int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);

        return 0;
    }
}
