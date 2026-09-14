using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Maieutics.Execution;

internal sealed class Workspace : IPermissionVariableSource
{
    private readonly Lock gate = new();
    private readonly string startupRootPath;
    private WorkspaceSnapshot current;

    private Workspace(string rootPath)
    {
        startupRootPath = rootPath;
        current = new WorkspaceSnapshot(rootPath, 0, false);
    }

    internal string RootPath => Capture().RootPath;

    string? IPermissionVariableSource.GetVariable(string name)
    {
        return string.Equals(name, "workspace", StringComparison.Ordinal) ? RootPath : null;
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
                if (current.Version != previous.Version) continue;

                current = new WorkspaceSnapshot(
                    replacementPath,
                    checked(previous.Version + 1),
                    true);
                return current;
            }
        }
    }

    internal WorkspaceSnapshot Reset()
    {
        lock (gate)
        {
            if (!current.HasSessionOverride) return current;

            current = new WorkspaceSnapshot(
                startupRootPath,
                checked(current.Version + 1),
                false);
            return current;
        }
    }

    private static string ValidateRoot(string? configuredPath, string startupCurrentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startupCurrentDirectory);
        if (configuredPath is not null && string.IsNullOrWhiteSpace(configuredPath))
            throw new ArgumentException(
                "The configured Maieutics workspace root cannot be empty.",
                nameof(configuredPath));

        var candidate = configuredPath ?? startupCurrentDirectory;
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate, startupCurrentDirectory));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("The configured Maieutics workspace root does not exist.");

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException(
                "The configured Maieutics workspace root cannot be a symbolic link.",
                nameof(configuredPath));

        return fullPath;
    }
}

