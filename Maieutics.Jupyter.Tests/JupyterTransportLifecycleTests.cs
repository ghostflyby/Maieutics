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

        var connect = () => NetMqJupyterTransport.ConnectAsync(
            connection,
            cancellationToken: cancellation.Token);

        await connect.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = 15_000)]
    public async Task CancelledBindStopsIoLoopBeforeReturning()
    {
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var connection = JupyterConnectionInfo.CreateLocalTcp();

        var bind = () => NetMqJupyterKernelTransport.BindAsync(
            connection,
            cancellationToken: cancellation.Token);

        await bind.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Timeout = 15_000)]
    public async Task ConcurrentKernelTransportDisposeCompletesOnce()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        var transport = await NetMqJupyterKernelTransport.BindAsync(
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
        var transport = await NetMqJupyterTransport.ConnectAsync(connection, cancellationToken: deadline.Token);
        var ping = transport.PingAsync(deadline.Token);

        await Task.WhenAll(transport.DisposeAsync().AsTask(), transport.DisposeAsync().AsTask())
            .WaitAsync(deadline.Token);

        await ping.Invoking(task => task).Should().ThrowAsync<ObjectDisposedException>();
    }
}