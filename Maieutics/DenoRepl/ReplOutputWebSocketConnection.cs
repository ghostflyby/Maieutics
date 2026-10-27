using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Maieutics.DenoRepl;

/// <summary>
///     Owns one REPL output WebSocket connection. The endpoint is half-duplex
///     (process -&gt; host only): a single receiver loop reads binary output
///     frames, enforces per-execution sequence order, and publishes them into a
///     bounded channel consumed through <see cref="Events" />. The connection
///     never writes to the socket; its completion is driven by the peer closing
///     the channel, a sequence violation, or host disposal.
/// </summary>
internal sealed class ReplOutputWebSocketConnection : IAsyncDisposable
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, long> _lastSequences = new(StringComparer.Ordinal);
    private readonly Channel<ReplOutputFrame> _frames;
    private readonly Lock _stateLock = new();
    private readonly WebSocket _socket;
    private Exception? _terminalError;
    private int _terminalState;
    private int _disposeState;
    private int _startState;
    private int _enumerationState;
    private Task _ownerTask = Task.CompletedTask;
    private CancellationTokenRegistration _ownerCancellation;

    internal ReplOutputWebSocketConnection(WebSocket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _frames = Channel.CreateBounded<ReplOutputFrame>(new BoundedChannelOptions(ReplOutputProtocol.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Completes when the connection ends; faults when the channel terminates abnormally.</summary>
    internal Task Completion => _completion.Task;

    /// <summary>Consumes decoded output frames in arrival order. Single-consumer.</summary>
    internal IAsyncEnumerable<ReplOutputFrame> Events => ReadEventsAsync();

    /// <summary>Starts the background receive loop. Returns immediately; the loop owns the socket
    /// until the peer closes it or the host disposes the connection.</summary>
    internal void Start(CancellationToken ownerToken)
    {
        if (Interlocked.Exchange(ref _startState, 1) != 0)
            throw new InvalidOperationException("The REPL output connection is already started.");

        _ownerCancellation = ownerToken.UnsafeRegister(
            static state =>
            {
                if (state is ReplOutputWebSocketConnection connection)
                    connection.Terminate(
                        new OperationCanceledException("The REPL output connection owner stopped."));
            },
            this);
        _ownerTask = RunOwnerAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            Terminate(new ObjectDisposedException(nameof(ReplOutputWebSocketConnection)));

        await _ownerTask.ConfigureAwait(false);
    }

    internal Task WaitForTerminationAsync()
    {
        return _ownerTask;
    }

    private async Task RunOwnerAsync()
    {
        try
        {
            await ReceiveAsync().ConfigureAwait(false);
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
            lock (_stateLock)
            {
                if (Interlocked.CompareExchange(ref _terminalState, 1, 0) == 0)
                    _terminalError = new IOException("The REPL output WebSocket ended unexpectedly.");
            }

            _frames.Writer.TryComplete(_terminalError);
            await _lifetime.CancelAsync();
            await _ownerCancellation.DisposeAsync();

            if (_terminalError is null)
                _completion.TrySetResult();
            else
                _completion.TrySetException(_terminalError);

            _lifetime.Dispose();
        }
    }

    private async Task ReceiveAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var frame = await ReplOutputFrameReader.ReadAsync(_socket, _lifetime.Token).ConfigureAwait(false);
            if (frame is null)
            {
                Terminate(null);
                return;
            }

            ValidateSequence(frame);
            await _frames.Writer.WriteAsync(frame, _lifetime.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Per-execution sequences must be strictly increasing from 1. Repeated, skipped, or
    /// out-of-order sequences terminate the connection (wire corruption).</summary>
    private void ValidateSequence(ReplOutputFrame frame)
    {
        if (frame.Seq <= 0 || (ulong)frame.Seq > ReplOutputProtocol.MaximumSafeSequence)
            throw new ReplOutputProtocolException(
                "invalid_sequence",
                $"The REPL output frame sequence '{frame.Seq}' for execution '{frame.ExecutionId}' must be a positive safe integer.");

        var previous = _lastSequences.AddOrUpdate(
            frame.ExecutionId,
            static _ => 1L,
            static (_, current) => current + 1);
        if (previous != frame.Seq)
            throw new ReplOutputProtocolException(
                "sequence_mismatch",
                $"The REPL output frame sequence '{frame.Seq}' for execution '{frame.ExecutionId}' is out of order (expected '{previous}').");
    }

    private async IAsyncEnumerable<ReplOutputFrame> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerationState, 1) != 0)
            throw new InvalidOperationException("REPL output events are single-consumer.");
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return frame;
        }
        finally
        {
            // The connection is a session-lifetime stream carrying every execution's frames; each
            // execution's collector enumerates its own window (bounded by its eval terminal) and
            // releases the single-concurrent-consumer guard when the window ends. Executions are
            // serialized per session, so the guard still prevents concurrent readers.
            Volatile.Write(ref _enumerationState, 0);
        }
    }

    private void Terminate(Exception? exception)
    {
        if (Interlocked.CompareExchange(ref _terminalState, 1, 0) != 0) return;
        _terminalError = exception;
        _lifetime.Cancel();
    }
}
