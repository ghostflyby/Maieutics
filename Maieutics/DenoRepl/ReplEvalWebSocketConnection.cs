using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Maieutics.Control;

namespace Maieutics.DenoRepl;

internal interface IDenoReplConnection
{
    Task Completion { get; }

    Task<ReplEvalExecution> ExecuteAsync(string code, CancellationToken cancellationToken = default);

    Task CancelAsync(string executionId, CancellationToken cancellationToken = default);

    Task ReplyInputAsync(
        ReplEvalInputRequestEvent request,
        string value,
        CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

internal sealed class ReplEvalWebSocketConnection : IDenoReplConnection, IAsyncDisposable
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<OutgoingMessage> _outgoing;
    private readonly Dictionary<string, string> _pendingInputRequests = new(StringComparer.Ordinal);
    private readonly Lock _stateLock = new();
    private readonly WebSocket _socket;
    private readonly string _readyCorrelationId;
    private ExecutionState? _activeExecution;
    private int _disposeState;
    private Task _ownerTask = Task.CompletedTask;
    private CancellationTokenRegistration _ownerCancellation;
    private string? _lastCompletedExecutionId;
    private ShutdownState? _shutdown;
    private int _shutdownAcknowledged;
    private int _startState;
    private Exception? _terminalError;
    private int _terminalState;

    internal ReplEvalWebSocketConnection(
        WebSocket socket,
        ReplEvalIdentity identity,
        string readyCorrelationId)
    {
        this._socket = socket ?? throw new ArgumentNullException(nameof(socket));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentException.ThrowIfNullOrWhiteSpace(readyCorrelationId);
        this._readyCorrelationId = readyCorrelationId;
        _outgoing = Channel.CreateBounded<OutgoingMessage>(new BoundedChannelOptions(ReplEvalProtocol.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    internal ReplEvalIdentity Identity { get; }

    public Task Completion => _completion.Task;

    internal async Task StartAsync(CancellationToken ownerToken)
    {
        if (Interlocked.Exchange(ref _startState, 1) != 0)
            throw new InvalidOperationException("The REPL eval connection is already started.");

        _ownerCancellation = ownerToken.UnsafeRegister(
            static state =>
            {
                if (state is ReplEvalWebSocketConnection connection)
                    connection.Terminate(
                        new OperationCanceledException("The REPL eval connection owner stopped."));
            },
            this);
        _ownerTask = RunOwnerAsync();
        try
        {
            var publicIdentity = Identity with { Credential = null };
            await SendAsync(
                CreateEnvelope(
                    ReplEvalMessageType.Ready,
                    _readyCorrelationId,
                    ReplEvalProtocol.Payload(publicIdentity, ReplEvalJsonContext.Default.ReplEvalIdentity)),
                ownerToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ReplEvalExecution> ExecuteAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();

        var executionId = Guid.NewGuid().ToString("D");
        ExecutionState state;
        lock (_stateLock)
        {
            if (_activeExecution is not null)
                throw new InvalidOperationException(
                    $"Execution '{_activeExecution.ExecutionId}' is still active.");
            if (_shutdown is not null)
                throw new InvalidOperationException("The REPL eval connection is shutting down.");

            state = new ExecutionState(executionId);
            _activeExecution = state;
        }

        try
        {
            var payload = new ReplEvalExecutePayload(executionId, code);
            var send = SendAsync(
                CreateEnvelope(
                    ReplEvalMessageType.Execute,
                    executionId,
                    ReplEvalProtocol.Payload(payload, ReplEvalJsonContext.Default.ReplEvalExecutePayload)),
                CancellationToken.None);
            await send.WaitAsync(cancellationToken).ConfigureAwait(false);
            return state.Execution;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The command remains issued. Caller cancellation only stops waiting for the send acknowledgement.
            throw;
        }
        catch (Exception exception)
        {
            Terminate(exception);
            throw;
        }
    }

    public async Task CancelAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        cancellationToken.ThrowIfCancellationRequested();

        ExecutionState state;
        Task send;
        lock (_stateLock)
        {
            if (_activeExecution is not { } active ||
                !string.Equals(active.ExecutionId, executionId, StringComparison.Ordinal))
            {
                if (string.Equals(_lastCompletedExecutionId, executionId, StringComparison.Ordinal)) return;
                throw new InvalidOperationException($"Execution '{executionId}' is not active.");
            }

            state = active;
            send = active.CancelSend ??= SendAsync(
                CreateEnvelope(
                    ReplEvalMessageType.Cancel,
                    executionId,
                    ReplEvalProtocol.Payload(
                        new ReplEvalCancelPayload(executionId),
                        ReplEvalJsonContext.Default.ReplEvalCancelPayload)),
                CancellationToken.None);
        }

        await send.WaitAsync(cancellationToken).ConfigureAwait(false);
        await state.Execution.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplyInputAsync(
        ReplEvalInputRequestEvent request,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        bool shouldReply;
        lock (_stateLock)
        {
            if (_pendingInputRequests.TryGetValue(request.RequestId, out var executionId) &&
                string.Equals(executionId, request.ExecutionId, StringComparison.Ordinal))
            {
                _pendingInputRequests.Remove(request.RequestId);
                shouldReply = true;
            }
            else
            {
                // The execution may have already terminated: Deno can finish without
                // awaiting the input result, so the reply has no recipient anymore.
                shouldReply = string.Equals(_lastCompletedExecutionId, request.ExecutionId, StringComparison.Ordinal);
            }
        }

        if (!shouldReply)
            throw new InvalidOperationException($"Input request '{request.RequestId}' is not pending.");

        var payload = new ReplEvalInputReplyPayload(request.ExecutionId, request.RequestId, value);
        var send = SendAsync(
            CreateEnvelope(
                ReplEvalMessageType.InputReply,
                request.RequestId,
                ReplEvalProtocol.Payload(payload, ReplEvalJsonContext.Default.ReplEvalInputReplyPayload)),
            CancellationToken.None);
        await send.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        lock (_stateLock)
        {
            _shutdown ??= new ShutdownState(Guid.NewGuid().ToString("D"));
            task = _shutdown.Task ??= RunShutdownAsync(_shutdown);
        }

        return task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            Terminate(new ObjectDisposedException(nameof(ReplEvalWebSocketConnection)));

        await _ownerTask.ConfigureAwait(false);
    }

    internal Task WaitForTerminationAsync()
    {
        return _ownerTask;
    }

    internal static async Task<(ReplEvalIdentity Identity, string CorrelationId)> ReadHelloAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var text = await ReplControlMessageReader.ReadAsync(socket, cancellationToken).ConfigureAwait(false)
                   ?? throw new ReplEvalProtocolException(
                       "hello_missing",
                       "The REPL eval WebSocket closed before its hello message.");
        var envelope = ReplEvalProtocol.Deserialize(text);
        if (!string.Equals(envelope.Type, ReplEvalMessageType.Hello, StringComparison.Ordinal))
            throw new ReplEvalProtocolException(
                "hello_required",
                "The first REPL eval message must be repl.eval.hello.",
                envelope.CorrelationId);

        var identity = ReplEvalProtocol.ParsePayload(
            envelope,
            ReplEvalJsonContext.Default.ReplEvalIdentity);
        if (string.IsNullOrWhiteSpace(identity.SessionId) || identity.Generation < 0)
            throw new ReplEvalProtocolException(
                "invalid_identity",
                "The REPL eval hello requires a session id and non-negative generation.",
                envelope.CorrelationId);
        return (identity, envelope.CorrelationId);
    }

    private async Task RunOwnerAsync()
    {
        try
        {
            await Task.WhenAll(ReceiveAsync(), SendAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _terminalState, 1, 0) == 0)
                _terminalError = new IOException("The REPL eval WebSocket owner ended unexpectedly.");

            _outgoing.Writer.TryComplete(_terminalError);
            await _lifetime.CancelAsync();
            await _ownerCancellation.DisposeAsync();

            var failure = _terminalError ?? new ObjectDisposedException(nameof(ReplEvalWebSocketConnection));
            while (_outgoing.Reader.TryRead(out var message)) message.Sent.TrySetException(failure);
            FailPending(failure);

            if (_terminalError is null && Volatile.Read(ref _shutdownAcknowledged) != 0)
                _completion.TrySetResult();
            else
                _completion.TrySetException(failure);

            _lifetime.Dispose();
        }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var text = await ReplControlMessageReader.ReadAsync(_socket, _lifetime.Token).ConfigureAwait(false);
                if (text is null)
                {
                    if (Volatile.Read(ref _shutdownAcknowledged) != 0)
                        Terminate(null);
                    else
                        Terminate(new IOException("The REPL eval WebSocket closed unexpectedly."));
                    return;
                }

                await HandleEnvelopeAsync(ReplEvalProtocol.Deserialize(text)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
            throw;
        }
    }

    private async Task SendAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                try
                {
                    await _socket.SendAsync(
                        message.Bytes,
                        WebSocketMessageType.Text,
                        true,
                        _lifetime.Token).ConfigureAwait(false);
                    message.Sent.TrySetResult();
                }
                catch (Exception exception)
                {
                    message.Sent.TrySetException(exception);
                    throw;
                }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
            throw;
        }
    }

    private async Task SendAsync(ReplEvalEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        var message = new OutgoingMessage(
            ReplEvalProtocol.Serialize(envelope),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await _outgoing.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            ThrowIfUnavailable();
            throw;
        }

        await message.Sent.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleEnvelopeAsync(ReplEvalEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case ReplEvalMessageType.InputRequest:
                await HandleInputRequestAsync(envelope).ConfigureAwait(false);
                return;
            case ReplEvalMessageType.Result:
                HandleResult(envelope);
                return;
            case ReplEvalMessageType.Error:
                HandleError(envelope);
                return;
            case ReplEvalMessageType.Cancelled:
                HandleCancelled(envelope);
                return;
            default:
                throw new ReplEvalProtocolException(
                    "unexpected_message_type",
                    $"The REPL eval host cannot receive '{envelope.Type}'.",
                    envelope.CorrelationId);
        }
    }

    private ValueTask HandleInputRequestAsync(ReplEvalEnvelope envelope)
    {
        var payload = ReplEvalProtocol.ParsePayload(
            envelope,
            ReplEvalJsonContext.Default.ReplEvalInputRequestPayload);
        if (string.IsNullOrWhiteSpace(payload.RequestId) ||
            !string.Equals(envelope.CorrelationId, payload.RequestId, StringComparison.Ordinal))
            throw InvalidPayload(envelope, "Input request correlation must equal its request id.");

        var input = new ReplEvalInputRequestEvent(
            payload.ExecutionId,
            payload.Sequence,
            payload.RequestId,
            payload.Prompt,
            payload.Password);
        return PublishEventAsync(envelope, input, payload.RequestId);
    }

    private async ValueTask PublishEventAsync(
        ReplEvalEnvelope envelope,
        ReplEvalEvent replEvent,
        string? inputRequestId = null)
    {
        ExecutionState state;
        lock (_stateLock)
        {
            state = RequireActive(envelope, replEvent.ExecutionId, requireExecutionCorrelation: inputRequestId is null);
            // Arrival order is the single ordering authority: the Deno client forwards
            // output events through one FIFO pump, so no sequence validation is needed.
            if (inputRequestId is not null && !_pendingInputRequests.TryAdd(inputRequestId, state.ExecutionId))
                throw new ReplEvalProtocolException(
                    "duplicate_input_request",
                    $"Input request '{inputRequestId}' is already pending.",
                    envelope.CorrelationId);
        }

        await state.Events.Writer.WriteAsync(replEvent, _lifetime.Token).ConfigureAwait(false);
    }

    private void HandleResult(ReplEvalEnvelope envelope)
    {
        var payload = ReplEvalProtocol.ParsePayload(
            envelope,
            ReplEvalJsonContext.Default.ReplEvalResultPayload);
        if (payload.ExecutionId is { } executionId)
        {
            CompleteExecution(envelope, executionId, new ReplEvalResultTerminal(executionId, payload.Value));
            return;
        }

        ShutdownState state;
        lock (_stateLock)
        {
            state = _shutdown is { } pending &&
                    string.Equals(pending.CorrelationId, envelope.CorrelationId, StringComparison.Ordinal)
                ? pending
                : throw UnknownCorrelation(envelope);
        }

        Interlocked.Exchange(ref _shutdownAcknowledged, 1);
        state.Acknowledged.TrySetResult();
    }

    private void HandleError(ReplEvalEnvelope envelope)
    {
        var payload = ReplEvalProtocol.ParsePayload(
            envelope,
            ReplEvalJsonContext.Default.ReplEvalErrorPayload);
        if (string.IsNullOrWhiteSpace(payload.Code) || string.IsNullOrWhiteSpace(payload.Message))
            throw InvalidPayload(envelope, "Error code and message are required.");
        if (payload.ExecutionId is { } executionId)
        {
            CompleteExecution(
                envelope,
                executionId,
                new ReplEvalErrorTerminal(executionId, payload.Code, payload.Message, payload.Fatal is true));
            return;
        }

        lock (_stateLock)
        {
            if (_shutdown is { } pending &&
                string.Equals(pending.CorrelationId, envelope.CorrelationId, StringComparison.Ordinal))
            {
                pending.Acknowledged.TrySetException(
                    new ReplEvalProtocolException(payload.Code, payload.Message, envelope.CorrelationId));
                return;
            }
        }

        throw UnknownCorrelation(envelope);
    }

    private void HandleCancelled(ReplEvalEnvelope envelope)
    {
        var payload = ReplEvalProtocol.ParsePayload(
            envelope,
            ReplEvalJsonContext.Default.ReplEvalCancelledPayload);
        CompleteExecution(
            envelope,
            payload.ExecutionId,
            new ReplEvalCancelledTerminal(payload.ExecutionId));
    }

    private void CompleteExecution(
        ReplEvalEnvelope envelope,
        string executionId,
        ReplEvalTerminal terminal)
    {
        ExecutionState state;
        lock (_stateLock)
        {
            if (_activeExecution is not { } active ||
                !string.Equals(active.ExecutionId, executionId, StringComparison.Ordinal))
            {
                var code = string.Equals(_lastCompletedExecutionId, executionId, StringComparison.Ordinal)
                    ? "duplicate_terminal"
                    : "unknown_correlation";
                throw new ReplEvalProtocolException(
                    code,
                    $"Execution terminal '{executionId}' does not match the active execution.",
                    envelope.CorrelationId);
            }
            if (!string.Equals(envelope.CorrelationId, executionId, StringComparison.Ordinal))
                throw new ReplEvalProtocolException(
                    "correlation_mismatch",
                    "Execution terminal correlation must equal its execution id.",
                    envelope.CorrelationId);

            state = active;
            _activeExecution = null;
            _lastCompletedExecutionId = executionId;
            foreach (var request in _pendingInputRequests
                         .Where(pair => string.Equals(pair.Value, executionId, StringComparison.Ordinal))
                         .Select(static pair => pair.Key)
                         .ToArray())
                _pendingInputRequests.Remove(request);
        }

        state.Events.Writer.TryComplete();
        state.Completion.TrySetResult(terminal);
    }

    // The flag exists to opt specific envelope kinds into the correlation precondition —
    // per-callsite strictness is its whole use.
    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private ExecutionState RequireActive(
        ReplEvalEnvelope envelope,
        string executionId,
        bool requireExecutionCorrelation)
    {
        if (_activeExecution is not { } state ||
            !string.Equals(state.ExecutionId, executionId, StringComparison.Ordinal))
            throw UnknownCorrelation(envelope);
        if (requireExecutionCorrelation &&
            !string.Equals(envelope.CorrelationId, executionId, StringComparison.Ordinal))
            throw new ReplEvalProtocolException(
                "correlation_mismatch",
                "Execution event correlation must equal its execution id.",
                envelope.CorrelationId);
        return state;
    }

    private async Task RunShutdownAsync(ShutdownState state)
    {
        var publicIdentity = Identity with { Credential = null };
        var payload = ReplEvalProtocol.Payload(publicIdentity, ReplEvalJsonContext.Default.ReplEvalIdentity);
        await SendAsync(
            CreateEnvelope(ReplEvalMessageType.Dispose, state.CorrelationId, payload),
            CancellationToken.None).ConfigureAwait(false);
        await state.Acknowledged.Task.ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
    }

    private void FailPending(Exception failure)
    {
        ExecutionState? execution;
        ShutdownState? pendingShutdown;
        lock (_stateLock)
        {
            execution = _activeExecution;
            _activeExecution = null;
            _pendingInputRequests.Clear();
            pendingShutdown = _shutdown;
        }

        if (execution is not null)
        {
            execution.Events.Writer.TryComplete(failure);
            execution.Completion.TrySetException(failure);
        }
        pendingShutdown?.Acknowledged.TrySetException(failure);
    }

    private void Terminate(Exception? exception)
    {
        if (Interlocked.CompareExchange(ref _terminalState, 1, 0) != 0) return;
        _terminalError = exception;
        _outgoing.Writer.TryComplete(exception);
        _lifetime.Cancel();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (Volatile.Read(ref _terminalState) != 0)
            throw new IOException("The REPL eval WebSocket has terminated.", _terminalError);
        if (Volatile.Read(ref _startState) == 0)
            throw new InvalidOperationException("The REPL eval connection has not started.");
    }

    private static ReplEvalEnvelope CreateEnvelope(
        string type,
        string correlationId,
        JsonElement payload)
    {
        return new ReplEvalEnvelope(ReplEvalProtocol.Version, type, correlationId, payload);
    }

    private static ReplEvalProtocolException InvalidPayload(
        ReplEvalEnvelope envelope,
        string message)
    {
        return new ReplEvalProtocolException("invalid_payload", message, envelope.CorrelationId);
    }

    private static ReplEvalProtocolException UnknownCorrelation(ReplEvalEnvelope envelope)
    {
        return new ReplEvalProtocolException(
            "unknown_correlation",
            $"Message '{envelope.Type}' has no matching operation.",
            envelope.CorrelationId);
    }

    private sealed record OutgoingMessage(byte[] Bytes, TaskCompletionSource Sent);

    private sealed class ExecutionState
    {
        internal ExecutionState(string executionId)
        {
            ExecutionId = executionId;
            Events = Channel.CreateBounded<ReplEvalEvent>(new BoundedChannelOptions(ReplEvalProtocol.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            Completion = new TaskCompletionSource<ReplEvalTerminal>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Execution = new ReplEvalExecution(executionId, Events.Reader, Completion.Task);
        }

        internal string ExecutionId { get; }

        internal Channel<ReplEvalEvent> Events { get; }

        internal TaskCompletionSource<ReplEvalTerminal> Completion { get; }

        internal ReplEvalExecution Execution { get; }

        internal Task? CancelSend { get; set; }
    }

    private sealed class ShutdownState(string correlationId)
    {
        internal string CorrelationId { get; } = correlationId;

        internal TaskCompletionSource Acknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task? Task { get; set; }
    }
}

internal sealed class ReplEvalExecution
{
    private readonly ChannelReader<ReplEvalEvent> _events;
    private int _enumerationState;

    internal ReplEvalExecution(
        string executionId,
        ChannelReader<ReplEvalEvent> events,
        Task<ReplEvalTerminal> completion)
    {
        ExecutionId = executionId;
        this._events = events;
        Completion = completion;
    }

    internal string ExecutionId { get; }

    internal IAsyncEnumerable<ReplEvalEvent> Events => ReadEventsAsync();

    internal Task<ReplEvalTerminal> Completion { get; }

    private async IAsyncEnumerable<ReplEvalEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerationState, 1) != 0)
            throw new InvalidOperationException("REPL eval execution events are single-consumer.");

        await foreach (var replEvent in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return replEvent;
    }
}
