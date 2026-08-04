using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Maieutics.Execution;

internal sealed class Workspace
{
    private readonly Lock gate = new();
    private readonly string startupRootPath;
    private WorkspaceSnapshot current;

    private Workspace(string rootPath)
    {
        startupRootPath = rootPath;
        current = new WorkspaceSnapshot(rootPath, Version: 0, HasSessionOverride: false);
    }

    internal static Workspace Create(string? configuredPath, string startupCurrentDirectory)
    {
        var rootPath = ValidateRoot(configuredPath, startupCurrentDirectory);
        return new Workspace(rootPath);
    }

    internal WorkspaceSnapshot Capture()
    {
        lock (gate)
        {
            return current;
        }
    }

    internal WorkspaceSnapshot Use(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        while (true)
        {
            var previous = Capture();
            var replacementPath = ValidateRoot(path, previous.RootPath);
            lock (gate)
            {
                if (current.Version != previous.Version)
                {
                    continue;
                }

                current = new WorkspaceSnapshot(
                    replacementPath,
                    checked(previous.Version + 1),
                    HasSessionOverride: true);
                return current;
            }
        }
    }

    internal WorkspaceSnapshot Reset()
    {
        lock (gate)
        {
            if (!current.HasSessionOverride)
            {
                return current;
            }

            current = new WorkspaceSnapshot(
                startupRootPath,
                checked(current.Version + 1),
                HasSessionOverride: false);
            return current;
        }
    }

    private static string ValidateRoot(string? configuredPath, string startupCurrentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startupCurrentDirectory);
        if (configuredPath is not null && string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException(
                "The configured Maieutics workspace root cannot be empty.",
                nameof(configuredPath));
        }

        var candidate = configuredPath ?? startupCurrentDirectory;
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate, startupCurrentDirectory));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The configured Maieutics workspace root does not exist.");
        }

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                "The configured Maieutics workspace root cannot be a symbolic link.",
                nameof(configuredPath));
        }

        return fullPath;
    }
}

