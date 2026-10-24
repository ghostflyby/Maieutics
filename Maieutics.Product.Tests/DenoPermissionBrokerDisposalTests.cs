using System.IO.Pipes;
using Maieutics.DenoExecution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>
///     Pins the shutdown ownership of the broker's listeners. The broker binds its listener before
///     anything can connect to it, so the accept loop is the only component positioned to release
///     it, and DisposeAsync on <see cref="DenoPermissionBroker"/> just awaits that loop. A listener
///     the loop leaves undisposed keeps its handle — and on Windows the broker's pipe name — alive
///     for the rest of the process.
/// </summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class DenoPermissionBrokerDisposalTests
{
    private const int PipeListenerInstanceLimit = 8; // The broker's pending-connection limit.

    /// <summary>Disposing the broker must release the named-pipe listener it holds. On Windows the
    /// listener is the one the broker created in its Windows branch; on unix that branch never
    /// runs, so an equivalent listener is installed into the broker's listener field to drive the
    /// same shutdown path — otherwise the leak the Windows branch exposes would be invisible to
    /// unix CI.</summary>
    [Fact(Timeout = 30_000)]
    public async Task DisposalReleasesTheNamedPipeListenerTheBrokerHolds()
    {
        // The body's awaits (DisposeAsync) take no token, so cooperate with the test host's
        // timeout directly instead.
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        // The test owns the broker's disposal explicitly: releasing it mid-test is the
        // behavior under test, so there is no using scope to auto-dispose behind it. The
        // finally re-disposes idempotently if an assertion fails first.
        var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var listener = InstallPipeListener(broker);
        try
        {
            // Baseline for the probe below. TransmissionMode is the probe member because its only
            // guard (CheckPipePropertyOperations) rejects a closed pipe, not an unconnected one:
            // Flush cannot play this role because on a fresh server pipe — the WaitingToConnect
            // state the broker's listener sits in between connections — Flush throws
            // InvalidOperationException before it ever reaches its disposal check, so
            // ObjectDisposedException separates a released listener from a leaked one: a leaked
            // (still live) listener cannot satisfy the probe silently, and a released one cannot
            // pass the baseline.
            Assert.Null(Record.Exception(() => _ = listener.TransmissionMode));

            await broker.DisposeAsync();

            // The leak this pins: the accept loop is the only component positioned to release the
            // listener (the broker binds it before anything can connect, and DisposeAsync only
            // awaits that loop), so a shutdown whose cleanup releases only the unix socket leaves
            // the pipe handle — and on Windows the broker's pipe name — alive for the rest of the
            // process. A released listener throws ObjectDisposedException from the same property
            // read the baseline proved live.
            Assert.Throws<ObjectDisposedException>(() => _ = listener.TransmissionMode);
        }
        finally
        {
            // The pre-fix behaviour this test pins leaks the listener; the test still owns its
            // probe and must not leave the pipe behind either. Disposal is idempotent, so this
            // is a no-op when the broker released it — and it releases the broker itself when
            // an assertion above failed before the test reached its own DisposeAsync.
            await listener.DisposeAsync();
            await broker.DisposeAsync();
        }
    }

    /// <summary>The unix branch keeps releasing its own listener: the socket path is deleted with
    /// the broker, and the shutdown change must not alter that.</summary>
    [Fact(Timeout = 30_000)]
    public async Task DisposalKeepsReleasingTheUnixSocketPath()
    {
        // The body's awaits (DisposeAsync) take no token, so cooperate with the test host's
        // timeout directly instead.
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The unix branch binds a socket path; Windows covers its pipe listener directly.");
        }

        // Disposal is the test's subject, so the broker is owned explicitly instead of a
        // using scope that would auto-dispose behind the manual release.
        var broker = DenoPermissionBroker.Create(NullLogger<DenoPermissionBroker>.Instance);
        var socketPath = broker.Address;
        Assert.True(File.Exists(socketPath));
        await broker.DisposeAsync();
        Assert.False(File.Exists(socketPath));
    }

    /// <summary>Returns the broker's live named-pipe listener. On Windows the broker created one;
    /// on unix the listener field is null by construction, so an equivalent listener (same
    /// direction, instance limit, transmission mode, and async mode as the broker's own) is
    /// installed to stand in for it.</summary>
    private static NamedPipeServerStream InstallPipeListener(DenoPermissionBroker broker)
    {
        if (broker._pipeListener is NamedPipeServerStream existing)
        {
            return existing;
        }

        // Unix binds the pipe name as "{TempPath}/CoreFxPipe_{name}" and macOS caps domain socket
        // paths at 104 characters, so the synthetic name stays short — the broker truncates its
        // socket name in CreateSocketPath for the same reason; the truncated Guid suffix keeps
        // concurrent runs collision-free.
        var installed = new NamedPipeServerStream(
            $"mc-broker-disp-{Guid.NewGuid():N}"[..24],
            PipeDirection.InOut,
            PipeListenerInstanceLimit,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        broker._pipeListener = installed;
        return installed;
    }
}
