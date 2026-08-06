using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Maieutics.Jupyter.Client.Transport;

namespace Maieutics.Jupyter.Client.Protocol;

internal sealed class AsyncEventHub<T>(int capacity)
{
    private readonly Lock gate = new();
    private readonly Dictionary<long, Channel<T>> subscribers = [];
    private bool completed;
    private Exception? completionError;
    private long nextSubscriberId;

    public IAsyncEnumerable<T> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        long subscriberId = -1;

        lock (gate)
        {
            if (completed)
            {
                channel.Writer.TryComplete(completionError);
            }
            else
            {
                subscriberId = nextSubscriberId++;
                subscribers[subscriberId] = channel;
            }
        }

        return ReadSubscriberAsync(subscriberId, channel, cancellationToken);
    }

    private async IAsyncEnumerable<T> ReadSubscriberAsync(
        long subscriberId,
        Channel<T> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            if (subscriberId >= 0)
                lock (gate)
                {
                    subscribers.Remove(subscriberId);
                }
        }
    }

    public void Publish(T item)
    {
        KeyValuePair<long, Channel<T>>[] snapshot;
        lock (gate)
        {
            if (completed) return;

            snapshot = subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            if (subscriber.Value.Writer.TryWrite(item)) continue;

            subscriber.Value.Writer.TryComplete(new JupyterBackpressureException(
                "A Jupyter client event subscriber stopped consuming events."));
            lock (gate)
            {
                subscribers.Remove(subscriber.Key);
            }
        }
    }

    public void Complete(Exception? error = null)
    {
        Channel<T>[] snapshot;
        lock (gate)
        {
            if (completed) return;

            completed = true;
            completionError = error;
            snapshot = subscribers.Values.ToArray();
            subscribers.Clear();
        }

        foreach (var subscriber in snapshot) subscriber.Writer.TryComplete(error);
    }
}