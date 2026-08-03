using Maieutics.Jupyter.Client;
using Maieutics.Control;
using Microsoft.Extensions.Logging;

namespace Maieutics.Execution;

internal interface IDenoReplSessionFactory
{
    Task<DenoReplStartResult> StartAsync(
        string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class LocalDenoReplSessionFactory : IDenoReplSessionFactory
{
    private static readonly string[] AllowedEnvironmentNames =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "TMPDIR",
        "TMP",
        "TEMP",
        "DENO_DIR",
        "XDG_CACHE_HOME",
        "LANG",
        "LC_ALL",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "PATHEXT"
    ];

    private readonly DenoReplOptions options;
    private readonly ILogger<ReplControlHost> logger;

    public LocalDenoReplSessionFactory(DenoReplOptions options, ILogger<ReplControlHost> logger)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DenoReplStartResult> StartAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Deno REPL control channel requires named-pipe bootstrap, which is not implemented on Windows yet.");
        }

        var socketPath = ReplControlHost.CreateSocketPath();
        var kernelSpec = new JupyterKernelSpec(
            [options.Executable, "jupyter", "--kernel", "--conn", "{connection_file}"],
            "Deno",
            "typescript",
            "signal",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MaieuticsReplIpcEnvironmentVariable] = socketPath
            });
        var manager = await LocalJupyterKernelManager.StartAsync(
            kernelSpec,
            new LocalJupyterKernelManagerOptions
            {
                WorkingDirectory = workingDirectory,
                ClearInheritedEnvironment = true,
                Environment = CaptureAllowedEnvironment(),
                StartupTimeout = options.StartupTimeout,
                ShutdownTimeout = options.ShutdownTimeout
            },
            cancellationToken).ConfigureAwait(false);

        ReplControlHost? controlChannel = null;
        try
        {
            var processId = manager.ProcessId
                ?? throw new InvalidOperationException("The Deno REPL child process id is unavailable.");
            controlChannel = await ReplControlHost.StartAsync(
                socketPath,
                processId,
                logger,
                cancellationToken).ConfigureAwait(false);
            return new DenoReplStartResult(manager, controlChannel);
        }
        catch
        {
            if (controlChannel is not null)
            {
                await controlChannel.DisposeAsync().ConfigureAwait(false);
            }

            await manager.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal const string MaieuticsReplIpcEnvironmentVariable = "MAIEUTICS_REPL_IPC";

    private static IReadOnlyDictionary<string, string> CaptureAllowedEnvironment()
    {
        var result = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in AllowedEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                result[name] = value;
            }
        }

        return result;
    }
}
