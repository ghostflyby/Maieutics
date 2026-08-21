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
    private readonly Lock gate = new();
    private readonly Process process;
    private readonly string processDescription;
    private readonly int processId;
    private readonly TaskCompletionSource<string>? standardError;
    private int exitCode = int.MinValue;
    private Task? stopping;

    private DenoRunProcess(
        Process process,
        InternalDenoProcessKind kind,
        ILogger logger,
        TaskCompletionSource<string>? standardError)
    {
        this.process = process;
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
        bool captureStandardError = false)
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
            await Completion.ConfigureAwait(false);
        }
        catch
        {
            // Exit observation must not mask shutdown.
        }

        process.Dispose();
    }

    private async Task DrainAsync(
        TextReader reader,
        string streamName,
        ILogger logger,
        TaskCompletionSource<string>? capturedOutput)
    {
        var buffer = ArrayPool<char>.Shared.Rent(DrainBufferCharacters);
        var remainingLogBudget = logger.IsEnabled(LogLevel.Debug) ? MaximumLoggedCharactersPerStream : 0;
        var captured = capturedOutput is null ? null : new StringBuilder();
        var streamCompleted = false;
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

                if (remainingLogBudget <= 0)
                {
                    if (!streamCompleted && remainingLogBudget == 0)
                    {
                        LogStreamSummary(logger, streamName, truncated: true);
                        streamCompleted = true;
                    }

                    continue;
                }

                var loggedCount = Math.Min(read, remainingLogBudget);
                remainingLogBudget -= loggedCount;
                if (loggedCount >= read) continue;

                LogStreamSummary(logger, streamName, truncated: true);
                streamCompleted = true;
            }

            if (!streamCompleted)
                LogStreamSummary(logger, streamName, truncated: false);
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
