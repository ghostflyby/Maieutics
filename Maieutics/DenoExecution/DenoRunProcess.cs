using System.Buffers;
using System.Diagnostics;
using System.Text;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoExecution;

/// <summary>Generic supervised internal <c>deno run</c> child: launch, stdout/stderr drain with
/// bounded logging and optional stderr capture, completion observation, and graceful stop with
/// <c>Kill(true)</c> escalation. The REPL and plugin-host adapters keep their own module,
/// flag, and environment concerns and delegate the process plumbing here (ADR 0018 §8).</summary>
internal sealed class DenoRunProcess : IAsyncDisposable
{
    private const int DrainBufferCharacters = 4096;
    private const int MaximumLoggedCharactersPerStream = 32 * 1024;

    /// <summary>Bounds one logged child-output line so a child that never breaks lines still
    /// cannot emit a single oversized log record; the line flushes early at this length.</summary>
    private const int MaximumLoggedLineCharacters = 4096;

    /// <summary>The default total stop budget: the kill is immediate, so the budget bounds the
    /// final completion drain. Callers that need a different budget pass one to
    /// <see cref="Start"/>; layered shutdown paths compose their own total budget on top.</summary>
    internal static readonly TimeSpan DefaultStopBudget = TimeSpan.FromSeconds(10);

    private readonly Lock gate = new();
    private readonly Process process;
    private readonly string processDescription;
    private readonly int processId;
    private readonly ILogger logger;
    private readonly TimeSpan stopBudget;
    private readonly TaskCompletionSource<string>? standardError;
    private int exitCode = int.MinValue;
    private Task? stopping;

    private DenoRunProcess(
        Process process,
        InternalDenoProcessKind kind,
        ILogger logger,
        TimeSpan stopBudget,
        TaskCompletionSource<string>? standardError)
    {
        this.process = process;
        this.logger = logger;
        this.stopBudget = stopBudget;
        processDescription = Describe(kind);
        processId = process.Id;
        this.standardError = standardError;
        var stdoutDrain = DrainAsync(process.StandardOutput, "stdout", logger, null);
        var stderrDrain = DrainAsync(process.StandardError, "stderr", logger, standardError);
        Completion = ObserveCompletionAsync(stdoutDrain, stderrDrain);
    }

    internal int ProcessId => processId;

    internal Task Completion { get; }

    internal int? ExitCode
    {
        get
        {
            var value = Volatile.Read(ref exitCode);
            return value == int.MinValue ? null : value;
        }
    }

