using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Maieutics.Execution;

internal static class WorkspaceFileReader
{
    internal static async ValueTask<BoundedFileContent> ReadAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        var initialAttributes = File.GetAttributes(path);
        EnsureRegular(initialAttributes);
        await using var stream = OpenVerifiedRead(path);
        if (stream.Length > maximumBytes)
        {
            return new BoundedFileContent([], ExceededLimit: true);
        }

        var buffer = new byte[maximumBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read == maximumBytes && stream.Length != read)
        {
            return new BoundedFileContent([], ExceededLimit: true);
        }

        Array.Resize(ref buffer, read);
        return new BoundedFileContent(buffer, ExceededLimit: false);
    }

    private static void EnsureRegular(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw NotRegular();
        }
    }

    private static WorkspaceToolException NotRegular() => new(
        "workspace_not_regular_file",
        "Workspace text tools can read only regular files.");

    internal static FileStream OpenVerifiedRead(string path)
    {
        var stream = OpenRead(path);
        try
        {
            if (!stream.CanSeek)
            {
                throw NotRegular();
            }

            EnsureRegular(File.GetAttributes(path));
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream OpenRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 4_096
                });
        }

        const int readOnly = 0;
        var nonBlocking = OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;
        var closeOnExec = OperatingSystem.IsMacOS() ? 0x01000000 : 0x080000;
        var descriptor = Open(path, readOnly | nonBlocking | closeOnExec);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                "The workspace file could not be opened.",
                new Win32Exception(error));
        }

        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.Read, bufferSize: 4_096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}

internal readonly record struct BoundedFileContent(byte[] Bytes, bool ExceededLimit);