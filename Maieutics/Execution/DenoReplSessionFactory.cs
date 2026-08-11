using Maieutics.Control;
using Maieutics.Jupyter.Client;

namespace Maieutics.Execution;

internal interface IDenoReplSessionFactory
{
    Task<IJupyterKernelManager> StartAsync(
        string workingDirectory,
        string sessionId,
        CancellationToken cancellationToken);
}

internal sealed class LocalDenoReplSessionFactory(
    DenoReplOptions options,
    ReplControlHost controlHost,
    ReplClientModule clientModule)
    : IDenoReplSessionFactory
{
    private static readonly string[] AllowedEnvironmentNames =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "LOCALAPPDATA",
        "APPDATA",
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

    private readonly ReplClientModule clientModule =
        clientModule ?? throw new ArgumentNullException(nameof(clientModule));

    private readonly ReplControlHost controlHost = controlHost ?? throw new ArgumentNullException(nameof(controlHost));

    private readonly DenoReplOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<IJupyterKernelManager> StartAsync(
        string workingDirectory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReplControlEnvironment.IpcAddress] = controlHost.ControlAddress,
            [ReplControlEnvironment.ClientModule] = clientModule.ClientUrl,
            [ReplControlEnvironment.SessionId] = sessionId
        };
        if (OperatingSystem.IsWindows())
        {
            if (controlHost.WindowsPipeName is not { } pipeName)
                throw new PlatformNotSupportedException(
                    "The Windows named-pipe bootstrap is not wired into the application host.");

            environment[ReplControlEnvironment.PipeName] = pipeName;
        }

        var kernelSpec = new JupyterKernelSpec(
            [options.Executable, "jupyter", "--kernel", "--conn", "{connection_file}"],
            "Deno",
            "typescript",
            "signal",
            environment);
        return await LocalJupyterKernelManager.StartAsync(
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
    }

    private static IReadOnlyDictionary<string, string> CaptureAllowedEnvironment()
    {
        var result = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in AllowedEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) result[name] = value;
        }

        return result;
    }
}