    /// <summary>Captured stderr text (bounded to 32 KiB), or null when the caller did not request
    /// capture. The task completes when the stderr drain reaches EOF.</summary>
    internal Task<string>? StandardError => standardError?.Task;

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }

    internal static DenoRunProcess Start(
        ProcessStartInfo startInfo,
        InternalDenoProcessKind kind,
        ILogger logger,
        DenoPermissionBroker? broker = null,
        EffectivePolicy? policy = null,
        bool captureStandardError = false,
        TimeSpan? stopBudget = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(logger);
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"The {Describe(kind)} process could not be started.");
        // Register the policy immediately after spawn: the child can connect to the broker before
        // this call, but the broker's registration slot makes those first requests wait for the
        // policy instead of being denied by default (ADR 0018 §9 readiness invariant). A child
        // launched without a broker (the plugin host, which runs with full Deno permissions and
        // narrows its workers via worker options) skips registration.
        if (broker is not null)
        {
            ArgumentNullException.ThrowIfNull(policy);
            broker.RegisterPolicy(process.Id, policy);
        }

        return new DenoRunProcess(
            process,
            kind,
            logger,
            stopBudget ?? DefaultStopBudget,
            captureStandardError
                ? new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                : null);
    }

    internal Task StopAsync()
    {
        lock (gate)
        {
            return stopping ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        try
        {
            if (!Completion.IsCompleted && !process.HasExited) process.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The process exited between the check and the kill.
        }

        try
        {
            // One total budget for the stop: the kill already happened, so the budget bounds the
            // exit/drain observation. A re-parented grandchild holding an inherited stdout pipe
            // keeps the drain open forever; without the bound every StopAsync caller (plugin host
            // stop, REPL shutdown, session factory, DisposeAsync) would block indefinitely.
            await Completion.WaitAsync(stopBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandon the completion deterministically: observe its eventual failure so nothing
            // surfaces as an unobserved task exception, log through the diagnostics channel, and
            // still dispose below so StopAsync always returns within its budget. ExitCode stays
            // unknown; the process object is the only resource this class owes a dispose.
            _ = Completion.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            logger.LogWarning(
                "{ProcessDescription} {ProcessId} did not drain within {Budget}; abandoning its completion.",
                processDescription,
                processId,
                stopBudget);
        }
        catch
        {
            // Exit observation must not mask shutdown.
        }

        process.Dispose();
    }

    /// <summary>Drains one child stream, emitting each output line at Debug under the
    /// per-stream character budget (the truncated-summary Debug marks the cut when the
    /// budget is spent). Lines split on CR/LF; a final partial line flushes at EOF, and a
    /// line longer than <see cref="MaximumLoggedLineCharacters" /> flushes early so one
    /// logged message stays bounded. The optional capture accumulates independently of the
    /// log budget and is returned to the caller unchanged.</summary>
    private async Task DrainAsync(
        TextReader reader,
        string streamName,
        ILogger logger,
        TaskCompletionSource<string>? capturedOutput)
    {
        var buffer = ArrayPool<char>.Shared.Rent(DrainBufferCharacters);
        var loggingEnabled = logger.IsEnabled(LogLevel.Debug);
        var remainingLogBudget = loggingEnabled ? MaximumLoggedCharactersPerStream : 0;
        var pendingLine = loggingEnabled ? new StringBuilder() : null;
        var truncatedLogged = false;
        var captured = capturedOutput is null ? null : new StringBuilder();

        void FlushLine(StringBuilder line)
        {
            if (line.Length == 0) return;

            logger.LogDebug(
                "{ProcessDescription} {ProcessId} {StreamName}: {Line}",
                processDescription,
                processId,
                streamName,
                line.ToString());
            line.Clear();
        }

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, DrainBufferCharacters)).ConfigureAwait(false);
                if (read == 0) break;

                if (captured is not null && captured.Length < MaximumLoggedCharactersPerStream)
                {
                    var count = Math.Min(read, MaximumLoggedCharactersPerStream - captured.Length);
                    captured.Append(buffer, 0, count);
                }

                if (pendingLine is not { } line) continue; // Debug disabled or the budget was spent.

                var segment = buffer.AsSpan(0, read);
                while (!segment.IsEmpty)
                {
                    var terminator = segment.IndexOfAny('\r', '\n');
                    var complete = terminator >= 0;
                    var lineLength = complete ? terminator : segment.Length;
                    while (lineLength > 0)
                    {
                        if (remainingLogBudget <= 0)
                        {
                            LogStreamSummary(logger, streamName, truncated: true);
                            truncatedLogged = true;
                            break;
                        }

                        var taken = Math.Min(
                            Math.Min(lineLength, remainingLogBudget),
                            MaximumLoggedLineCharacters - line.Length);
                        line.Append(segment[..taken]);
                        remainingLogBudget -= taken;
                        segment = segment[taken..];
                        lineLength -= taken;
                        if (line.Length >= MaximumLoggedLineCharacters) FlushLine(line);
                    }

                    if (lineLength > 0) break; // truncated: the budget no longer covers the line

                    if (!complete) break; // the rest of the segment is a partial line

                    FlushLine(line);
                    segment = segment[1..]; // the terminator
                }

                if (truncatedLogged) pendingLine = null;
            }

            if (pendingLine is { } remainder)
            {
                FlushLine(remainder); // a final partial line without a terminator
                if (!truncatedLogged) LogStreamSummary(logger, streamName, truncated: false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            logger.LogDebug(
                exception,
                "{ProcessDescription} {ProcessId} {StreamName} drain ended before EOF.",
                processDescription,
                processId,
                streamName);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            capturedOutput?.TrySetResult(captured?.ToString() ?? string.Empty);
        }
    }

    private void LogStreamSummary(ILogger logger, string streamName, bool truncated)
    {
        if (!logger.IsEnabled(LogLevel.Debug)) return;

        logger.LogDebug(
            truncated
                ? "{ProcessDescription} {ProcessId} {StreamName} logging was truncated after {CharacterLimit} characters; remaining output is still drained."
                : "{ProcessDescription} {ProcessId} {StreamName} drain completed.",
            processDescription,
            processId,
            streamName,
            MaximumLoggedCharactersPerStream);
    }

    private async Task ObserveCompletionAsync(Task stdoutDrain, Task stderrDrain)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
        Volatile.Write(ref exitCode, process.ExitCode);
    }

    private static string Describe(InternalDenoProcessKind kind)
    {
        return kind switch
        {
            InternalDenoProcessKind.DenoRepl => "Deno REPL",
            InternalDenoProcessKind.PluginHost => "Plugin host",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown internal Deno process kind.")
        };
    }
}
