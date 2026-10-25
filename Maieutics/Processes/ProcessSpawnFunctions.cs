using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maieutics.Agent;
using Maieutics.Execution;
using Maieutics.Permissions;
using Microsoft.Extensions.AI;
using ChildProcess = System.Diagnostics.Process;

namespace Maieutics.Processes;

/// <summary>
///     The kernel-side spawn surface for processes requested from inside a Deno child (ADR
///     0045): the REPL's <c>Deno.Command</c> polyfill routes every spawn here, the
///     <see cref="IProcessSandboxPolicy"/> turns the calling session's effective policy into
///     the launch vector (run-gated, environment-allowlisted, sandbox-wrapped when a launcher
///     exists), and this tool owns the child's whole lifetime — bounded output capture, a
///     declarative timeout that kills the process tree, cooperative cancellation, and strict
///     UTF-8 decoding with typed failures. The tool is registered only when the sandbox mode
///     is not <see cref="ProcessSandboxMode.Off"/>; its absence is the polyfill's per-call
///     signal to keep the child's native Deno permission path.
/// </summary>
internal sealed class ProcessSpawnFunctions
{
    internal const int DefaultTimeoutMs = 60_000;
    internal const int MaximumTimeoutMs = 600_000;
    internal const int MaximumOutputBytesPerStream = 256 * 1_024;
    internal const int MaximumStdinBytes = 64 * 1_024;
    internal const int MaximumArguments = 256;
    internal const int MaximumArgumentCharacters = 32_768;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(ProcessSpawnJsonSerializerContext.Default.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private readonly IProcessSandboxPolicy _sandbox;
    private readonly PermissionPolicyAcquirer _acquirer;
    private readonly Workspace _workspace;

    internal ProcessSpawnFunctions(
        IProcessSandboxPolicy sandbox,
        PermissionPolicyAcquirer acquirer,
        Workspace workspace)
    {
        this._sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        this._acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        this._workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Functions =
        [
            CreateFunction(
                (Func<string, string[]?, string?, int?, AIFunctionArguments, CancellationToken, ValueTask<ProcessSpawnResult>>)
                    SpawnAsync,
                "process_spawn",
                "Runs one executable with arguments on behalf of the calling session's Deno child: " +
                "the session's permission policy gates the executable and shapes the sandbox, the " +
                "environment comes from the policy's env allowlist, stdout and stderr are captured " +
                "as UTF-8 text with per-stream size bounds, and a timeout kills the process tree. " +
                "Binary output is not supported (redirect it to a file); interactive processes " +
                "belong to the terminal tools.")
        ];
    }

    internal IReadOnlyList<AIFunction> Functions { get; }

    private static AIFunction CreateFunction(Delegate method, string name, string description)
    {
        return AIFunctionFactory.Create(
            method,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                SerializerOptions = SerializerOptions
            });
    }

