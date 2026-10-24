using System.Text;
using System.Threading.Channels;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

public sealed class TerminalSessionTests
{
    private static TerminalOptions TestOptions()
    {
        return new TerminalOptions
        {
            Columns = 40,
            Rows = 10,
            SettleTimeout = TimeSpan.FromMilliseconds(30)
        };
    }

    private static TerminalSession CreateSession(FakeTerminalProcess process)
    {
        return CreateSession(process, TerminalSessionKind.Persistent, "sh", [], EffectivePolicy.Default);
    }

    private static TerminalSession CreateSession(
        FakeTerminalProcess process,
        TerminalSessionKind kind,
        string executable,
        IReadOnlyList<string> launchArguments)
    {
        return CreateSession(process, kind, executable, launchArguments, EffectivePolicy.Default);
    }

    private static TerminalSession CreateSession(
        FakeTerminalProcess process,
        string executable,
        IReadOnlyList<string> launchArguments,
        EffectivePolicy policy)
    {
        return CreateSession(process, TerminalSessionKind.Persistent, executable, launchArguments, policy);
    }

    private static TerminalSession CreateSession(
        FakeTerminalProcess process,
        TerminalSessionKind kind,
        string executable,
        IReadOnlyList<string> launchArguments,
        EffectivePolicy policy)
    {
        return new TerminalSession(
            Guid.NewGuid().ToString("N"),
            true,
            Directory.GetCurrentDirectory(),
            kind,
            executable,
            launchArguments,
            policy,
            TestOptions(),
            new FakeTerminalProcessFactory(process),
            NullLogger<TerminalSession>.Instance);
    }

