using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Maieutics.Execution;

/// <summary>A file-system identity that survives renames: the device/volume and
/// inode/file-index pair a directory keeps for its lifetime on one volume, optionally
/// paired with its creation time (ADR 0027 amendment). Birth time is null on platforms or
/// file systems that do not report one.</summary>
internal sealed record WorkspaceFileIdentity(ulong Device, ulong Inode, DateTimeOffset? BirthTimeUtc)
{
    /// <summary>Whether this identity and <paramref name="other"/> can describe the same
    /// object. Birth times compare only when both sides report one, so a capture that
    /// lacks the creation time never turns a real match into a conflict.</summary>
    internal bool Matches(WorkspaceFileIdentity? other)
    {
        return other is not null &&
               Device == other.Device &&
               Inode == other.Inode &&
               (BirthTimeUtc is null || other.BirthTimeUtc is null ||
                BirthTimeUtc.Equals(other.BirthTimeUtc));
    }
}

/// <summary>Captures <see cref="WorkspaceFileIdentity"/> for directories. Every failure —
/// an unsupported file system, a vanished directory, a reparse point (the opens are
/// no-follow), or a platform without the identity primitives — returns null and the caller
/// falls back to path-only identity, the pre-amendment ADR 0027 semantics.</summary>
internal static partial class WorkspaceFileIdentityReader
{
    internal static WorkspaceFileIdentity? TryRead(string directoryPath)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return TryReadWindows(directoryPath);
            if (OperatingSystem.IsMacOS()) return TryReadMacOS(directoryPath);
            if (OperatingSystem.IsLinux()) return TryReadLinux(directoryPath);
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            DirectoryNotFoundException or
            FileNotFoundException or
            DllNotFoundException or
            EntryPointNotFoundException or
            Win32Exception or
            ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static WorkspaceFileIdentity? TryReadMacOS(string directoryPath)
    {
        var descriptor = OpenUnix(
            directoryPath,
            UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
        if (descriptor < 0) return null;

        try
        {
            if (FStat(descriptor, out var stat) != 0) return null;
            return new WorkspaceFileIdentity(
                stat.Device,
                stat.Inode,
                BirthTime(stat.BirthSeconds, stat.BirthNanoseconds));
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private static WorkspaceFileIdentity? TryReadLinux(string directoryPath)
    {
        if (StatX(
                directoryPath,
                AtSymlinkNoFollow,
                StatXBasicStats | StatXBirthTime,
                out var stat) != 0)
            return null;

        return new WorkspaceFileIdentity(
            ((ulong)stat.DeviceMajor << 32) | stat.DeviceMinor,
            stat.Inode,
            BirthTime(stat.BirthSeconds, stat.BirthNanoseconds));
    }

    private static WorkspaceFileIdentity? TryReadWindows(string directoryPath)
    {
        using var handle = CreateFile(
            directoryPath,
            FileReadAttributes,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid) return null;
        if (!GetFileInformationByHandle(handle, out var info)) return null;

        // A zero file index means the volume cannot express an identity (FAT-family);
        // a (volume, 0) pair would falsely match every other directory on the volume.
        var inode = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        if (inode == 0) return null;

        return new WorkspaceFileIdentity(
            info.VolumeSerialNumber,
            inode,
            info.CreationTime == 0 ? null : FromFileTime(info.CreationTime));
    }

    private static DateTimeOffset? BirthTime(long seconds, long nanoseconds)
    {
        // The Unix epoch bounds for DateTimeOffset; an out-of-range read is a layout or
        // file-system artifact, not a creation time, and degrades to no birth time.
        if (seconds is < -62_167_219_200L or > 253_402_300_799L) return null;
        _ = nanoseconds;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static DateTimeOffset? FromFileTime(ulong fileTime)
    {
        try
        {
            return DateTimeOffset.FromFileTime(unchecked((long)fileTime));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatXBasicStats = 0x7FF;
    private const uint StatXBirthTime = 0x800;

    private const int FileReadAttributes = 0x00000080;
    private const int FileShareReadWriteDelete = 0x00000007;
    private const int OpenExisting = 3;
    private const int FileFlagBackupSemantics = 0x02000000;

    /// <summary>The struct the macOS <c>fstat</c> writes: 144 bytes with 16-byte timespecs;
    /// only the identity fields are read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct StatMacOS
    {
        [FieldOffset(0)] public uint Device;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public long BirthNanoseconds;
    }

    /// <summary>The buffer the Linux <c>statx</c> syscall writes. The kernel copies its own
    /// fixed-size struct, so the buffer reserves 256 bytes (0x100) covering every kernel
    /// version; a future larger struct fails the call and degrades to path-only.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x100)]
    internal struct StatLinux
    {
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct ByHandleFileInformation
    {
        public uint Attributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [Flags]
    private enum UnixOpenFlags
    {
        None = 0,
        Directory = 1,
        NoFollow = 2
    }

    private static int OpenUnix(string path, UnixOpenFlags flags)
    {
        // O_RDONLY | O_CLOEXEC, plus the per-OS O_DIRECTORY / O_NOFOLLOW bits.
        var value = OperatingSystem.IsMacOS() ? 0x0100_0000 : 0x0008_0000;
        if ((flags & UnixOpenFlags.Directory) != 0)
            value |= OperatingSystem.IsMacOS() ? 0x0010_0000 : 0x0001_0000;
        if ((flags & UnixOpenFlags.NoFollow) != 0)
            value |= OperatingSystem.IsMacOS() ? 0x0000_0100 : 0x0002_0000;
        return Open(path, value);
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(int descriptor, out StatMacOS stat);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int StatX(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out StatLinux stat);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string name,
        int desiredAccess,
        int shareMode,
        IntPtr securityAttributes,
        int creationDisposition,
        int flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        out ByHandleFileInformation information);
}