    [Description("Runs one sandboxed process and captures its output.")]
    private async ValueTask<ProcessSpawnResult> SpawnAsync(
        [Description("The executable to run: a bare command name or an absolute path.")] string executable,
        [Description("The command-line arguments, at most 256.")]
        string[]? arguments = null,
        [Description("UTF-8 text written to the child's stdin before it is closed, at most 64 KiB.")]
        string? stdin = null,
        [Description("The timeout in milliseconds after which the process tree is killed, 1 through 600000. Defaults to 60000.")]
        int? timeoutMs = null,
        AIFunctionArguments toolArguments = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var requestedArguments = arguments ?? [];
        if (requestedArguments.Length > MaximumArguments)
            throw new AgentToolException(
                "process_spawn_invalid_arguments",
                $"At most {MaximumArguments} arguments are accepted.");
        foreach (var argument in requestedArguments)
        {
            if (argument.Length > MaximumArgumentCharacters)
                throw new AgentToolException(
                    "process_spawn_invalid_arguments",
                    $"An argument exceeds {MaximumArgumentCharacters} characters.");
        }

        var requestedTimeout = timeoutMs ?? DefaultTimeoutMs;
        if (requestedTimeout is < 1 or > MaximumTimeoutMs)
            throw new AgentToolException(
                "process_spawn_invalid_arguments",
                $"timeoutMs must be between 1 and {MaximumTimeoutMs}.");
        var stdinBytes = Array.Empty<byte>();
        if (stdin is { Length: > 0 })
        {
            stdinBytes = EncodeStdin(stdin);
            if (stdinBytes.Length > MaximumStdinBytes)
                throw new AgentToolException(
                    "process_spawn_invalid_arguments",
                    $"stdin exceeds {MaximumStdinBytes} UTF-8 bytes.");
        }

        var session = WorkspaceToolCalls.TryGetSessionId(toolArguments) is { } text &&
                      AgentSessionId.TryParse(text, out var parsed)
            ? parsed
            : (AgentSessionId?)null;
        // The spawn surface's baseline matches the model-tool posture (the workspace root is
        // readable and writable): a sandboxed child must at least resolve its own working
        // directory, and a build tool that cannot write its tree is useless — configuration
        // denials subtract from the baseline as everywhere else.
        var root = _workspace.Capture().RootPath;
        var policy = _acquirer.Acquire(WorkspacePermissionSurface.ModelToolBaseline(root), session);
        var plan = _sandbox.Plan(policy, executable, requestedArguments);
        if (plan is ProcessLaunchPlan.DeniedPlan denied)
            throw new AgentToolException("process_spawn_denied", denied.Reason);

        var launch = (ProcessLaunchPlan.LaunchPlan)plan;
        return await RunAsync(
                launch,
                stdinBytes,
                TimeSpan.FromMilliseconds(requestedTimeout),
                root,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Owns the child for one spawn: starts the planned vector with the planned
    /// environment, streams both outputs into bounded buffers, kills the whole tree on the
    /// timeout, an output cap, or cancellation, and decodes strictly. The launch vector is
    /// trusted kernel-planned argv (positional, no shell), so no quoting rules apply.</summary>
    private static async Task<ProcessSpawnResult> RunAsync(
        ProcessLaunchPlan.LaunchPlan launch,
        byte[] stdinBytes,
        TimeSpan timeout,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Argv[0],
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var argument in launch.Argv.Skip(1)) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var (name, value) in launch.Environment)
            if (!string.IsNullOrEmpty(value))
                startInfo.Environment[name] = value;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var killToken = timeoutSource.Token;

        using var process = new ChildProcess { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new AgentToolException("process_spawn_failed", "The process could not be started.");

        var stdout = CaptureAsync(process, process.StandardOutput.BaseStream);
        var stderr = CaptureAsync(process, process.StandardError.BaseStream);
        var exitTask = WaitForExitAsync(process, killToken);

        if (stdinBytes.Length > 0)
        {
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(stdinBytes, killToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The child closed its stdin early or died; the exit task reports the outcome.
            }
        }

        try
        {
            await process.StandardInput.BaseStream.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        var exited = await exitTask.ConfigureAwait(false);
        var stdoutBytes = await stdout.ConfigureAwait(false);
        var stderrBytes = await stderr.ConfigureAwait(false);

        if (!exited)
        {
            // The timeout (or the caller's cancellation) fired: the tree is killed by the
            // exit task; the captured prefix is the honest partial output.
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            return new ProcessSpawnResult(
                null,
                TimedOut: true,
                OutputTruncated: stdoutBytes.Truncated || stderrBytes.Truncated,
                launch.Sandboxed,
                DecodeOutput(stdoutBytes.Buffer, "stdout"),
                DecodeOutput(stderrBytes.Buffer, "stderr"));
        }

        return new ProcessSpawnResult(
            process.ExitCode,
            TimedOut: false,
            OutputTruncated: stdoutBytes.Truncated || stderrBytes.Truncated,
            launch.Sandboxed,
            DecodeOutput(stdoutBytes.Buffer, "stdout"),
            DecodeOutput(stderrBytes.Buffer, "stderr"));
    }

    /// <summary>Waits for the child's exit, killing the whole process tree when the token
    /// fires first (the sandbox wrapper's children die with it).</summary>
    private static async Task<bool> WaitForExitAsync(ChildProcess process, CancellationToken killToken)
    {
        try
        {
            await process.WaitForExitAsync(killToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return false;
        }
    }

    /// <summary>Reads one output stream to EOF or the byte cap; the cap kills the process tree
    /// and the captured prefix returns as truncated.</summary>
    private static async Task<(byte[] Buffer, bool Truncated)> CaptureAsync(
        ChildProcess process,
        Stream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), CancellationToken.None)
                    .ConfigureAwait(false);
                if (read == 0) return (buffer.ToArray(), Truncated: false);
                if (buffer.Length + read > MaximumOutputBytesPerStream)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone.
                    }

                    return (buffer.ToArray(), Truncated: true);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return (buffer.ToArray(), Truncated: false);
        }
    }

    private static byte[] EncodeStdin(string stdin)
    {
        try
        {
            return StrictUtf8.GetBytes(stdin);
        }
        catch (EncoderFallbackException exception)
        {
            throw new AgentToolException(
                "process_spawn_invalid_arguments",
                $"stdin is not valid UTF-8 text. ({exception.Message})");
        }
    }

    private static string DecodeOutput(byte[] bytes, string stream)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new AgentToolException(
                "process_spawn_output_not_utf8",
                $"The process's {stream} is not valid UTF-8 text; redirect binary output to a file. ({exception.Message})");
        }
    }
}

/// <summary>Result of one spawned process: the exit code (null when the timeout killed it),
/// whether the timeout fired, whether an output stream hit the size cap, whether the platform
/// sandbox wrapped the launch, and the captured UTF-8 text.</summary>
internal sealed record ProcessSpawnResult(
    int? ExitCode,
    bool TimedOut,
    bool OutputTruncated,
    bool Sandboxed,
    string Stdout,
    string Stderr);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(ProcessSpawnResult))]
internal sealed partial class ProcessSpawnJsonSerializerContext : JsonSerializerContext;
