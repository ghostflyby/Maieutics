using System.Runtime.InteropServices;
using FluentAssertions;
using Maieutics;
using Maieutics.Jupyter.Kernel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

public sealed class JupyterKernelLifetimeTests
{
    [Fact]
    public void SigIntRequestsInterruptWithoutStoppingApplication()
    {
        var application = new FakeApplicationLifetime();
        var coordinator = new FakeInterruptCoordinator();
        using var lifetime = new JupyterKernelLifetime(application, coordinator, NullLogger<JupyterKernelLifetime>.Instance);

        lifetime.HandleSignal(PosixSignal.SIGINT);

        coordinator.InterruptRequests.Should().Be(1);
        application.StoppingCount.Should().Be(0);
    }

    [Theory]
    [InlineData(PosixSignal.SIGQUIT)]
    [InlineData(PosixSignal.SIGTERM)]
    public void ShutdownSignalsStopApplicationWithoutInterrupt(PosixSignal signal)
    {
        var application = new FakeApplicationLifetime();
        var coordinator = new FakeInterruptCoordinator();
        using var lifetime = new JupyterKernelLifetime(application, coordinator, NullLogger<JupyterKernelLifetime>.Instance);

        lifetime.HandleSignal(signal);

        coordinator.InterruptRequests.Should().Be(0);
        application.StoppingCount.Should().Be(1);
    }

    [Fact]
    public async Task WaitForStartAsyncRegistersAndStopAsyncUnregistersSignals()
    {
        var application = new FakeApplicationLifetime();
        var coordinator = new FakeInterruptCoordinator();
        using var lifetime = new JupyterKernelLifetime(application, coordinator, NullLogger<JupyterKernelLifetime>.Instance);

        await lifetime.WaitForStartAsync(CancellationToken.None);
        await lifetime.StopAsync(CancellationToken.None);
        await lifetime.WaitForStartAsync(CancellationToken.None);
        await lifetime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void CoordinatorInterruptWithoutHostIsNoOp()
    {
        var coordinator = new KernelInterruptCoordinator();

        var action = () => coordinator.RequestInterrupt();

        action.Should().NotThrow();
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private int stoppingCount;

        public int StoppingCount => Volatile.Read(ref stoppingCount);

        public CancellationToken ApplicationStarted { get; } = new CancellationTokenSource().Token;

        public CancellationToken ApplicationStopping { get; } = new CancellationTokenSource().Token;

        public CancellationToken ApplicationStopped { get; } = new CancellationTokenSource().Token;

        public void StopApplication() => Interlocked.Increment(ref stoppingCount);
    }

    private sealed class FakeInterruptCoordinator : IKernelInterruptCoordinator
    {
        private int interruptRequests;

        public int InterruptRequests => Volatile.Read(ref interruptRequests);

        public void SetHost(JupyterKernelHost host)
        {
        }

        public void Clear()
        {
        }

        public void RequestInterrupt() => Interlocked.Increment(ref interruptRequests);
    }
}
