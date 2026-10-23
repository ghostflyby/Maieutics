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
    private readonly CancellationTokenSource cancellation = new();
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Task serveLoop;
    private readonly Lock gate = new();
    private readonly List<TaskCompletionSource> parked = [];
    private readonly TaskCompletionSource globalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> connections = [];
    private readonly ModelApiFlavor flavor;
    private readonly ProviderGateMode mode;
    private readonly string answer;
    private readonly string model;
    private readonly string? reasoning;
    private readonly int requestCount;
    private readonly bool toolFlow;
    private readonly bool applyPatchFlow;
    private readonly string? applyPatchOperationJson;
    private readonly bool webSearchFlow;
    private readonly int webSearchCalls;
    private readonly string toolName;
    private readonly string toolArgumentsJson;
    private readonly string? expectedToolResultText;
    private int served;

    private int connectionsOpened;
    private int parkedCount;
    private TaskCompletionSource parkedChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="flavor">The API flavor the fake speaks.</param>
    /// <param name="mode">How answers are handed out.</param>
    /// <param name="answer">The answer text embedded in the SSE stream.</param>
    /// <param name="model">The model the built-in request check expects.</param>
    /// <param name="reasoning">Chat-completions only: an optional reasoning-content delta
    /// before the answer text.</param>
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
        int webSearchCalls = 1,
        Action<HttpRequest, int>? validateRequest = null)
    {
        this.flavor = flavor;
        this.mode = mode;
        this.answer = answer;
        this.model = model;
        this.reasoning = reasoning;
        this.toolFlow = toolFlow;
        this.toolName = toolName;
        this.toolArgumentsJson = toolArgumentsJson;
        this.expectedToolResultText = expectedToolResultText;
        this.applyPatchFlow = applyPatchFlow;
        this.applyPatchOperationJson = applyPatchOperationJson;
        this.webSearchFlow = webSearchFlow;
        this.webSearchCalls = webSearchCalls;
        this.requestCount = requestCount ?? (toolFlow || applyPatchFlow || webSearchFlow && flavor == ModelApiFlavor.Anthropic ? 2 : 1);
        ValidateRequest = validateRequest;
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
        serveLoop = ServeAsync(cancellation.Token);
    }

    /// <summary>Optional per-request validation, invoked with the request and its global
    /// served ordinal before the answer is selected; a throw fails the connection task
    /// and surfaces through <see cref="Completion" /> instead of hanging the client.</summary>
    public Action<HttpRequest, int>? ValidateRequest { get; init; }

    /// <summary>The provider's base endpoint, for configuration injection.</summary>
    public Uri Endpoint { get; }

    /// <summary>The serve loop's task: scripted retires once the expected request count
    /// has been served; the other modes complete at disposal.</summary>
    public Task Completion => serveLoop;

    /// <summary>The raw listen port, for tests that drive the TCP surface directly.</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>The bodies of the requests served so far, in arrival order.</summary>
    public ConcurrentQueue<JsonElement> RequestBodies { get; } = new();

    /// <summary>How many provider requests have arrived and are waiting for release.
    /// Gated mode only: Hanging-mode requests are counted once but never listed, because
    /// they wait on the global release instead of the per-request queue.</summary>
    public int ParkedRequests
    {
        get
        {
            lock (gate)
            {
                return parked.Count;
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
            lock (gate)
            {
                if (parked.Count >= minimum) return;

                signal = parkedChanged.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until every parked provider request has left the gate (released, or
    /// its client vanished). Signal-driven like <see cref="WaitForParkedAsync" />; Gated
    /// mode only.</summary>
    public async Task WaitForParkedDrainedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task signal;
            lock (gate)
            {
                if (parked.Count == 0) return;

                signal = parkedChanged.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Completes the parked-count signal and swaps in a fresh source, under the
    /// caller's hold of <see cref="gate" />.</summary>
    private void NotifyParkedChangedLocked()
    {
        parkedChanged.TrySetResult();
        parkedChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Releases the oldest provider request that is still awaiting release
    /// (Gated mode). The released entry leaves the parked list under the same lock that
    /// releases it, so the list holds exactly the requests still awaiting a release; a
    /// stale (already completed) head is skipped instead of silently consuming the
    /// release.</summary>
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

            if (release is not null) NotifyParkedChangedLocked();
        }

        release?.TrySetResult();
    }

    /// <summary>Releases every provider request currently awaiting release (Gated
    /// mode).</summary>
    public void ReleaseAll()
    {
        TaskCompletionSource[] releases;
        lock (gate)
        {
            releases = [.. parked];
            parked.Clear();
            if (releases.Length > 0) NotifyParkedChangedLocked();
        }

        foreach (var release in releases) release.TrySetResult();
    }

    /// <summary>Hanging mode: releases every parked request at once through the global
    /// release.</summary>
    public void Release()
    {
        globalRelease.TrySetResult();
    }

    /// <summary>Parks a synthetic request so the release bookkeeping can be exercised
    /// without a live socket. The returned source completes when a release reaches it.</summary>
    public TaskCompletionSource ParkForTest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            parked.Add(release);
            NotifyParkedChangedLocked();
        }

        return release;
    }

    /// <summary>Failure diagnostics: how many provider connections were accepted, how
    /// many reached the park point, and how the gate stands.</summary>
    public string DescribeState()
    {
        return $"connections={Volatile.Read(ref connectionsOpened)} "
            + $"parkedTotal={Volatile.Read(ref parkedCount)} parkedNow={ParkedRequests} "
            + $"released={(mode == ProviderGateMode.Hanging ? globalRelease.Task.IsCompleted.ToString() : "n/a")}";
    }

    /// <summary>Stops the listener, releases every parked request, and observes all
    /// connection and serve-loop tasks; each is observed in its own try so a faulting
    /// connection task can never leave the serve loop unobserved.</summary>
    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        ReleaseAll();
        globalRelease.TrySetResult();
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
            // Scripted mode retires once the expected request count has been served; the
            // other modes serve until disposal. The provider client's HttpClient pools
            // connections (keep-alive by default), so connections are served
            // concurrently, each staying open for reuse until the expected count is
            // reached.
            while (mode != ProviderGateMode.Scripted || Volatile.Read(ref served) < requestCount)
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
            Interlocked.Increment(ref connectionsOpened);
            while (!cancellationToken.IsCancellationRequested &&
                   (mode != ProviderGateMode.Scripted || Volatile.Read(ref served) < requestCount))
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

                RequestBodies.Enqueue(request.Body);
                Task wait;
                if (mode == ProviderGateMode.Hanging)
                {
                    Interlocked.Increment(ref parkedCount);
                    wait = globalRelease.Task;
                }
                else if (mode == ProviderGateMode.Gated)
                {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (gate)
                    {
                        parked.Add(release);
                        Interlocked.Increment(ref parkedCount);
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
                    lock (gate)
                    {
                        if (parked.Remove(release)) NotifyParkedChangedLocked();
                    }

                    if (cancellationToken.IsCancellationRequested || !ReferenceEquals(settled, release.Task)) return;

                    wait = release.Task;
                }
                else
                {
                    // The global served ordinal claims this request's index before the
                    // answer is selected, so a flow's two-step sequence stays ordered as
                    // long as its calls do.
                    var index = Interlocked.Increment(ref served) - 1;
                    ValidateRequest?.Invoke(request, index);
                    await WriteSseAsync(stream, SelectStream(index), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
                await AnswerAsync(stream, cancellationToken).ConfigureAwait(false);
                if (mode == ProviderGateMode.Hanging)
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

    /// <summary>Selects the answer stream for scripted request <paramref name="index" />:
    /// a flow's first request serves the call (tool, web search, apply patch) and its
    /// second the final answer.</summary>
    private string SelectStream(int index) => (flavor, index) switch
    {
        (ModelApiFlavor.Responses, 0) when toolFlow =>
            ModelStreams.CreateResponsesToolStream(toolName, toolArgumentsJson),
        (ModelApiFlavor.Responses, 0) when webSearchFlow =>
            ModelStreams.CreateResponsesWebSearchStream(webSearchCalls),
        (ModelApiFlavor.Responses, 0) when applyPatchFlow && applyPatchOperationJson is { } operation =>
            ModelStreams.CreateResponsesApplyPatchStream(operation),
        (ModelApiFlavor.Responses, _) =>
            ModelStreams.CreateResponsesStream(toolFlow ? "tool-backed answer" : answer),
        (ModelApiFlavor.ChatCompletions, 0) when toolFlow =>
            ModelStreams.CreateChatCompletionsToolStream(toolName, toolArgumentsJson),
        (ModelApiFlavor.ChatCompletions, _) =>
            ModelStreams.CreateChatCompletionsStream(toolFlow ? "tool-backed answer" : answer, reasoning),
        (ModelApiFlavor.Anthropic, 0) when toolFlow => ModelStreams.CreateToolStream(),
        (ModelApiFlavor.Anthropic, 0) when webSearchFlow => ModelStreams.CreateWebSearchStream(),
        (ModelApiFlavor.Anthropic, _) => ModelStreams.CreateTextStream(answer),
        _ => throw new InvalidOperationException($"Unsupported test API flavor '{flavor}'."),
    };

    private async Task AnswerAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(answer) + "}}]}\n\n" +
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
    /// entry must be removed instead of blocking the head of the queue.</summary>
    private static async Task WaitDisconnectedAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var socket = client.Client;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0) return;
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>One fully read provider request: the request line, the header dictionary, and
/// the JSON body clone.</summary>
public sealed record HttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    JsonElement Body);

/// <summary>The single HTTP/1.1 request reader for the fake providers: Content-Length
/// framing with the header dictionary preserved and the body parsed as JSON. A request
/// larger than <see cref="MaxRequestBytes" /> fails fast instead of growing the buffer
/// without bound.</summary>
public static class HttpRequestReader
{
    /// <summary>The request cap: a well-formed provider request is a few KiB, so anything
    /// past this is garbage from a broken or hostile client.</summary>
    public const int MaxRequestBytes = 1024 * 1024;

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
        var body = JsonDocument.Parse(bodyBytes).RootElement.Clone();
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
