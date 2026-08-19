using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Maieutics.Control;
using Maieutics.Permissions;
using Microsoft.Extensions.Logging;

namespace Maieutics.DenoExecution;

/// <summary>
///     The .NET side of the official Deno permission broker (<c>DENO_PERMISSION_BROKER_PATH</c>,
///     JSON-lines protocol, version 1; verified against Deno 2.9.5). When the env var is set, the
///     broker is the <b>single authority</b> for the child's permission checks: even launch-time
///     <c>--allow-*</c> flags do not short-circuit broker requests (verified: a child launched with
///     <c>--allow-env=PATH</c> still produced a broker request and the broker's deny won). Requests
///     arrive on a unix socket (peer-pid attributed like the control channel) or a Windows named
///     pipe; each request is resolved against the policy the owner registered for that process via
///     <see cref="RegisterPolicy"/> — exact allow → allow, exact deny → deny with the policy
///     reason, otherwise deny by default. The broker never prompts.
/// </summary>
internal sealed class DenoPermissionBroker : IAsyncDisposable
{
    private const int EnvelopeVersion = 1;
    private const int MaximumRequestLineBytes = 16 * 1024;
    private const int MaximumPendingConnections = 8;
    private const int ReadBufferBytes = 4 * 1024;
    private static readonly TimeSpan PolicyRegistrationWait = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ILogger<DenoPermissionBroker> logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<EffectivePolicy>> registrations = new();
    private readonly string? socketPath;
    private readonly string? pipeName;
    private Task loop = Task.CompletedTask;
    private Socket? unixListener;
    private NamedPipeServerStream? pipeListener;

    private DenoPermissionBroker(
        string? socketPath,
        string? pipeName,
        ILogger<DenoPermissionBroker> logger)
    {
        this.socketPath = socketPath;
        this.pipeName = pipeName;
        this.logger = logger;
    }

    /// <summary>Address REPL and plugin-host children use to reach the broker via
    /// <c>DENO_PERMISSION_BROKER_PATH</c>: a unix socket path on unix, the full named-pipe path
    /// (<c>\\.\pipe\name</c>) on Windows — Deno's broker client connects to the pipe by path, so
    /// the bare pipe name alone fails with <c>NotFound</c> (verified in CI).</summary>
    public string Address
    {
        get
        {
            // The listener is bound synchronously by Create before this is ever handed to a
            // child, so the address is unconditionally safe to spawn against.
            return socketPath ?? (pipeName is { } name ? $@"\\.\pipe\{name}" : string.Empty);
        }
    }

    /// <summary>Creates the broker with its listener already bound (unix socket or Windows named
    /// pipe). The accept loop runs in the background; the returned broker is ready to serve
    /// immediately, so no child can be spawned against a broker that is not yet listening.</summary>
    internal static DenoPermissionBroker Create(ILogger<DenoPermissionBroker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (OperatingSystem.IsWindows())
        {
            var windowsBroker = new DenoPermissionBroker(null, $"maieutics-broker-{Guid.NewGuid():N}", logger);
            windowsBroker.pipeListener = new NamedPipeServerStream(
                windowsBroker.pipeName!,
                PipeDirection.InOut,
                MaximumPendingConnections,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            windowsBroker.loop = windowsBroker.AcceptLoopAsync();
            return windowsBroker;
        }

        var socketPath = CreateSocketPath();
        var broker = new DenoPermissionBroker(socketPath, null, logger);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(MaximumPendingConnections);
        broker.unixListener = listener;
        broker.loop = broker.AcceptLoopAsync();
        return broker;
    }

    /// <summary>Completes the registration slot with the effective policy for one child, captured
    /// once at launch (AGENTS.md invariant 19: the policy never changes mid-operation). A request
    /// that arrives before registration waits on the slot (see <see cref="GetPolicyAsync"/>), so
    /// there is no spawn-to-register window.</summary>
    internal void RegisterPolicy(int processId, EffectivePolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(policy);
        var slot = registrations.GetOrAdd(
            processId,
            static _ => new TaskCompletionSource<EffectivePolicy>(TaskCreationOptions.RunContinuationsAsynchronously));
        slot.TrySetResult(policy);
    }

    internal void UnregisterProcess(int processId)
    {
        if (!registrations.TryRemove(processId, out var slot)) return;

        slot.TrySetCanceled();
    }

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

        if (socketPath is not null)
            try
            {
                File.Delete(socketPath);
            }
            catch
            {
                // Best-effort socket cleanup must not mask shutdown.
            }
    }