internal sealed record WorkspaceSnapshot(
    string RootPath,
    long Version,
    bool HasSessionOverride)
{
    private const string UriPrefix = "workspace://local";

    internal WorkspacePath Resolve(string? uri, bool allowRoot = true)
    {
        var segments = ParseSegments(uri, allowRoot);
        var current = RootPath;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException exception)
            {
                throw new WorkspaceException(
                    "workspace_path_not_found",
                    "The workspace URI does not identify an existing path.",
                    exception);
            }
            catch (DirectoryNotFoundException exception)
            {
                throw new WorkspaceException(
                    "workspace_path_not_found",
                    "The workspace URI does not identify an existing path.",
                    exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new WorkspaceException(
                    "workspace_symbolic_link_not_allowed",
                    "Workspace tools cannot read or traverse symbolic links.");
            }
        }

        return new WorkspacePath(current, ToWorkspaceUri(current), File.GetAttributes(current));
    }

    internal string ToWorkspaceUri(string fullPath)
    {
        var relative = Path.GetRelativePath(RootPath, fullPath);
        if (relative == ".")
        {
            return $"{UriPrefix}/";
        }

        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A workspace path escaped its configured root.");
        }

        var builder = new StringBuilder(UriPrefix);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append('/').Append(Uri.EscapeDataString(segment));
        }

        return builder.ToString();
    }

    internal async ValueTask<BoundedFileContent> ReadAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        EnsureRegular(File.GetAttributes(path));
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
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
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

    internal FileStream OpenVerifiedRead(string path)
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

    private static IReadOnlyList<string> ParseSegments(string? uri, bool allowRoot)
    {
        if (uri is null)
        {
            if (allowRoot)
            {
                return [];
            }

            throw InvalidUri("A workspace URI is required.");
        }

        if (uri.Length == 0 || uri.IndexOf('\0') >= 0 ||
            uri.IndexOfAny(['?', '#']) >= 0 ||
            !uri.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            throw InvalidUri("The value must be a workspace://local URI.");
        }

        var suffix = uri[UriPrefix.Length..];
        if (suffix.Length == 0 || suffix == "/")
        {
            if (allowRoot)
            {
                return [];
            }

            throw InvalidUri("The workspace root is not valid for this operation.");
        }

        if (suffix[0] != '/')
        {
            throw InvalidUri("The value must be a workspace://local URI.");
        }

        var rawSegments = suffix[1..].Split('/');
        var segments = new List<string>(rawSegments.Length);
        for (var index = 0; index < rawSegments.Length; index++)
        {
            var rawSegment = rawSegments[index];
            if (rawSegment.Length == 0)
            {
                if (index == rawSegments.Length - 1)
                {
                    continue;
                }

                throw InvalidUri("Workspace URIs cannot contain empty path segments.");
            }

            ValidatePercentEscaping(rawSegment);
            string segment;
            try
            {
                segment = Uri.UnescapeDataString(rawSegment);
            }
            catch (UriFormatException exception)
            {
                throw InvalidUri("The workspace URI contains invalid escaping.", exception);
            }

            if (segment.Length == 0 || segment is "." or ".." ||
                segment.IndexOf('\0') >= 0 ||
                segment.IndexOf('/') >= 0 ||
                segment.IndexOf('\\') >= 0)
            {
                throw InvalidUri("The workspace URI contains an invalid path segment.");
            }

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspaceException(
                    "workspace_path_denied",
                    "Workspace tools cannot access .git metadata.");
            }

            segments.Add(segment);
        }

        if (segments.Count == 0 && !allowRoot)
        {
            throw InvalidUri("The workspace root is not valid for this operation.");
        }

        return segments;
    }

    private static void ValidatePercentEscaping(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length || !IsHexDigit(value[index + 1]) || !IsHexDigit(value[index + 2]))
            {
                throw InvalidUri("The workspace URI contains invalid escaping.");
            }

            index += 2;
        }
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static WorkspaceException InvalidUri(string message, Exception? innerException = null) =>
        new("workspace_invalid_uri", message, innerException);

    private static void EnsureRegular(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw NotRegular();
        }
    }

    private static WorkspaceException NotRegular() => new(
        "workspace_not_regular_file",
        "Workspace text tools can read only regular files.");

    private FileStream OpenRead(string path)
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

        var segments = GetRelativeSegments(path);
        using var rootHandle = OpenUnixHandle(
            RootPath,
            UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
        SafeFileHandle? childDirectory = null;
        try
        {
            var directoryHandle = rootHandle;
            for (var index = 0; index < segments.Count - 1; index++)
            {
                var nextDirectory = OpenUnixHandleAt(
                    directoryHandle,
                    segments[index],
                    UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
                childDirectory?.Dispose();
                childDirectory = nextDirectory;
                directoryHandle = nextDirectory;
            }

            var fileHandle = OpenUnixHandleAt(
                directoryHandle,
                segments[^1],
                UnixOpenFlags.NonBlocking | UnixOpenFlags.NoFollow);
            try
            {
                return new FileStream(fileHandle, FileAccess.Read, bufferSize: 4_096, isAsync: false);
            }
            catch
            {
                fileHandle.Dispose();
                throw;
            }
        }
        finally
        {
            childDirectory?.Dispose();
        }
    }

    private IReadOnlyList<string> GetRelativeSegments(string path)
    {
        var relative = Path.GetRelativePath(RootPath, path);
        if (relative == "." ||
            Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A workspace path escaped its captured root.");
        }

        return relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static SafeFileHandle OpenUnixHandle(string path, UnixOpenFlags additionalFlags)
    {
        var descriptor = Open(path, GetUnixOpenFlags(additionalFlags));
        return CreateUnixHandle(descriptor);
    }

    private static SafeFileHandle OpenUnixHandleAt(
        SafeFileHandle directoryHandle,
        string name,
        UnixOpenFlags additionalFlags)
    {
        var descriptor = OpenAt(
            directoryHandle.DangerousGetHandle().ToInt32(),
            name,
            GetUnixOpenFlags(additionalFlags));
        return CreateUnixHandle(descriptor);
    }

    private static SafeFileHandle CreateUnixHandle(int descriptor)
    {
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == (OperatingSystem.IsMacOS() ? 62 : 40))
            {
                throw new WorkspaceException(
                    "workspace_symbolic_link_not_allowed",
                    "Workspace tools cannot read or traverse symbolic links.",
                    new Win32Exception(error));
            }

            throw new IOException(
                "The workspace file could not be opened.",
                new Win32Exception(error));
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private static int GetUnixOpenFlags(UnixOpenFlags flags)
    {
        var value = OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
        if ((flags & UnixOpenFlags.NonBlocking) != 0)
        {
            value |= OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;
        }

        if ((flags & UnixOpenFlags.NoFollow) != 0)
        {
            value |= OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;
        }

        if ((flags & UnixOpenFlags.Directory) != 0)
        {
            value |= OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;
        }

        return value;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [Flags]
    private enum UnixOpenFlags
    {
        None = 0,
        NonBlocking = 1,
        NoFollow = 2,
        Directory = 4
    }
}

internal sealed record WorkspacePath(string FullPath, string Uri, FileAttributes Attributes)
{
    internal bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    internal bool IsRegularFile =>
        (Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
}

internal sealed class WorkspaceException : Exception
{
    internal WorkspaceException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    internal string Code { get; }
}

internal readonly record struct BoundedFileContent(byte[] Bytes, bool ExceededLimit);