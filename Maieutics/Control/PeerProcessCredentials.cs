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

        if (OperatingSystem.IsMacOS())
        {
            return TryGetMacOsPeerIdentity(handle, out userId);
        }

        return false;
    }

    /// <summary>Gets the effective user id of the current process.</summary>
    public static uint GetCurrentUserId()
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }

        return Geteuid();
    }

    private static bool TryGetLinuxPeerIdentity(IntPtr socketHandle, out int processId, out uint userId)
    {
        var credential = new UCred();
        var size = Unsafe.SizeOf<UCred>();
        if (Getsockopt(socketHandle, SolSocket, SoPeerCred, ref credential, ref size) != 0 ||
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

    private static bool TryGetMacOsPeerIdentity(IntPtr socketHandle, out uint userId)
    {
        if (Getpeereid(socketHandle, out var uid, out _) != 0)
        {
            userId = 0;
            return false;
        }

        userId = uid;
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

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int Getsockopt(
        IntPtr socket,
        int level,
        int optionName,
        ref UCred optionValue,
        ref int optionLength);

    [DllImport("libc", EntryPoint = "getpeereid", SetLastError = true)]
    private static extern int Getpeereid(IntPtr socket, out uint euid, out uint egid);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();
}
