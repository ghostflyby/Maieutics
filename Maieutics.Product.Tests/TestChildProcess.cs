using System.Diagnostics;
using Maieutics.DenoExecution;

namespace Maieutics.Product.Tests;

/// <summary>A child the suite starts directly, holding a slot of the shared process budget
/// for the child's whole life. Production spawn paths for internal Deno children take the
/// same budget through their own leases; this wrapper exists for the children only tests
/// start (real Deno probes and the published smoke executable), so the cap covers the whole
/// suite instead of leaving those starts unbounded. Disposal kills the child tree first and
/// then releases the slot, on every path including a failed read or a canceled test.</summary>
internal sealed class TestChildProcess : IAsyncDisposable
{
    private static readonly TimeSpan ReapBudget = TimeSpan.FromSeconds(10);

    private readonly Process _process;
    private readonly ChildSlotLease _slot;
    private bool _disposed;

    private TestChildProcess(Process process, ChildSlotLease slot)
    {
        this._process = process;
        this._slot = slot;
    }

    internal int Id => _process.Id;

    internal Process Raw => _process;

    /// <summary>Starts the child behind the shared budget. A failed start releases the
    /// slot before rethrowing, so a spawn failure cannot wedge the budget.</summary>
    internal static async Task<TestChildProcess> StartAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        var slot = await TestChildProcessGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException(
                          $"The test child '{startInfo.FileName}' could not be started.");
        }
        catch
        {
            slot.Dispose();
            throw;
        }

        return new TestChildProcess(process, slot);
    }

    /// <summary>Drains both pipes concurrently (a child blocked on a full pipe never
    /// exits), waits for exit, and returns the combined output. The caller's cancellation
    /// and disposal still kill the tree.</summary>
    internal async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
    {
        var stdout = _process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = _process.StandardError.ReadToEndAsync(cancellationToken);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var outputs = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return outputs[0] + outputs[1];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(ReapBudget);
                try
                {
                    await _process.WaitForExitAsync(reap.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The kill was issued; an unresponsive tree must not extend teardown.
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            // The child exited between the check and the kill.
        }
        finally
        {
            _process.Dispose();
            _slot.Dispose();
        }
    }
}
