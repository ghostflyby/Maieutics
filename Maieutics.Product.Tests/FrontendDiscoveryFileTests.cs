using System.Text.Json;

namespace Maieutics.Product.Tests;

public sealed class FrontendDiscoveryFileTests
{
    [Fact(Timeout = 30_000)]
    public async Task AtomicPublicationCompletesTheMissingFileWait()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var area = TempArea.Create("discovery-rename");
        var path = Path.Combine(area.Path, "ready.json");
        var temporary = Path.Combine(area.Path, "ready.tmp");
        await File.WriteAllTextAsync(temporary, """{"ready":true}""", deadline.Token);
        var read = FrontendDiscoveryFile.ReadAsync(path, TimeSpan.FromSeconds(8), deadline.Token);
        Assert.False(read.IsCompleted);
        try
        {
            File.Move(temporary, path);
            Assert.True((await read.WaitAsync(deadline.Token)).GetProperty("ready").GetBoolean());
        }
        finally
        {
            await deadline.CancelAsync();
            await ObserveAsync(read);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OverflowRechecksAnAlreadyPublishedFileWithoutAnotherFileEvent()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var area = TempArea.Create("discovery-overflow");
        var path = Path.Combine(area.Path, "ready.json");
        var watcher = new ControlledWatcher(area.Path);
        var read = FrontendDiscoveryFile.ReadAsync(path, TimeSpan.FromSeconds(8), deadline.Token,
            publicationWatcher: watcher);
        // A missing-file open completes synchronously. Returning this incomplete task
        // pins the wait after that failed probe, before any publication notification.
        Assert.False(read.IsCompleted);
        try
        {
            await File.WriteAllTextAsync(path, """{"ready":true}""", deadline.Token);
            watcher.Overflow();
            var result = await read.WaitAsync(deadline.Token);
            Assert.True(result.GetProperty("ready").GetBoolean());
        }
        finally
        {
            await deadline.CancelAsync();
            await ObserveAsync(read);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AWatcherFailureSettlesTheWaitWithItsCause()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var area = TempArea.Create("discovery-watch-failed");
        var watcher = new ControlledWatcher(area.Path);
        var failure = new IOException("directory notifications failed");
        var read = FrontendDiscoveryFile.ReadAsync(Path.Combine(area.Path, "ready.json"),
            TimeSpan.FromSeconds(8), deadline.Token, publicationWatcher: watcher);
        Assert.False(read.IsCompleted);
        try
        {
            watcher.Fail(failure);
            var error = await Assert.ThrowsAnyAsync<IOException>(() => read.WaitAsync(deadline.Token));
            Assert.Same(failure, error.InnerException);
        }
        finally
        {
            await deadline.CancelAsync();
            await ObserveAsync(read);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OverflowBeforePublicationRearmsTheWaitForARealNotification()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var area = TempArea.Create("discovery-rearm");
        var path = Path.Combine(area.Path, "ready.json");
        var watcher = new ControlledWatcher(area.Path);
        var probes = 0;
        var secondProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = FrontendDiscoveryFile.ReadAsync(path, TimeSpan.FromSeconds(8), deadline.Token,
            fault: () =>
            {
                if (++probes == 2) secondProbe.TrySetResult();
                return null;
            }, publicationWatcher: watcher);
        Assert.False(read.IsCompleted);
        try
        {
            watcher.Overflow();
            await secondProbe.Task.WaitAsync(deadline.Token);
            Assert.False(read.IsCompleted);
            await File.WriteAllTextAsync(path, """{"ready":true}""", deadline.Token);
            watcher.Publish();
            Assert.True((await read.WaitAsync(deadline.Token)).GetProperty("ready").GetBoolean());
        }
        finally
        {
            await deadline.CancelAsync();
            await ObserveAsync(read);
        }
    }

    private static async Task ObserveAsync(Task<JsonElement> read)
    {
        try { await read; }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException) { }
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationSettlesAMissingFileWait()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var area = TempArea.Create("discovery-cancellation");
        var path = Path.Combine(area.Path, "ready.json");
        var watcher = new ControlledWatcher(area.Path);
        var probes = 0;
        var read = FrontendDiscoveryFile.ReadAsync(path, TimeSpan.FromSeconds(8), deadline.Token,
            fault: () =>
            {
                ++probes;
                return null;
            }, publicationWatcher: watcher);
        Assert.False(read.IsCompleted);
        try
        {
            await deadline.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Equal(1, probes);
        }
        finally
        {
            await deadline.CancelAsync();
            await ObserveAsync(read);
        }
    }

    private sealed class ControlledWatcher(string directory) : FileSystemWatcher(directory, "never-published.json")
    {
        internal void Overflow() => OnError(new ErrorEventArgs(new InternalBufferOverflowException()));
        internal void Fail(Exception error) => OnError(new ErrorEventArgs(error));
        internal void Publish() => OnCreated(new FileSystemEventArgs(WatcherChangeTypes.Created, Path, "ready.json"));
    }
}