internal sealed partial record WorkspaceSnapshot(
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
                throw new WorkspaceException(
                    "workspace_symbolic_link_not_allowed",
                    "Workspace tools cannot read or traverse symbolic links.");
        }

        return new WorkspacePath(current, ToWorkspaceUri(current), File.GetAttributes(current));
    }

    internal string ToWorkspaceUri(string fullPath)
    {
        var relative = Path.GetRelativePath(RootPath, fullPath);
        if (relative == ".") return $"{UriPrefix}/";

        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("A workspace path escaped its configured root.");

        var builder = new StringBuilder(UriPrefix);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
            builder.Append('/').Append(Uri.EscapeDataString(segment));

        return builder.ToString();
    }

    /// <summary>The workspace-relative, forward-slash display path used in
    /// diff headers: decoded, unlike the percent-escaped wire URI.</summary>
    internal string ToDisplayPath(string fullPath)
    {
        var relative = Path.GetRelativePath(RootPath, fullPath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Resolves a write target for the edit tools. Unlike
    /// <see cref="Resolve(string,bool)"/> the final segment may be missing
    /// (creation); existing segments still refuse reparse points and the same
    /// URI rules apply, so the caller can classify create versus update while
    /// every opened component re-validates at open time.</summary>
    internal WorkspaceWriteTarget ResolveWriteTarget(string uri)
    {
        var segments = ParseSegments(uri, allowRoot: false);
        var current = RootPath;
        for (var index = 0; index < segments.Count; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                return MissingWriteTarget(current, segments, index);
            }
            catch (DirectoryNotFoundException)
            {
                return MissingWriteTarget(current, segments, index);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceException(
                    "workspace_symbolic_link_not_allowed",
                    "Workspace tools cannot write through symbolic links.");

            if ((attributes & FileAttributes.Directory) == 0)
            {
                if (index == segments.Count - 1)
                    return new WorkspaceWriteTarget(
                        current,
                        ToWorkspaceUri(current),
                        ToDisplayPath(current),
                        true);

                throw new WorkspaceException(
                    "workspace_not_directory",
                    "The workspace URI's parent path is not a directory.");
            }
        }

        throw new WorkspaceException(
            "workspace_not_regular_file",
            "Workspace edit tools can write only regular files.");
    }

    private WorkspaceWriteTarget MissingWriteTarget(
        string existingPrefix,
        IReadOnlyList<string> segments,
        int missingIndex)
    {
        var fullPath = existingPrefix;
        for (var index = missingIndex + 1; index < segments.Count; index++)
            fullPath = Path.Combine(fullPath, segments[index]);

        return new WorkspaceWriteTarget(
            fullPath,
            ToWorkspaceUri(fullPath),
            ToDisplayPath(fullPath),
            false);
    }

    internal void EnsureParentDirectories(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null || !parent.StartsWith(RootPath, StringComparison.Ordinal))
            throw new InvalidOperationException("A workspace write target escaped its captured root.");

        Directory.CreateDirectory(parent);
    }

    /// <summary>Opens a write handle to a workspace file. Creation uses an
    /// exclusive create so a concurrent appearance fails typed; updates truncate
    /// an existing regular file. Every path component re-validates at open
    /// time: the Unix walk uses openat with O_NOFOLLOW, and Windows re-checks
    /// the final attributes after opening.</summary>
    internal FileStream OpenWrite(string path, bool create)
    {
        if (OperatingSystem.IsWindows())
        {
            var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = create ? FileMode.CreateNew : FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 4_096
            });
            try
            {
                EnsureWritableRegular(File.GetAttributes(path));
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
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

            var flags = UnixOpenFlags.WriteOnly | UnixOpenFlags.Trunc | UnixOpenFlags.NoFollow;
            if (create) flags |= UnixOpenFlags.Creat | UnixOpenFlags.Excl;

            var descriptor = OpenAt(
                directoryHandle.DangerousGetHandle().ToInt32(),
                segments[^1],
                GetUnixOpenFlags(flags),
                // 0x198 = 0o666; narrowed below to the conventional 0644 (see
                // SetUnixFileMode) so the created file is owner-writable and
                // world-readable regardless of how the host applies the mask.
                mode: create ? 0x198 : 0);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 17)
                    throw new WorkspaceException(
                        "workspace_path_exists",
                        "The workspace path already exists.");

                if (error == (OperatingSystem.IsMacOS() ? 62 : 40))
                    throw new WorkspaceException(
                        "workspace_symbolic_link_not_allowed",
                        "Workspace tools cannot write through symbolic links.");

                throw new IOException(
                    "The workspace file could not be opened.",
                    new Win32Exception(error));
            }

            var handle = new SafeFileHandle(descriptor, true);
            if (create)
                // Normalize the fresh file's mode through the open descriptor:
                // the kernel applies the umask at create time, and an explicit
                // chmod makes the resulting permissions deterministic.
                File.SetUnixFileMode(
                    handle,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            return new FileStream(handle, FileAccess.Write, 4_096, false);
        }
        finally
        {
            childDirectory?.Dispose();
        }
    }

    private static void EnsureWritableRegular(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new WorkspaceException(
                "workspace_not_regular_file",
                "Workspace edit tools can write only regular files.");
    }

    /// <summary>Deletes one regular file. The Unix path walks openat with
    /// O_NOFOLLOW and unlinks through the parent descriptor; Windows checks
    /// the final attributes before deleting. Directories are not deletable
    /// through this surface.</summary>
    internal void DeleteFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureWritableRegular(File.GetAttributes(path));
            File.Delete(path);
            return;
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

            if (UnlinkAt(
                    directoryHandle.DangerousGetHandle().ToInt32(),
                    segments[^1],
                    flags: 0) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 21)
                    throw new WorkspaceException(
                        "workspace_not_regular_file",
                        "Workspace edit tools can delete only regular files.");

                if (error == 2)
                    throw new WorkspaceException(
                        "workspace_path_not_found",
                        "The workspace URI does not identify an existing path.");

                if (error == (OperatingSystem.IsMacOS() ? 62 : 40))
                    throw new WorkspaceException(
                        "workspace_symbolic_link_not_allowed",
                        "Workspace tools cannot delete symbolic links.");

                throw new IOException(
                    "The workspace file could not be deleted.",
                    new Win32Exception(error));
            }
        }
        finally
        {
            childDirectory?.Dispose();
        }
    }

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static partial int UnlinkAt(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    internal async ValueTask<BoundedFileContent> ReadAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        EnsureRegular(File.GetAttributes(path));
        await using var stream = OpenVerifiedRead(path);
        if (stream.Length > maximumBytes) return new BoundedFileContent([], true);

        var buffer = new byte[maximumBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;

            read += count;
        }

        if (read == maximumBytes && stream.Length != read) return new BoundedFileContent([], true);

        Array.Resize(ref buffer, read);
        return new BoundedFileContent(buffer, false);
    }

    internal FileStream OpenVerifiedRead(string path)
    {
        var stream = OpenRead(path);
        try
        {
            if (!stream.CanSeek) throw NotRegular();

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
            if (allowRoot) return [];

            throw InvalidUri("A workspace URI is required.");
        }

        if (uri.Length == 0 || uri.IndexOf('\0') >= 0 ||
            uri.IndexOfAny(['?', '#']) >= 0 ||
            !uri.StartsWith(UriPrefix, StringComparison.Ordinal))
            throw InvalidUri("The value must be a workspace://local URI.");

        var suffix = uri[UriPrefix.Length..];
        if (suffix.Length == 0 || suffix == "/")
        {
            if (allowRoot) return [];

            throw InvalidUri("The workspace root is not valid for this operation.");
        }

        if (suffix[0] != '/') throw InvalidUri("The value must be a workspace://local URI.");

        var rawSegments = suffix[1..].Split('/');
        var segments = new List<string>(rawSegments.Length);
        for (var index = 0; index < rawSegments.Length; index++)
        {
            var rawSegment = rawSegments[index];
            if (rawSegment.Length == 0)
            {
                if (index == rawSegments.Length - 1) continue;

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
                throw InvalidUri("The workspace URI contains an invalid path segment.");

            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceException(
                    "workspace_path_denied",
                    "Workspace tools cannot access .git metadata.");

            segments.Add(segment);
        }

        if (segments.Count == 0 && !allowRoot) throw InvalidUri("The workspace root is not valid for this operation.");

        return segments;
    }

    private static void ValidatePercentEscaping(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;

            if (index + 2 >= value.Length || !IsHexDigit(value[index + 1]) || !IsHexDigit(value[index + 2]))
                throw InvalidUri("The workspace URI contains invalid escaping.");

            index += 2;
        }
    }

    private static bool IsHexDigit(char value)
    {
        return value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
    }

    private static WorkspaceException InvalidUri(string message, Exception? innerException = null)
    {
        return new WorkspaceException("workspace_invalid_uri", message, innerException);
    }

    private static void EnsureRegular(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw NotRegular();
    }

    private static WorkspaceException NotRegular()
    {
        return new WorkspaceException(
            "workspace_not_regular_file",
            "Workspace text tools can read only regular files.");
    }

    private FileStream OpenRead(string path)
    {
        if (OperatingSystem.IsWindows())
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
                return new FileStream(fileHandle, FileAccess.Read, 4_096, false);
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
            throw new InvalidOperationException("A workspace path escaped its captured root.");

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
            GetUnixOpenFlags(additionalFlags),
            mode: 0);
        return CreateUnixHandle(descriptor);
    }

    private static SafeFileHandle CreateUnixHandle(int descriptor)
    {
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == (OperatingSystem.IsMacOS() ? 62 : 40))
                throw new WorkspaceException(
                    "workspace_symbolic_link_not_allowed",
                    "Workspace tools cannot read or traverse symbolic links.",
                    new Win32Exception(error));

            throw new IOException(
                "The workspace file could not be opened.",
                new Win32Exception(error));
        }

        return new SafeFileHandle(descriptor, true);
    }

    private static int GetUnixOpenFlags(UnixOpenFlags flags)
    {
        var value = OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
        if ((flags & UnixOpenFlags.NonBlocking) != 0) value |= OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;

        if ((flags & UnixOpenFlags.NoFollow) != 0) value |= OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;

        if ((flags & UnixOpenFlags.Directory) != 0) value |= OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;

        if ((flags & UnixOpenFlags.WriteOnly) != 0) value |= 0x00000001;

        if ((flags & UnixOpenFlags.Creat) != 0) value |= OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;

        if ((flags & UnixOpenFlags.Excl) != 0) value |= OperatingSystem.IsMacOS() ? 0x00000800 : 0x00000080;

        if ((flags & UnixOpenFlags.Trunc) != 0) value |= OperatingSystem.IsMacOS() ? 0x00000400 : 0x00000200;

        return value;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAt(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        int mode);

    [Flags]
    private enum UnixOpenFlags
    {
        None = 0,
        NonBlocking = 1,
        NoFollow = 2,
        Directory = 4,
        WriteOnly = 8,
        Creat = 16,
        Excl = 32,
        Trunc = 64
    }
}

internal sealed record WorkspacePath(string FullPath, string Uri, FileAttributes Attributes)
{
    internal bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    internal bool IsRegularFile =>
        (Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
}

/// <summary>A write target resolved by <see cref="WorkspaceSnapshot.ResolveWriteTarget"/>:
/// the final path segment may be missing, in which case the edit tools create it
/// (with its parent directories) instead of updating an existing file.</summary>
internal sealed record WorkspaceWriteTarget(string FullPath, string Uri, string DisplayPath, bool Exists);

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