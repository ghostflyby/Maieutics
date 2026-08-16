using FluentAssertions;
using Maieutics.Jupyter.Client;
using Maieutics.Jupyter.Client.Transport;
using Maieutics.Jupyter.Kernel;
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

    [Fact(Timeout = 15_000)]
    public async Task CleanKernelShutdown_DoesNotTerminateTransportOnAnyEof()
    {
        // Regression for the shutdown race: the kernel closes its five sockets
        // after the shutdown exchange, and EOF arrival order across the TCP
        // connections is not guaranteed. A clean EOF (failure == null) on any
        // channel - IOPub, shell, control, ... - must not terminate the
        // transport (which would fail every pending request with "The Jupyter
        // <channel> connection ended."). Only an abnormal peer end is
        // terminal. Observes the transport's incoming stream directly: a
        // clean-EOF termination completes it with an IOException.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var cancellationToken = deadline.Token;
        var connection = JupyterConnectionInfo.CreateLocalTcp();
        await using var host = await JupyterKernelHost.StartAsync(
            connection,
            new HangingKernelApplication(),
            cancellationToken: cancellationToken);
        await using var transport = await ZmqSharpJupyterTransport.ConnectAsync(
            connection,
            cancellationToken: cancellationToken);
        await ((IJupyterTransportConnectionReadiness)transport)
            .WaitForStdinConnectedAsync(cancellationToken);
        var enumeration = transport.IncomingMessages.GetAsyncEnumerator(cancellationToken);

        await host.DisposeAsync();

        // The stream must keep running (a clean EOF does not terminate the
        // transport) or end without an EOF error; a TimeoutException here
        // means "still open, no error" - exactly the desired state. Without
        // the fix the first clean EOF terminates the transport and the
        // enumeration throws IOException("The Jupyter ... connection ended.").
        var outcome = await Record.ExceptionAsync(async () =>
            await enumeration.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(300), cancellationToken));
        var failedByCleanEof = outcome is JupyterProtocolException or IOException;
        failedByCleanEof.Should().BeFalse($"a clean EOF must not terminate the transport; actual outcome: {outcome}");

        await transport.DisposeAsync();
    }

    private sealed class HangingKernelApplication : IJupyterKernelApplication
    {
        public JupyterKernelInfo KernelInfo { get; } = new(
            ProtocolVersion: "5.5",
            Implementation: "maieutics-test",
            ImplementationVersion: "1.0",
            LanguageInfo: new JupyterLanguageInfo("csharp", ".NET", ".cs", "dotnet"));

        public ValueTask<JupyterExecuteResult> ExecuteAsync(
            JupyterExecutionContext context,
            JupyterExecuteRequest request,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(JupyterExecuteResult.Ok);
        }
    }
}
