using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Maieutics.Control;

/// <summary>
/// Resolves the peer process identity of a connected unix domain socket. Linux exposes the peer
/// process id; macOS exposes only the peer user id.
/// </summary>
internal static partial class PeerProcessCredentials
{
    /// <summary>
    /// Gets the peer process identity when the platform exposes it.
    /// </summary>
    /// <param name="socket">The accepted unix domain socket.</param>
    /// <param name="processId">The peer process id, or zero when the platform does not expose it.</param>
    /// <param name="userId">The peer effective user id, or zero when unavailable.</param>
    public static bool TryGetPeerIdentity(Socket socket, out int processId, out uint userId)
    {
        processId = 0;
        userId = 0;
        if (socket.AddressFamily != AddressFamily.Unix)
        {
            return false;
        }

        var handle = socket.Handle;
        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxPeerIdentity(handle, out processId, out userId);
        }

        return OperatingSystem.IsMacOS() && TryGetMacOsPeerIdentity(handle, out processId, out userId);
    }

    /// <summary>Gets the effective user id of the current process.</summary>
    public static uint GetCurrentUserId()
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }

        return GetEuid();
    }

    private static bool TryGetLinuxPeerIdentity(IntPtr socketHandle, out int processId, out uint userId)
    {
        var credential = new UCred();
        var size = Unsafe.SizeOf<UCred>();
        if (GetSockOpt(socketHandle, SolSocket, SoPeerCred, ref credential, ref size) != 0 ||
            size != Unsafe.SizeOf<UCred>())
        {
            processId = 0;
            userId = 0;
            return false;
        }

        processId = unchecked((int)credential.Pid);
        userId = credential.Uid;
        return true;
    }

    private static bool TryGetMacOsPeerIdentity(
        IntPtr socketHandle,
        out int processId,
        out uint userId)
    {
        // macOS exposes the peer pid through SOL_LOCAL/LOCAL_PEERPID (baseline 10.14).
        var size = Unsafe.SizeOf<uint>();
        if (GetSockOptPid(socketHandle, SolLocal, LocalPeerPid, out var peerPid, ref size) != 0 ||
            size != Unsafe.SizeOf<uint>())
        {
            processId = 0;
            userId = 0;
            return false;
        }

        processId = unchecked((int)peerPid);
        userId = 0;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UCred
    {
        public uint Pid;
        public uint Uid;
        public uint Gid;
    }

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int SolLocal = 0;
    private const int LocalPeerPid = 0x002;

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSockOpt(
        IntPtr socket,
        int level,
        int optionName,
        ref UCred optionValue,
        ref int optionLength);

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSockOptPid(
        IntPtr socket,
        int level,
        int optionName,
        out uint pid,
        ref int optionLength);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();
}
