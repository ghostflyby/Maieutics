using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Maieutics.Product.Tests;

/// <summary>Guards the file logger's shutdown contract: a record racing disposal is dropped,
/// never thrown. The provider used to dispose its writer outside the gate that serializes
/// writes, so a record that had already entered <c>Write</c> — or that read the flag just
/// before it was set — reached a disposed <c>StreamWriter</c> and threw
/// <c>ObjectDisposedException</c> into the logging pipeline. The file sink is the evidence
/// channel for CI forensics, so noise injected at shutdown is costly.</summary>
[Collection(ProductIntegrationCollection.Name)]
public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string logDirectory;

    public FileLoggerProviderTests()
    {
        logDirectory = Path.Combine(
            Path.GetTempPath(),
            "maieutics-file-logger-tests",
            Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(logDirectory))
        {
            try
            {
                Directory.Delete(logDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Writers and one disposer released together must not let any exception escape.
    /// The pre-fix code surfaces <c>ObjectDisposedException</c> from the racing writer.</summary>
    [Fact(Timeout = 120_000)]
    public async Task RecordsRacingDisposalAreDroppedRatherThanThrown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // One round is a coin flip on the interleaving, so iterate: the window is between the
        // in-lock flag read and the writer call, which the scheduler only sometimes exposes.
        for (var round = 0; round < 200; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(logDirectory);
            var provider = new FileLoggerProvider(logDirectory);
            var logger = provider.CreateLogger("Maieutics.Product.Tests.FileLoggerProviderTests");

            // RaceAsync rethrows anything a writer or the disposer observes.
            await RaceAsync(
                writers: 8,
                cancellationToken,
                write: index => logger.LogInformation("record {Round}-{Index}", round, index),
                release: provider.Dispose);
        }
    }

    /// <summary>Concurrent disposers are safe, and disposal is idempotent.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ConcurrentDisposalIsSafeAndIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(logDirectory);
        var provider = new FileLoggerProvider(logDirectory);
        var logger = provider.CreateLogger("Maieutics.Product.Tests.FileLoggerProviderTests");
        logger.LogInformation("before disposal");

        await RaceAsync(
            writers: 16,
            cancellationToken,
            write: _ => { },
            release: provider.Dispose);

        // Still safe after the fact: a second pass of concurrent disposers plus writes.
        await RaceAsync(
            writers: 8,
            cancellationToken,
            write: _ => logger.LogInformation("after disposal"),
            release: provider.Dispose);

        var file = Directory.GetFiles(logDirectory, "maieutics-*.log").Should().ContainSingle().Which;
        File.ReadAllText(file).Should().Contain("before disposal");
    }

    /// <summary>Holds writers and the disposer at one start gate, then releases them together.</summary>
    private static async Task RaceAsync(
        int writers,
        CancellationToken cancellationToken,
        Action<int> write,
        Action release)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var total = writers + 1;
        Exception? failure = null;

        var tasks = Enumerable.Range(0, writers).Select(index => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == total) ready.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            try
            {
                for (var iteration = 0; iteration < 50; iteration++) write(index);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
            }
        }, cancellationToken)).Append(Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == total) ready.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            try
            {
                release();
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
            }
        }, cancellationToken)).ToArray();

        await ready.Task.WaitAsync(cancellationToken);
        gate.TrySetResult();
        await Task.WhenAll(tasks).WaitAsync(cancellationToken);

        if (failure is { } observed) throw new InvalidOperationException(observed.Message, observed);
    }
}
