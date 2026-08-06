using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Maieutics.Control;

/// <summary>
///     Resolves the peer process identity of a connected unix domain socket. Linux exposes the peer
///     process id; macOS exposes only the peer user id.
/// </summary>
internal static partial class PeerProcessCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int SolLocal = 0;
    private const int LocalPeerPid = 0x002;

    /// <summary>
    ///     Gets the peer process identity when the platform exposes it.
    /// </summary>
    /// <param name="socket">The accepted unix domain socket.</param>
    /// <param name="processId">The peer process id, or zero when the platform does not expose it.</param>
    /// <param name="userId">The peer effective user id, or zero when unavailable.</param>
    public static bool TryGetPeerIdentity(Socket socket, out int processId, out uint userId)
    {
        processId = 0;
        userId = 0;
        if (socket.AddressFamily != AddressFamily.Unix) return false;

        var handle = socket.Handle;
        if (OperatingSystem.IsLinux()) return TryGetLinuxPeerIdentity(handle, out processId, out userId);

        return OperatingSystem.IsMacOS() && TryGetMacOsPeerIdentity(handle, out processId, out userId);
    }

    /// <summary>Gets the effective user id of the current process.</summary>
    public static uint GetCurrentUserId()
    {
        if (OperatingSystem.IsWindows()) return 0;

        return GetEuid();
    }

    [SupportedOSPlatform("Linux")]
    private static unsafe bool TryGetLinuxPeerIdentity(IntPtr socketHandle, out int processId, out uint userId)
    {
        var credential = new UCred();
        var size = (uint)sizeof(UCred);
        if (GetSockOpt(socketHandle, SolSocket, SoPeerCred, ref credential, in size) != 0)
        {
            processId = 0;
            userId = 0;
            return false;
        }

        processId = unchecked((int)credential.Pid);
        userId = credential.Uid;
        return true;
    }

    [SupportedOSPlatform("macOS")]
    private static bool TryGetMacOsPeerIdentity(
        IntPtr socketHandle,
        out int processId,
        out uint userId)
    {
        // macOS exposes the peer pid through SOL_LOCAL/LOCAL_PEERPID (baseline 10.14);
        // pid_t is a signed 32-bit type and socklen_t is unsigned.
        uint size = sizeof(uint);
        if (GetSockOptPid(socketHandle, SolLocal, LocalPeerPid, out var peerPid, in size) != 0)
        {
            processId = 0;
            userId = 0;
            return false;
        }

        processId = peerPid;
        userId = 0;
        return true;
    }


    [SupportedOSPlatform("Linux")]
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSockOpt(
        IntPtr socket,
        int level,
        int optionName,
        ref UCred optionValue,
        in uint optionLength);

    [SupportedOSPlatform("macOS")]
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSockOptPid(
        IntPtr socket,
        int level,
        int optionName,
        out int pid,
        in uint optionLength);

    [UnsupportedOSPlatform("Windows")]
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    [StructLayout(LayoutKind.Sequential)]
    private struct UCred
    {
        public uint Pid;
        public uint Uid;
        public uint Gid;
    }
}