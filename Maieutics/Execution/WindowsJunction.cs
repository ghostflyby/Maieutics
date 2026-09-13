using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Maieutics.Execution;

/// <summary>Creates Windows directory junctions. .NET exposes no BCL junction-creation API,
/// so the reparse point is written directly (ADR 0027 §3): a junction needs no privilege,
/// unlike a symbolic link, which would require elevation or Developer Mode. Only compiled
/// against kernel32; never invoked off Windows.</summary>
internal static partial class WindowsJunction
{
    private const uint FsctlSetReparsePoint = 0x0009_00A4;
    private const uint IoReparseTagMountPoint = 0xA000_0003;
    private const uint GenericWrite = 0x4000_0000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileShareReadWriteDelete = 0x0000_0007;

    /// <summary>Creates <paramref name="linkPath"/> as a directory junction pointing at
    /// <paramref name="targetPath"/>. The target is stored as an absolute non-UNC path,
    /// which is the only form junctions support.</summary>
    internal static void Create(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(linkPath);
        using var handle = CreateFile(
            linkPath,
            GenericWrite,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException(
                $"The junction directory '{linkPath}' could not be opened.",
                new Win32Exception(Marshal.GetLastWin32Error()));

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + targetPath);
        var printName = Encoding.Unicode.GetBytes(targetPath);
        var dataLength = 8 + substitute.Length + printName.Length;
        var buffer = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
        // Name offsets are relative to PathBuffer (buffer offset 16), not to the header.
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)printName.Length);
        substitute.CopyTo(buffer, 16);
        printName.CopyTo(buffer, 16 + substitute.Length);

        if (!DeviceIoControl(
                handle,
                FsctlSetReparsePoint,
                buffer,
                (uint)buffer.Length,
                null,
                0,
                out _,
                IntPtr.Zero))
            throw new IOException(
                $"The junction reparse point for '{linkPath}' could not be written.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string name,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inBuffer,
        uint inBufferSize,
        byte[]? outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
