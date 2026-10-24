using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Maieutics.Control;

/// <summary>
///     Windows bootstrap endpoint: listens on a named pipe, verifies the connecting REPL child
///     through its client process id, and issues a session credential for the loopback TCP control
///     channel. One pipe connection per REPL child; the child learns only the pipe name from its
///     environment.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPipeBootstrap : IWindowsPipeBootstrap, IAsyncDisposable
{
    private readonly ReplControlCredentialRegistry _credentialRegistry;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<WindowsPipeBootstrap> _logger;
    private readonly Task _loop;
    private readonly ReplControlSessionRegistry _sessionRegistry;

    public WindowsPipeBootstrap(
        string pipeName,
        ReplControlSessionRegistry sessionRegistry,
        ReplControlCredentialRegistry credentialRegistry,
        ILogger<WindowsPipeBootstrap> logger)
    {
        PipeName = pipeName;
        this._sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        this._credentialRegistry = credentialRegistry ?? throw new ArgumentNullException(nameof(credentialRegistry));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loop = RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected at shutdown.
        }
    }

    public string PipeName { get; }

    private async Task RunAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await ServeConnectionAsync(pipe).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // One client vanishing mid-bootstrap must not fault the accept loop: later
                // REPL children would never receive a control credential. The pipe instance
                // is disposed by the await using above; keep serving.
                _logger.LogWarning(exception, "Named-pipe bootstrap of a control credential failed.");
            }
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe)
    {
        var processId = GetClientProcessId(pipe);
        if (processId <= 0 || !_sessionRegistry.TryGetSession(processId, out var sessionId))
        {
            _logger.LogWarning(
                "Rejected named-pipe bootstrap from an unexpected process (pid {ProcessId}).",
                processId);
            return;
        }

        var credential = _credentialRegistry.Issue(sessionId);
        var payload = Encoding.UTF8.GetBytes($"{{\"sessionId\":\"{sessionId}\",\"credential\":\"{credential}\"}}");
        await pipe.WriteAsync(payload, _lifetime.Token).ConfigureAwait(false);
        await pipe.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        _logger.LogInformation("Issued a control credential to session {SessionId}.", sessionId);
    }

    private static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle.DangerousGetHandle();
        if (GetNamedPipeClientProcessId(handle, out var processId)) return unchecked((int)processId);

        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetNamedPipeClientProcessId(
        IntPtr pipeHandle,
        out uint clientProcessId);
}