    [Fact(Timeout = 10_000)]
    public async Task RestrictedRunPolicyRejectsTheConfiguredExecutable()
    {
        var fake = new FakeTerminalProcess();
        var restricted = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = ["/usr/bin/safe-shell"] }));
        await using var session = CreateSession(fake, "sh", [], restricted);

        var assertion36 = await Assert.ThrowsAnyAsync<AgentToolException>(() => session.StartAsync(TestContext.Current.CancellationToken));
        Assert.True(((Func<AgentToolException, bool>)(static exception => exception.Code == "terminal_start_failed"))(assertion36));
        Assert.Equal("faulted", session.GetSnapshot().State);
    }

    [Fact(Timeout = 10_000)]
    public async Task StartBudgetExceededFailsTheSessionWithStartFailed()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = TestOptions();
        options.StartupTimeout = TimeSpan.FromMilliseconds(100);
        var session = new TerminalSession(
            Guid.NewGuid().ToString("N"),
            true,
            Directory.GetCurrentDirectory(),
            TerminalSessionKind.Persistent,
            "sh",
            [],
            EffectivePolicy.Default,
            options,
            new StalledTerminalProcessFactory(release.Task),
            NullLogger<TerminalSession>.Instance);
        try
        {
            var start = () => session.StartAsync(TestContext.Current.CancellationToken);

            var assertion37 = await Assert.ThrowsAnyAsync<AgentToolException>(async () => { await start.Invoke(); });
            Assert.True(((Func<AgentToolException, bool>)(static exception => exception.Code == "terminal_start_failed"))(assertion37));
            Assert.Equal("faulted", session.GetSnapshot().State);
        }
        finally
        {
            release.SetResult();
            await session.DisposeAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task DefaultPolicyAllowsTheConfiguredExecutable()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);

        await session.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("idle", session.GetSnapshot().State);
    }

    [Fact(Timeout = 10_000)]
    public async Task RunGrantDoesNotAdmitASiblingExecutable()
    {
        var fake = new FakeTerminalProcess();
        var restricted = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = ["/usr/bin/safe-shell"] }));
        // The sibling name merely shares the grant's prefix; the boundary rule keeps it out.
        await using var session = CreateSession(fake, "/usr/bin/safe-shell-evil", [], restricted);

        var assertion38 = await Assert.ThrowsAnyAsync<AgentToolException>(() => session.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal("terminal_start_failed", assertion38.Code);
        Assert.Equal("faulted", session.GetSnapshot().State);
    }

    [Fact(Timeout = 10_000)]
    public async Task RunGrantAdmitsTheExactExecutableAndDirectoryChildren()
    {
        var fake = new FakeTerminalProcess();
        var restricted = BuildPolicy(
            (PermissionKind.Run, new PermissionKindRules { Allow = ["/opt/shells"] }));

        await using var exact = CreateSession(fake, "/opt/shells", [], restricted);
        await exact.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal("idle", exact.GetSnapshot().State);

        var nestedFake = new FakeTerminalProcess();
        await using var nested = CreateSession(nestedFake, "/opt/shells/nested/sh", [], restricted);
        await nested.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal("idle", nested.GetSnapshot().State);
    }

    private static EffectivePolicy BuildPolicy(params (PermissionKind Kind, PermissionKindRules Rules)[] kinds)
    {
        return PermissionLayerStore.Build(
            [new PermissionLayer { Kinds = kinds.ToDictionary(static entry => entry.Kind, static entry => entry.Rules) }],
            new VariableTable(new EmptyVariableSource()));
    }

    private sealed class EmptyVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name)
        {
            return null;
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task ExecuteWritesBatchLinesInOrder()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);

        var batch = TerminalInputBatchParser.Parse("t hello\nk <CR>\n" + "t world");
        var result = await session.ExecuteInputAsync(
            batch,
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExecutedLines);
        Assert.Null(result.FailedLine);
        Assert.Equal(["68656C6C6F", "0D", "776F726C64"], fake.HexWrites);
    }

    [Fact(Timeout = 10_000)]
    public async Task FirstFrameIsFullAndLaterFramesCarryOnlyChangedRows()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("hello\r\nworld\r\n");
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);

        var first = session.Snapshot(new TerminalSnapshotRequest());
        Assert.True(first.Frame.Full);
        Assert.Contains(first.Frame.Lines, row => row.Row == 0 && row.Text.Contains("hello"));

        // An unchanged frame still carries the cursor row so the cursor's content window stays anchored.
        var unchanged = session.Snapshot(new TerminalSnapshotRequest());
        Assert.False(unchanged.Frame.Full);
        Assert.Equal(2, Assert.Single(unchanged.Frame.Lines).Row);

        before = session.ScreenVersion;
        fake.Emit("changed\r\n");
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);
        var changed = session.Snapshot(new TerminalSnapshotRequest());
        Assert.False(changed.Frame.Full);
        Assert.Contains(changed.Frame.Lines, row => row.Row == 2 && row.Text.Contains("changed"));
        Assert.Contains(changed.Frame.Lines, row => row.Row == 3); // the new cursor row is included too
    }

    [Fact(Timeout = 10_000)]
    public async Task AlternateBufferIsReported()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("\x1b[?1049h");
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);

        var frame = session.Snapshot(new TerminalSnapshotRequest()).Frame;
        Assert.True(frame.AlternateBuffer);
    }

    [Fact(Timeout = 10_000)]
    public async Task WriteFailureStopsAtTheFailingLine()
    {
        var fake = new FakeTerminalProcess(failOnWrite: 2);
        await using var session = CreateSession(fake);

        var batch = TerminalInputBatchParser.Parse("t one\nk <CR>\n" + "t three");
        var result = await session.ExecuteInputAsync(
            batch,
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExecutedLines);
        Assert.Equal(2, result.FailedLine);
        Assert.Equal(["6F6E65"], fake.HexWrites);
    }

    [Fact(Timeout = 10_000)]
    public async Task UnexpectedEndOfOutputFaultsTheSession()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        fake.EndOfOutput();

        await WaitForStateAsync(session, "faulted", TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 10_000)]
    public async Task InterruptWritesTheInterruptByte()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);

        await session.InterruptAsync(new TerminalSnapshotRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(["03"], fake.HexWrites);
    }

    [Fact(Timeout = 10_000)]
    public async Task DisposeRunsTheProcessLadder()
    {
        var fake = new FakeTerminalProcess();
        var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        Assert.True(fake.Disposed);
        Assert.Equal("closed", session.GetSnapshot().State);
    }

    [Fact(Timeout = 10_000)]
    public async Task CursorReportsRowColumnAndUniqueWindow()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("foo(bar)baz");
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);
        before = session.ScreenVersion;
        fake.Emit("\x1b[5G"); // cursor to column 5 (1-based) = cell column 4
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        Assert.True(cursor.Visible);
        Assert.Equal(0, cursor.Row);
        Assert.Equal(4, cursor.Column);
        // The minimal unique window: "(" ends and "b" begins at only one gap on this row.
        Assert.Equal("(", cursor.Left);
        Assert.Equal("b", cursor.Right);
        Assert.True(cursor.Unambiguous);
    }

    [Fact(Timeout = 10_000)]
    public async Task CursorStyleTracksDecscusr()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("\x1b[6 q"); // DECSCUSR 6: steady bar (vim insert mode)
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        Assert.Equal("bar", cursor.Style);
        Assert.False(cursor.Blink);
    }

    [Fact(Timeout = 10_000)]
    public async Task CursorWindowIsUnambiguousWithinItsOwnRow()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("aaaaaaaa\r\nbbbbbb");
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);
        before = session.ScreenVersion;
        fake.Emit("\x1b[2;3H"); // row 2 (1-based) = row 1 (0-based), column 3 (1-based) = column 2
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        Assert.Equal(1, cursor.Row);
        Assert.Equal(2, cursor.Column);
        // Two b's on the left and four on the right is the first minimal unique window (total budget 6).
        Assert.Equal("bb", cursor.Left);
        Assert.Equal("bbbb", cursor.Right);
        Assert.True(cursor.Unambiguous);
    }

    [Fact(Timeout = 10_000)]
    public async Task CursorOnRepeatingLineFallsBackToColumn()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("aaaaaaaaaa");
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);
        before = session.ScreenVersion;
        fake.Emit("\x1b[1;8H"); // row 1 (1-based) = row 0, column 8 (1-based) = column 7
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        Assert.Equal(0, cursor.Row);
        Assert.Equal(7, cursor.Column);
        Assert.Equal("aaaaaa", cursor.Left);
        Assert.Equal("aaa", cursor.Right);
        Assert.False(cursor.Unambiguous);
    }

    [Fact(Timeout = 10_000)]
    public async Task HiddenCursorReportsVisibleFalse()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("\x1b[?25l"); // DECTCEM hide cursor
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        Assert.False(cursor.Visible);
        Assert.Equal("block", cursor.Style);
    }

    [Fact(Timeout = 10_000)]
    public async Task TruncatedFramesDoNotProducePhantomDiffs()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("row one\r\nrow two\r\nrow three\r\nrow four\r\n");
        await session.WaitForScreenVersionAsync(before + 1, TestContext.Current.CancellationToken);

        // A tight budget truncates the frame mid-way and keeps the cursor row anchored.
        var first = session.Snapshot(new TerminalSnapshotRequest(null, 20));
        Assert.True(first.Frame.Truncated);

        // The identical screen must produce an empty diff on the next frame: no phantom rows from
        // truncated-vs-full text mismatches.
        var second = session.Snapshot(new TerminalSnapshotRequest(null, 20));
        Assert.True(second.Frame.Truncated);
        Assert.Single(second.Frame.Lines); // only the cursor row anchor, no phantom diffs
    }

    [Fact(Timeout = 10_000)]
    public async Task ExitedEventFaultsAnIdleSession()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        fake.RaiseExited(0);

        await WaitForStateAsync(session, "faulted", TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotRunCompletesWithExitCodeAndFinalFrame()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake, TerminalSessionKind.OneShot, "echo", ["hi"]);
        var run = session.RunOnceAsync(
            TimeSpan.FromSeconds(5),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);

        fake.Emit("hi\r\n");
        fake.EndOfOutput();
        fake.RaiseExited(0);

        var result = await run;
        Assert.Equal("completed", result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Frame.Lines, row => row.Text.Contains("hi"));
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotRunTimesOutAndReturnsRunningHandle()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake, TerminalSessionKind.OneShot, "sleep", ["60"]);
        fake.Emit("partial output\r\n");

        var result = await session.RunOnceAsync(
            TimeSpan.FromMilliseconds(50),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal("running", result.State);
        Assert.Null(result.ExitCode);

        // The handle stays alive: snapshots report running until the child exits.
        Assert.Equal("running", session.GetSnapshot().State);

        fake.EndOfOutput();
        fake.RaiseExited(3);
        await WaitForStateAsync(session, "completed", TestContext.Current.CancellationToken);
        Assert.Equal(3, session.GetSnapshot().ExitCode);
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotRunCancelTerminatesTheChild()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake, TerminalSessionKind.OneShot, "sleep", ["60"]);
        using var cancel = new CancellationTokenSource();

        // Start the run, then cancel once the start has settled: the wire "running" state
        // proves the spawn was adopted (the idle transition happens after the process is
        // published), so the cancel lands on the run's dispose-before-rethrow path and the
        // assertion below is deterministic. The old fixed 50ms pacing raced the
        // fire-and-forget late-spawn reclaim under suite load: the spawn was still in
        // flight at cancel time, and the reclaim disposed the child only after this
        // assertion had already read it.
        var run = session.RunOnceAsync(TimeSpan.FromSeconds(60), new TerminalSnapshotRequest(), cancel.Token);
        await WaitForStateAsync(session, "running", TestContext.Current.CancellationToken);
        await cancel.CancelAsync();

        var failure = () => run;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await failure.Invoke(); });
        Assert.True(fake.Disposed);
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotRunCancelDuringStartReclaimsTheLateSpawn()
    {
        var fake = new FakeTerminalProcess();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new StalledTerminalProcessFactory(release.Task, fake);
        var session = new TerminalSession(
            Guid.NewGuid().ToString("N"),
            true,
            Directory.GetCurrentDirectory(),
            TerminalSessionKind.OneShot,
            "sleep",
            ["60"],
            EffectivePolicy.Default,
            TestOptions(),
            factory,
            NullLogger<TerminalSession>.Instance);
        try
        {
            using var cancel = new CancellationTokenSource();

            // Cancel while the spawn is still blocked: the OCE surfaces before any process
            // is adopted, so the only disposer is the fire-and-forget late-spawn reclaim.
            // It must still dispose the child — asynchronously, because cancellation must
            // not wait on a pathological spawn. StartEntered makes the in-flight spawn
            // deterministic instead of pacing cancellation against a sleep.
            var run = session.RunOnceAsync(TimeSpan.FromSeconds(60), new TerminalSnapshotRequest(), cancel.Token);
            await factory.StartEntered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await cancel.CancelAsync();

            var failure = () => run;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await failure.Invoke(); });
            release.TrySetResult();

            await fake.DisposedSignal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(fake.Disposed, "the reclaim must dispose the late spawn even though the cancellation surfaced first");
        }
        finally
        {
            release.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotCompletionAllowsFollowUpSnapshot()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake, TerminalSessionKind.OneShot, "echo", ["done"]);
        var run = session.RunOnceAsync(
            TimeSpan.FromSeconds(5),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);

        fake.Emit("done\r\n");
        fake.EndOfOutput();
        fake.RaiseExited(7);


        var completed = await run;
        Assert.Equal("completed", completed.State);
        var snapshot = session.Snapshot(new TerminalSnapshotRequest());
        Assert.Equal(7, snapshot.ExitCode);
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotSessionRejectsRestartAfterCompletion()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake, TerminalSessionKind.OneShot, "echo", ["done"]);
        var run = session.RunOnceAsync(
            TimeSpan.FromSeconds(5),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);
        fake.Emit("done\r\n");
        fake.EndOfOutput();
        fake.RaiseExited(0);
        await run;

        // The one-shot's single-run bookkeeping is spent: a restart would spawn a second
        // child whose exit can never be recorded. It must be rejected, not corrupt the
        // session into a permanent "running" handle with a stale exit code.
        var assertion39 = await Assert.ThrowsAnyAsync<AgentToolException>(() => session.StartAsync(TestContext.Current.CancellationToken));
        Assert.True(((Func<AgentToolException, bool>)(static exception => exception.Code == "terminal_session_completed"))(assertion39));

        Assert.Equal("completed", session.GetSnapshot().State);
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotSignalTerminationCompletesWithoutAnExitCode()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake, TerminalSessionKind.OneShot, "sleep", ["60"]);
        var run = session.RunOnceAsync(
            TimeSpan.FromSeconds(5),
            new TerminalSnapshotRequest(),
            TestContext.Current.CancellationToken);

        fake.EndOfOutput();
        fake.RaiseSignaled(9);

        var completed = await run;
        Assert.Equal("completed", completed.State);
        Assert.Null(completed.ExitCode);
        Assert.Null(session.GetSnapshot().ExitCode);
    }

    /// <summary>Awaits a state transition on the session's own signal instead of polling:
    /// capture <see cref="TerminalSession.StateChanged"/> before triggering the transition,
    /// then re-check the wire state after each completion. The wait is bounded only by the
    /// caller's deadline, and a state that never arrives is named in the timeout.</summary>
    private static async Task WaitForStateAsync(
        TerminalSession session,
        string expected,
        CancellationToken cancellationToken)
    {
        while (!string.Equals(session.GetSnapshot().State, expected, StringComparison.Ordinal))
        {
            var changed = session.StateChanged;
            await changed.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}

internal sealed class FakeTerminalProcessFactory(FakeTerminalProcess process) : ITerminalProcessFactory
{
    public ITerminalProcess Start(
        string shell,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        int columns,
        int rows)
    {
        return process;
    }
}

/// <summary>Blocks Start until released, simulating a pathological PTY allocation that
/// exceeds the session's start budget. The product runs Start on a dedicated thread, so
/// blocking there is safe. An explicit <paramref name="process" /> is returned after the
/// release so a test can cancel while the spawn is still in flight and assert on the very
/// process the late-spawn reclaim disposes.</summary>
internal sealed class StalledTerminalProcessFactory(Task pendingRelease, FakeTerminalProcess? process = null)
    : ITerminalProcessFactory
{
    private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the product has entered <see cref="Start"/> and is blocked on
    /// the release gate; tests await this instead of pacing against a sleep.</summary>
    internal Task StartEntered => _startEntered.Task;

    public ITerminalProcess Start(
        string shell,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        int columns,
        int rows)
    {
        _startEntered.TrySetResult();
        pendingRelease.Wait();
        return process ?? new FakeTerminalProcess();
    }
}

internal sealed class FakeTerminalProcess : ITerminalProcess
{
    private readonly FakePtyStream _stream;

    internal FakeTerminalProcess(int? failOnWrite = null)
    {
        _stream = new FakePtyStream(failOnWrite);
    }

    public Stream BaseStream => _stream;

    public int? ExitCode { get; private set; }

    public int? TerminationSignal { get; private set; }

    public TimeSpan GracefulExitTimeout { get; set; }

    public event Action<ITerminalProcess>? Exited;

    public IReadOnlyList<string> HexWrites => [.. _stream.Writes.Select(Convert.ToHexString)];

    public bool Disposed { get; private set; }

    /// <summary>Completes when the session disposes this process, so the late-spawn reclaim
    /// can be awaited instead of polled.</summary>
    internal Task DisposedSignal => _disposed.Task;

    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RaiseExited(int exitCode)
    {
        ExitCode = exitCode;
        Exited?.Invoke(this);
    }

    public void RaiseSignaled(int signal)
    {
        TerminationSignal = signal;
        Exited?.Invoke(this);
    }

    public void Emit(params string[] texts)
    {
        _stream.Emit(Encoding.UTF8.GetBytes(string.Concat(texts)));
    }

    public void EndOfOutput()
    {
        _stream.EndOfOutput();
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private sealed class FakePtyStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private readonly int? _failOnWrite;

        internal FakePtyStream(int? failOnWrite)
        {
            this._failOnWrite = failOnWrite;
        }

        internal List<byte[]> Writes { get; } = [];

        internal int WriteCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Emit(byte[] bytes)
        {
            _chunks.Writer.TryWrite(bytes);
        }

        internal void EndOfOutput()
        {
            _chunks.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_chunks.Reader.TryRead(out var chunk)) return 0;

            var count = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, count).CopyTo(buffer.Span);
            if (count < chunk.Length) _chunks.Writer.TryWrite(chunk[count..]);
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            if (_failOnWrite is { } fail && WriteCount >= fail)
                throw new IOException("The pty child closed its terminal.");

            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("The fake pty stream is asynchronous only.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("The fake pty stream is asynchronous only.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}
