using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Maieutics.Control;

/// <summary>
/// Owns the single writer for one control-bus WebSocket connection.
/// </summary>
internal sealed class SessionBusConnection : IAsyncDisposable
{
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<OutgoingMessage> outgoing;
    private readonly Lock stateLock = new();
    private readonly WebSocket socket;
    private readonly Task writer;
    private Task? closeTask;
    private int closeState;
    private Exception? terminalError;

    /// <summary>Gets the open-comm registry this connection owns. Scoping it to the
    /// connection instance keeps a replaced connection's teardown from deleting a
    /// successor's freshly registered comm ids.</summary>
    internal ConcurrentDictionary<string, byte> OpenComms { get; } =
        new(StringComparer.Ordinal);

    internal SessionBusConnection(WebSocket socket)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        outgoing = Channel.CreateBounded<OutgoingMessage>(new BoundedChannelOptions(
            ReplControlLimits.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        writer = RunWriterAsync();
    }

    internal WebSocketState State => socket.State;

    internal Task Completion => completion.Task;

    internal Task SendAsync(ReplEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return SendAsync(ReplControlJson.Serialize(envelope), cancellationToken);
    }

    internal async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > ReplControlLimits.MaximumInboundMessageBytes)
            throw new InvalidOperationException("The control message exceeds the maximum message size.");

        ThrowIfUnavailable();
        var message = new OutgoingMessage(
            payload,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await outgoing.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            ThrowIfUnavailable();
            throw;
        }

        await message.Sent.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string statusDescription,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statusDescription);
        lock (stateLock)
        {
            if (closeTask is not null) return closeTask;
            Interlocked.Exchange(ref closeState, 1);
            closeTask = CloseCoreAsync(closeStatus, statusDescription, cancellationToken);
            return closeTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Disposal must stay deterministic even when the writer is stuck on a socket
        // send: the budget bounds the graceful drain, and its cancellation (registered
        // in CloseCoreAsync) then releases the writer before the close frame goes out.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
        }
    }

    private async Task RunWriterAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var message in outgoing.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await socket.SendAsync(
                        message.Payload,
                        WebSocketMessageType.Text,
                        true,
                        lifetime.Token).ConfigureAwait(false);
                    message.Sent.TrySetResult();
                }
                catch (Exception exception)
                {
                    message.Sent.TrySetException(exception);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            failure = terminalError;
        }
        catch (Exception exception)
        {
            failure = exception;
            terminalError = exception;
        }
        finally
        {
            outgoing.Writer.TryComplete(failure);
            while (outgoing.Reader.TryRead(out var pending))
                pending.Sent.TrySetException(
                    failure ?? new ObjectDisposedException(nameof(SessionBusConnection)));

            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }

    private async Task CloseCoreAsync(
        WebSocketCloseStatus closeStatus,
        string statusDescription,
        CancellationToken cancellationToken)
    {
        var cancellation = cancellationToken.Register(static state =>
        {
            if (state is CancellationTokenSource source) source.Cancel();
        }, lifetime);
        outgoing.Writer.TryComplete();
        try
        {
            await writer.ConfigureAwait(false);
        }
        catch
        {
            // The writer completion is exposed through Completion; close still releases the socket.
        }
        finally
        {
            lifetime.Cancel();
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                // One total budget: the close write rides the caller's token (the
                // disposal budget), not a second stacked timeout.
                await socket.CloseOutputAsync(
                    closeStatus,
                    statusDescription,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when
                (exception is WebSocketException or OperationCanceledException or InvalidOperationException)
            {
                // The peer may have closed concurrently, or the close write overran
                // its budget on a dead socket.
            }
        }

        // Unregister the budget callback before disposing the lifetime CTS: a budget
        // expiry in this window would otherwise Cancel a disposed source.
        await cancellation.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref closeState) != 0 || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("The control WebSocket is not available.");
    }

    private sealed record OutgoingMessage(
        byte[] Payload,
        TaskCompletionSource Sent);
}
