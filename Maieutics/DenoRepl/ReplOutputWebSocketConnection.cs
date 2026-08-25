using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Maieutics.Control;

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
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, long> lastSequences = new(StringComparer.Ordinal);
    private readonly Channel<ReplOutputFrame> frames;
    private readonly Lock stateLock = new();
    private readonly WebSocket socket;
    private Exception? terminalError;
    private int terminalState;
    private int disposeState;
    private int startState;
    private int enumerationState;
    private Task ownerTask = Task.CompletedTask;
    private CancellationTokenRegistration ownerCancellation;

    internal ReplOutputWebSocketConnection(WebSocket socket)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        frames = Channel.CreateBounded<ReplOutputFrame>(new BoundedChannelOptions(ReplOutputProtocol.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Completes when the connection ends; faults when the channel terminates abnormally.</summary>
    internal Task Completion => completion.Task;

    /// <summary>Consumes decoded output frames in arrival order. Single-consumer.</summary>
    internal IAsyncEnumerable<ReplOutputFrame> Events => ReadEventsAsync();

    /// <summary>Starts the background receive loop. Returns immediately; the loop owns the socket
    /// until the peer closes it or the host disposes the connection.</summary>
    internal void Start(CancellationToken ownerToken)
    {
        if (Interlocked.Exchange(ref startState, 1) != 0)
            throw new InvalidOperationException("The REPL output connection is already started.");

        ownerCancellation = ownerToken.UnsafeRegister(
            static state =>
            {
                if (state is ReplOutputWebSocketConnection connection)
                    connection.Terminate(
                        new OperationCanceledException("The REPL output connection owner stopped."));
            },
            this);
        ownerTask = RunOwnerAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) == 0)
            Terminate(new ObjectDisposedException(nameof(ReplOutputWebSocketConnection)));

        await ownerTask.ConfigureAwait(false);
    }

    internal Task WaitForTerminationAsync()
    {
        return ownerTask;
    }

    private async Task RunOwnerAsync()
    {
        try
        {
            await ReceiveAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
        }
        finally
        {
            lock (stateLock)
            {
                if (Interlocked.CompareExchange(ref terminalState, 1, 0) == 0)
                    terminalError = new IOException("The REPL output WebSocket ended unexpectedly.");
            }

            frames.Writer.TryComplete(terminalError);
            lifetime.Cancel();
            ownerCancellation.Dispose();

            if (terminalError is null)
                completion.TrySetResult();
            else
                completion.TrySetException(terminalError);

            lifetime.Dispose();
        }
    }

    private async Task ReceiveAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            var frame = await ReplOutputFrameReader.ReadAsync(socket, lifetime.Token).ConfigureAwait(false);
            if (frame is null)
            {
                Terminate(null);
                return;
            }

            ValidateSequence(frame);
            await frames.Writer.WriteAsync(frame, lifetime.Token).ConfigureAwait(false);
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

        var previous = lastSequences.AddOrUpdate(
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
        if (Interlocked.Exchange(ref enumerationState, 1) != 0)
            throw new InvalidOperationException("REPL output events are single-consumer.");
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return frame;
        }
        finally
        {
            // The connection is a session-lifetime stream carrying every execution's frames; each
            // execution's collector enumerates its own window (bounded by its eval terminal) and
            // releases the single-concurrent-consumer guard when the window ends. Executions are
            // serialized per session, so the guard still prevents concurrent readers.
            Volatile.Write(ref enumerationState, 0);
        }
    }

    private void Terminate(Exception? exception)
    {
        if (Interlocked.CompareExchange(ref terminalState, 1, 0) != 0) return;
        terminalError = exception;
        lifetime.Cancel();
    }
}