    private static string CreateSocketPath()
    {
        var name = $"mc-broker-{Guid.NewGuid():N}"[..24] + ".sock";
        return Path.Combine(Path.GetTempPath(), name);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                while (!lifetime.IsCancellationRequested)
                {
                    try
                    {
                        await pipeListener!.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    _ = ServePipeConnectionAsync(pipeListener, lifetime.Token);
                    pipeListener = new NamedPipeServerStream(
                        pipeName!,
                        PipeDirection.InOut,
                        MaximumPendingConnections,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }

                return;
            }

            var listener = unixListener!;
            while (!lifetime.IsCancellationRequested)
            {
                var connection = await listener.AcceptAsync(lifetime.Token).ConfigureAwait(false);
                _ = ServeSocketConnectionAsync(connection, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            unixListener?.Dispose();
        }
    }

    /// <summary>Waits (signal-driven, bounded) for the effective policy registered for a process.
    /// A request may arrive before the owner registers (the child connects immediately after
    /// spawn), so an unknown pid gets a pending slot and waits for the policy instead of being
    /// denied by default; if the policy never arrives within the bound, the caller denies by
    /// default.</summary>
    private async Task<EffectivePolicy> GetPolicyAsync(int processId, CancellationToken cancellationToken)
    {
        var slot = registrations.GetOrAdd(
            processId,
            static _ => new TaskCompletionSource<EffectivePolicy>(TaskCreationOptions.RunContinuationsAsynchronously));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PolicyRegistrationWait);
        try
        {
            return await slot.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "Permission broker policy for process {ProcessId} did not arrive within {Wait}; denying by default.",
                processId,
                PolicyRegistrationWait);
            return EffectivePolicy.Default;
        }
    }

    private async Task ServeSocketConnectionAsync(Socket connection, CancellationToken cancellationToken)
    {
        try
        {
            if (!PeerProcessCredentials.TryGetPeerIdentity(connection, out var processId, out _) ||
                processId <= 0)
            {
                logger.LogWarning("Rejected permission broker connection with an unexpected peer identity.");
                return;
            }

            var policy = await GetPolicyAsync(processId, cancellationToken).ConfigureAwait(false);
            await ServeConnectionAsync(new NetworkStream(connection, ownsSocket: false), policy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            logger.LogDebug(exception, "Permission broker connection ended before EOF.");
        }
        finally
        {
            connection.Dispose();
        }
    }

    private async Task ServePipeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            var processId = GetClientProcessId(pipe);
            if (processId <= 0)
            {
                logger.LogWarning("Rejected permission broker named-pipe connection with an unexpected peer identity.");
                return;
            }

            var policy = await GetPolicyAsync(processId, cancellationToken).ConfigureAwait(false);
            await ServeConnectionAsync(pipe, policy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            logger.LogDebug(exception, "Permission broker named-pipe connection ended before EOF.");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle.DangerousGetHandle();
        return GetNamedPipeClientProcessId(handle, out var processId) ? unchecked((int)processId) : 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipeHandle, out uint clientProcessId);

    private async Task ServeConnectionAsync(
        Stream stream,
        EffectivePolicy policy,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferBytes];
        var pending = new List<byte>(ReadBufferBytes);
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;

            pending.AddRange(buffer.AsSpan(0, read).ToArray());
            if (pending.Count > MaximumRequestLineBytes)
            {
                // A request line that never terminates within the bound cannot be a valid
                // permission request; drop the connection's buffered input.
                pending.Clear();
                continue;
            }

            var start = 0;
            while (true)
            {
                var newline = pending.IndexOf((byte)'\n', start);
                if (newline < 0) break;

                var line = pending.GetRange(start, newline - start);
                start = newline + 1;
                if (line.Count > 0)
                    await HandleRequestAsync(stream, line.ToArray(), policy, cancellationToken).ConfigureAwait(false);
            }

            if (start > 0) pending.RemoveRange(0, start);
        }
    }

    private async Task HandleRequestAsync(
        Stream stream,
        byte[] line,
        EffectivePolicy policy,
        CancellationToken cancellationToken)
    {
        if (line.Length == 0) return;

        DenoBrokerRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, DenoBrokerJsonContext.Default.DenoBrokerRequest);
        }
        catch (JsonException)
        {
            logger.LogDebug("Ignored a malformed permission broker request.");
            return;
        }

        if (request is null ||
            request.Version != EnvelopeVersion ||
            request.Id is null ||
            string.IsNullOrWhiteSpace(request.Permission))
            return;

        var decision = DenoPermissionResolver.Resolve(policy, request.Permission, request.Value ?? string.Empty);
        var response = new DenoBrokerResponse(request.Id.Value, decision);
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, DenoBrokerJsonContext.Default.DenoBrokerResponse);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Permission broker {Decision} {Permission} {Value} for process.",
            decision.IsAllowed ? "allowed" : "denied",
            request.Permission,
            request.Value);
    }
}
