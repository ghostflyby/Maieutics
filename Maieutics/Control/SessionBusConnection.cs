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

    public ValueTask DisposeAsync()
    {
        return new ValueTask(CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "closed",
            CancellationToken.None));
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
        using var cancellation = cancellationToken.Register(static state =>
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
                await socket.CloseOutputAsync(
                    closeStatus,
                    statusDescription,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // The peer may have closed concurrently.
            }
        }

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
