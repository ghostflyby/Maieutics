using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Jupyter.Tests;

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
        return CreateSession(process, TerminalSessionKind.Persistent, "sh", []);
    }

    private static TerminalSession CreateSession(
        FakeTerminalProcess process,
        TerminalSessionKind kind,
        string executable,
        IReadOnlyList<string> launchArguments)
    {
        return new TerminalSession(
            AgentSessionId.Create(),
            Guid.NewGuid().ToString("N"),
            true,
            Directory.GetCurrentDirectory(),
            kind,
            executable,
            launchArguments,
            TestOptions(),
            new FakeTerminalProcessFactory(process),
            NullLogger<TerminalSession>.Instance);
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

        result.ExecutedLines.Should().Be(3);
        result.FailedLine.Should().BeNull();
        fake.HexWrites.Should().Equal("68656C6C6F", "0D", "776F726C64");
    }

    [Fact(Timeout = 10_000)]
    public async Task FirstFrameIsFullAndLaterFramesCarryOnlyChangedRows()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("hello\r\nworld\r\n");
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);

        var first = session.Snapshot(new TerminalSnapshotRequest());
        first.Frame.Full.Should().BeTrue();
        first.Frame.Lines.Should().Contain(row => row.Row == 0 && row.Text.Contains("hello"));

        // An unchanged frame still carries the cursor row so the cursor's content window stays anchored.
        var unchanged = session.Snapshot(new TerminalSnapshotRequest());
        unchanged.Frame.Full.Should().BeFalse();
        unchanged.Frame.Lines.Should().ContainSingle().Which.Row.Should().Be(2);

        before = session.ScreenVersion;
        fake.Emit("changed\r\n");
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);
        var changed = session.Snapshot(new TerminalSnapshotRequest());
        changed.Frame.Full.Should().BeFalse();
        changed.Frame.Lines.Should().Contain(row => row.Row == 2 && row.Text.Contains("changed"));
        changed.Frame.Lines.Should().Contain(row => row.Row == 3); // the new cursor row is included too
    }

    [Fact(Timeout = 10_000)]
    public async Task AlternateBufferIsReported()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("\x1b[?1049h");
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);

        var frame = session.Snapshot(new TerminalSnapshotRequest()).Frame;
        frame.AlternateBuffer.Should().BeTrue();
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

        result.ExecutedLines.Should().Be(1);
        result.FailedLine.Should().Be(2);
        fake.HexWrites.Should().Equal("6F6E65");
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

        fake.HexWrites.Should().Equal("03");
    }

    [Fact(Timeout = 10_000)]
    public async Task DisposeRunsTheProcessLadder()
    {
        var fake = new FakeTerminalProcess();
        var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        fake.Disposed.Should().BeTrue();
        session.GetSnapshot().State.Should().Be("closed");
    }

    [Fact(Timeout = 10_000)]
    public async Task CursorReportsRowColumnAndUniqueWindow()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("foo(bar)baz");
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);
        before = session.ScreenVersion;
        fake.Emit("\x1b[5G"); // cursor to column 5 (1-based) = cell column 4
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        cursor.Visible.Should().BeTrue();
        cursor.Row.Should().Be(0);
        cursor.Column.Should().Be(4);
        // The minimal unique window: "(" ends and "b" begins at only one gap on this row.
        cursor.Left.Should().Be("(");
        cursor.Right.Should().Be("b");
        cursor.Unambiguous.Should().BeTrue();
    }

    [Fact(Timeout = 10_000)]
    public async Task CursorStyleTracksDecscusr()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("\x1b[6 q"); // DECSCUSR 6: steady bar (vim insert mode)
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        cursor.Style.Should().Be("bar");
        cursor.Blink.Should().BeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task CursorWindowIsUnambiguousWithinItsOwnRow()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("aaaaaaaa\r\nbbbbbb");
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);
        before = session.ScreenVersion;
        fake.Emit("\x1b[2;3H"); // row 2 (1-based) = row 1 (0-based), column 3 (1-based) = column 2
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        cursor.Row.Should().Be(1);
        cursor.Column.Should().Be(2);
        // Two b's on the left and four on the right is the first minimal unique window (total budget 6).
        cursor.Left.Should().Be("bb");
        cursor.Right.Should().Be("bbbb");
        cursor.Unambiguous.Should().BeTrue();
    }

    [Fact(Timeout = 10_000)]
    public async Task CursorOnRepeatingLineFallsBackToColumn()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("aaaaaaaaaa");
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);
        before = session.ScreenVersion;
        fake.Emit("\x1b[1;8H"); // row 1 (1-based) = row 0, column 8 (1-based) = column 7
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        cursor.Row.Should().Be(0);
        cursor.Column.Should().Be(7);
        cursor.Left.Should().Be("aaaaaa");
        cursor.Right.Should().Be("aaa");
        cursor.Unambiguous.Should().BeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task HiddenCursorReportsVisibleFalse()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("\x1b[?25l"); // DECTCEM hide cursor
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);

        var cursor = session.Snapshot(new TerminalSnapshotRequest()).Frame.Cursor;
        cursor.Visible.Should().BeFalse();
        cursor.Style.Should().Be("block");
    }

    [Fact(Timeout = 10_000)]
    public async Task TruncatedFramesDoNotProducePhantomDiffs()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake);
        await session.StartAsync(TestContext.Current.CancellationToken);

        var before = session.ScreenVersion;
        fake.Emit("row one\r\nrow two\r\nrow three\r\nrow four\r\n");
        await WaitForVersionAsync(session, before + 1, TestContext.Current.CancellationToken);

        // A tight budget truncates the frame mid-way and keeps the cursor row anchored.
        var first = session.Snapshot(new TerminalSnapshotRequest(null, 20));
        first.Frame.Truncated.Should().BeTrue();

        // The identical screen must produce an empty diff on the next frame: no phantom rows from
        // truncated-vs-full text mismatches.
        var second = session.Snapshot(new TerminalSnapshotRequest(null, 20));
        second.Frame.Truncated.Should().BeTrue();
        second.Frame.Lines.Should().ContainSingle(); // only the cursor row anchor, no phantom diffs
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
        result.State.Should().Be("completed");
        result.ExitCode.Should().Be(0);
        result.Frame.Lines.Should().Contain(row => row.Text.Contains("hi"));
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

        result.State.Should().Be("running");
        result.ExitCode.Should().BeNull();

        // The handle stays alive: snapshots report running until the child exits.
        session.GetSnapshot().State.Should().Be("running");

        fake.EndOfOutput();
        fake.RaiseExited(3);
        await WaitForStateAsync(session, "completed", TestContext.Current.CancellationToken);
        session.GetSnapshot().ExitCode.Should().Be(3);
    }

    [Fact(Timeout = 10_000)]
    public async Task OneShotRunCancelTerminatesTheChild()
    {
        var fake = new FakeTerminalProcess();
        await using var session = CreateSession(fake, TerminalSessionKind.OneShot, "sleep", ["60"]);
        using var cancel = new CancellationTokenSource();

        // Start the run, then cancel while it waits for the child to exit.
        var run = session.RunOnceAsync(TimeSpan.FromSeconds(60), new TerminalSnapshotRequest(), cancel.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cancel.Cancel();

        var failure = () => run;
        await failure.Should().ThrowAsync<OperationCanceledException>();
        fake.Disposed.Should().BeTrue();
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
        completed.State.Should().Be("completed");
        var snapshot = session.Snapshot(new TerminalSnapshotRequest());
        snapshot.State.Should().Be("completed");
        snapshot.ExitCode.Should().Be(7);
    }

    private static async Task WaitForStateAsync(
        TerminalSession session,
        string expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!string.Equals(session.GetSnapshot().State, expected, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"state never became {expected}");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task WaitForVersionAsync(
        TerminalSession session,
        long minimum,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (session.ScreenVersion < minimum)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("the emitted output was never applied");
            await Task.Delay(10, cancellationToken);
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

internal sealed class FakeTerminalProcess : ITerminalProcess
{
    private readonly FakePtyStream stream;

    internal FakeTerminalProcess(int? failOnWrite = null)
    {
        stream = new FakePtyStream(failOnWrite);
    }

    public Stream BaseStream => stream;

    public int Pid => 42;

    public bool HasExited { get; private set; }

    public int? ExitCode { get; private set; }

    public TimeSpan GracefulExitTimeout { get; set; }

    public event Action<int, ITerminalProcess>? Exited;

    public IReadOnlyList<string> HexWrites => stream.Writes.Select(Convert.ToHexString).ToArray();

    public bool Disposed { get; private set; }

    public void RaiseExited(int exitCode)
    {
        ExitCode = exitCode;
        HasExited = true;
        Exited?.Invoke(exitCode, this);
    }

    public void Emit(params string[] texts)
    {
        stream.Emit(Encoding.UTF8.GetBytes(string.Concat(texts)));
    }

    public void EndOfOutput()
    {
        stream.EndOfOutput();
    }

    public void RequestClose()
    {
    }

    public void Kill()
    {
        HasExited = true;
    }

    public void Resize(int columns, int rows)
    {
    }

    public Task<bool> WaitForExitAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(HasExited);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private sealed class FakePtyStream : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private readonly int? failOnWrite;

        internal FakePtyStream(int? failOnWrite)
        {
            this.failOnWrite = failOnWrite;
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
            chunks.Writer.TryWrite(bytes);
        }

        internal void EndOfOutput()
        {
            chunks.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!await chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!chunks.Reader.TryRead(out var chunk)) return 0;

            var count = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, count).CopyTo(buffer.Span);
            if (count < chunk.Length) chunks.Writer.TryWrite(chunk[count..]);
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WriteCount++;
            if (failOnWrite is { } fail && WriteCount >= fail)
                throw new IOException("The pty child closed its terminal.");

            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

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
