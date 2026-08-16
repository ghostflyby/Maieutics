using FluentAssertions;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Kernel.Transport;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

[Collection(JupyterSocketIntegrationCollection.Name)]
public sealed class JupyterTransportLifecycleTests
{
    [Fact(Timeout = 15_000)]
    public async Task CancelledConnectStopsIoLoopBeforeReturning()
    {
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var connection = JupyterConnectionInfo.CreateLocalTcp();

        await (Connection: connection, cancellation.Token)
            .Awaiting(static state => ZmqSharpJupyterTransport.ConnectAsync(
                state.Connection,
                cancellationToken: state.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = 15_000)]
    public async Task CancelledBindStopsIoLoopBeforeReturning()
    {
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var connection = JupyterConnectionInfo.CreateLocalTcp();

        await (Connection: connection, cancellation.Token)
            .Awaiting(static state => ZmqSharpJupyterKernelTransport.BindAsync(
                state.Connection,
                cancellationToken: state.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = 15_000)]
    public async Task ConcurrentKernelTransportDisposeCompletesOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var transport = await ZmqSharpJupyterKernelTransport.BindAsync(
            connection,
            cancellationToken: deadline.Token);

        await Task.WhenAll(transport.DisposeAsync().AsTask(), transport.DisposeAsync().AsTask())
            .WaitAsync(deadline.Token);
    }

    [Fact(Timeout = 15_000)]
    public async Task ConcurrentDisposeFailsPendingHeartbeat()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var transport = await ZmqSharpJupyterTransport.ConnectAsync(connection, cancellationToken: deadline.Token);
        var ping = transport.PingAsync(deadline.Token);

        await Task.WhenAll(transport.DisposeAsync().AsTask(), transport.DisposeAsync().AsTask())
            .WaitAsync(deadline.Token);

        await ping.Invoking(static task => task).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact(Timeout = 15_000)]
    public async Task DisposeFailsPendingStdinConnectionReadiness()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var transport = await ZmqSharpJupyterTransport.ConnectAsync(connection, cancellationToken: deadline.Token);
        var readiness = ((IJupyterTransportConnectionReadiness)transport)
            .WaitForStdinConnectedAsync(deadline.Token);

        await transport.DisposeAsync();

        await readiness.Invoking(static task => task).Should().ThrowAsync<ObjectDisposedException>();
    }
}
