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

    private readonly DenoReplOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ReplControlHost controlHost = controlHost ?? throw new ArgumentNullException(nameof(controlHost));

    private readonly ReplClientModule clientModule =
        clientModule ?? throw new ArgumentNullException(nameof(clientModule));

    public async Task<IJupyterKernelManager> StartAsync(
        string workingDirectory,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Deno REPL control channel requires named-pipe bootstrap, which is not implemented on Windows yet.");
        }

        var kernelSpec = new JupyterKernelSpec(
            [options.Executable, "jupyter", "--kernel", "--conn", "{connection_file}"],
            "Deno",
            "typescript",
            "signal",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ReplControlEnvironment.IpcAddress] = controlHost.SocketPath,
                [ReplControlEnvironment.ClientModule] = clientModule.ClientUrl,
                [ReplControlEnvironment.SessionId] = sessionId
            });
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
            if (!string.IsNullOrEmpty(value))
            {
                result[name] = value;
            }
        }

        return result;
    }
}