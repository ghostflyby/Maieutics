using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Maieutics.Control;

/// <summary>
/// Windows bootstrap endpoint: listens on a named pipe, verifies the connecting REPL child
/// through its client process id, and issues a session credential for the loopback TCP control
/// channel. One pipe connection per REPL child; the child learns only the pipe name from its
/// environment.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPipeBootstrap : IWindowsPipeBootstrap, IAsyncDisposable
{
    private readonly ReplControlSessionRegistry sessionRegistry;
    private readonly ReplControlCredentialRegistry credentialRegistry;
    private readonly ILogger<WindowsPipeBootstrap> logger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task loop;

    public WindowsPipeBootstrap(
        string pipeName,
        ReplControlSessionRegistry sessionRegistry,
        ReplControlCredentialRegistry credentialRegistry,
        ILogger<WindowsPipeBootstrap> logger)
    {
        PipeName = pipeName;
        this.sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        this.credentialRegistry = credentialRegistry ?? throw new ArgumentNullException(nameof(credentialRegistry));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        loop = RunAsync();
    }

    public string PipeName { get; }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected at shutdown.
        }
    }

    private async Task RunAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await ServeConnectionAsync(pipe).ConfigureAwait(false);
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe)
    {
        var processId = GetClientProcessId(pipe);
        if (processId <= 0 || !sessionRegistry.TryGetSession(processId, out var sessionId))
        {
            logger.LogWarning(
                "Rejected named-pipe bootstrap from an unexpected process (pid {ProcessId}).",
                processId);
            return;
        }

        var credential = credentialRegistry.Issue(sessionId);
        var payload = Encoding.UTF8.GetBytes($"{{\"sessionId\":\"{sessionId}\",\"credential\":\"{credential}\"}}");
        await pipe.WriteAsync(payload, lifetime.Token).ConfigureAwait(false);
        await pipe.FlushAsync(lifetime.Token).ConfigureAwait(false);
        logger.LogInformation("Issued a control credential to session {SessionId}.", sessionId);
    }

    private static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle.DangerousGetHandle();
        if (GetNamedPipeClientProcessId(handle, out var processId))
        {
            return unchecked((int)processId);
        }

        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetNamedPipeClientProcessId(
        IntPtr pipeHandle,
        out uint clientProcessId);
}
