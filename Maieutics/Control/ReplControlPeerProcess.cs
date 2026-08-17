using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Maieutics.Control;

/// <summary>Resolves the operating-system process behind a control-channel connection.</summary>
internal static partial class ReplControlPeerProcess
{
    internal static bool TryGetIdentity(HttpContext context, out int processId, out uint userId)
    {
        ArgumentNullException.ThrowIfNull(context);
        processId = 0;
        userId = 0;

        if (OperatingSystem.IsWindows())
        {
            var feature = context.Features.Get<IConnectionNamedPipeFeature>();
            return feature is not null && TryGetNamedPipeClientProcessId(feature.NamedPipe, out processId);
        }

        var socket = context.Features.Get<IConnectionSocketFeature>()?.Socket;
        return socket is not null && PeerProcessCredentials.TryGetPeerIdentity(socket, out processId, out userId);
    }

    internal static int GetProcessId(HttpContext context)
    {
        return TryGetIdentity(context, out var processId, out _) ? processId : 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetNamedPipeClientProcessId(
        System.IO.Pipes.NamedPipeServerStream pipe,
        out int processId)
    {
        var handle = pipe.SafePipeHandle;
        if (!handle.IsInvalid && GetNamedPipeClientProcessId(handle.DangerousGetHandle(), out var value))
        {
            processId = unchecked((int)value);
            return processId > 0;
        }

        processId = 0;
        return false;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
