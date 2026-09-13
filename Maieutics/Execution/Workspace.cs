using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Maieutics.Execution;

/// <summary>The authoritative workspace: a fixed, product-owned home directory
/// (ADR 0027). The root never changes mid-process; external projects are opened as managed
/// links under <c>projects/</c>, which bumps the snapshot version. Exposes the permission
/// variable seam: <c>workspace</c> is the home root, <c>project.&lt;name&gt;</c> is a
/// registered link's canonical target.</summary>
internal sealed class Workspace : IPermissionVariableSource
{
    private const string WorkspaceVariableName = "workspace";
    private const string ProjectVariablePrefix = "project.";
    private readonly Lock gate = new();
    private readonly WorkspaceHome? home;
    private readonly string rootPath;
    private WorkspaceSnapshot current;

    private Workspace(WorkspaceHome? home, string rootPath)
    {
        this.home = home;
        this.rootPath = rootPath;
        current = new WorkspaceSnapshot(rootPath, 0, home?.LinksView());
    }

    internal string RootPath => rootPath;

    string? IPermissionVariableSource.GetVariable(string name)
    {
        if (string.Equals(name, WorkspaceVariableName, StringComparison.Ordinal)) return rootPath;

        if (name.StartsWith(ProjectVariablePrefix, StringComparison.Ordinal))
        {
            var projectName = name[ProjectVariablePrefix.Length..];
            if (projectName.Length > 0 &&
                Capture().Links?.TargetsByName.TryGetValue(projectName, out var target) == true)
                return target;
        }

        return null;
    }

    /// <summary>Host composition: the workspace root is the managed home.</summary>
    internal static Workspace Create(WorkspaceHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return new Workspace(home, home.HomePath);
    }

    /// <summary>Root-only variant without a home: no links can be opened and no link
    /// traversal is registered. The configured path resolves against
    /// <paramref name="startupCurrentDirectory"/>; null selects that directory itself.</summary>
    internal static Workspace Create(string? configuredPath, string startupCurrentDirectory)
    {
        var rootPath = ValidateRoot(configuredPath, startupCurrentDirectory);
        return new Workspace(null, rootPath);
    }

    internal WorkspaceSnapshot Capture()
    {
        lock (gate)
        {
            return current;
        }
    }

    internal WorkspaceSnapshot OpenLink(string path, string? alias)
    {
        var owner = home
                    ?? throw new InvalidOperationException(
                        "Workspace links require the fixed workspace home.");
        var record = owner.Open(path, alias);
        return CommitLinksChanged(record);
    }

    internal WorkspaceSnapshot CloseLink(string name)
    {
        var owner = home
                    ?? throw new InvalidOperationException(
                        "Workspace links require the fixed workspace home.");
        var record = owner.Close(name);
        return CommitLinksChanged(record);
    }

    private WorkspaceSnapshot CommitLinksChanged(WorkspaceLinkRecord record)
    {
        lock (gate)
        {
            current = new WorkspaceSnapshot(
                rootPath,
                checked(current.Version + 1),
                home?.LinksView());
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
        var fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate, startupCurrentDirectory));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("The configured Maieutics workspace root does not exist.");

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException(
                "The configured Maieutics workspace root cannot be a symbolic link.",
                nameof(configuredPath));

        return fullPath;
    }
}

/// <summary>An immutable view of the workspace root and its registered links. URIs are
/// logical (root-relative through <c>projects/&lt;name&gt;</c>); paths past a managed hop
/// are physical locations outside the root, so URIs are always built from logical
/// segments, never from physical paths. Partial: hosts the Unix <c>openat</c> P/Invokes.</summary>
internal sealed partial record WorkspaceSnapshot(
    string RootPath,
    long Version,
    WorkspaceLinks? Links)
{
    private const string UriPrefix = "workspace://local";

    internal WorkspacePath Resolve(string? uri, bool allowRoot = true)
    {
        var segments = ParseSegments(uri, allowRoot);
        var current = RootPath;
        FileAttributes attributes;
        if (segments.Count == 0)
        {
            attributes = File.GetAttributes(current);
        }
        else
        {
            attributes = 0;
            for (var index = 0; index < segments.Count; index++)
            {
                var candidate = Path.Combine(current, segments[index]);
                FileAttributes candidateAttributes;
                try
                {
                    candidateAttributes = File.GetAttributes(candidate);
                }
                catch (FileNotFoundException exception)
                {
                    throw NotFound(exception);
                }
                catch (DirectoryNotFoundException exception)
                {
                    throw NotFound(exception);
                }

                if ((candidateAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = ResolveRegisteredLink(segments, index, candidate);
                    if (target is null)
                        throw new WorkspaceException(
                            "workspace_symbolic_link_not_allowed",
                            "Workspace tools cannot read or traverse symbolic links.");

                    current = target;
                    candidateAttributes = File.GetAttributes(current);
                }
                else
                {
                    current = candidate;
                }

                attributes = candidateAttributes;
            }
        }

        return new WorkspacePath(current, ToWorkspaceUri(segments), attributes, segments);
    }

    /// <summary>The only permitted traversal of a reparse point: the segment directly
    /// under <c>projects/</c>, when the registry binds that name to exactly the link's
    /// final target. Everything else is rejected by the caller as a foreign link.</summary>
    internal string? ResolveRegisteredLink(
        IReadOnlyList<string> segments,
        int index,
        string linkPath)
    {
        if (index != 1 || Links is null || segments.Count <= index ||
            !segments[0].Equals(WorkspaceHome.ProjectsDirectoryName, StringComparison.Ordinal))
            return null;

        if (!Links.TargetsByName.TryGetValue(segments[index], out var target)) return null;

        var final = new DirectoryInfo(linkPath).ResolveLinkTarget(returnFinalTarget: true);
        if (final is null) return null;

        var finalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(final.FullName));
        return string.Equals(
            finalPath,
            target,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
            ? target
            : null;
    }

    internal string ToWorkspaceUri(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return $"{UriPrefix}/";

        var builder = new StringBuilder(UriPrefix);
        foreach (var segment in segments)
            builder.Append('/').Append(Uri.EscapeDataString(segment));

        return builder.ToString();
    }

    internal async ValueTask<BoundedFileContent> ReadAsync(
        string path,
        IReadOnlyList<string> segments,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        EnsureRegular(File.GetAttributes(path));
        await using var stream = OpenVerifiedRead(path, segments);
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

    internal FileStream OpenVerifiedRead(WorkspacePath path)
    {
        EnsureRegular(path.Attributes);
        var stream = OpenRead(path.FullPath, path.Segments);
        try
        {
            if (!stream.CanSeek) throw NotRegular();

            EnsureRegular(File.GetAttributes(path.FullPath));
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private FileStream OpenVerifiedRead(string path, IReadOnlyList<string> segments)
    {
        var stream = OpenRead(path, segments);
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

    private static WorkspaceException NotFound(FileNotFoundException exception)
    {
        return new WorkspaceException(
            "workspace_path_not_found",
            "The workspace URI does not identify an existing path.",
            exception);
    }

    private static WorkspaceException NotFound(DirectoryNotFoundException exception)
    {
        return new WorkspaceException(
            "workspace_path_not_found",
            "The workspace URI does not identify an existing path.",
            exception);
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

            if (IsDeniedSegmentName(segment))
                throw new WorkspaceException(
                    "workspace_path_denied",
                    "Workspace tools cannot access restricted metadata directories.");

            segments.Add(segment);
        }

        if (segments.Count == 0 && !allowRoot) throw InvalidUri("The workspace root is not valid for this operation.");

        return segments;
    }

    private static bool IsDeniedSegmentName(string segment)
    {
        return segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals(WorkspaceHome.StateDirectoryName, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Opens a regular file for reading. The Unix walk pins every directory
    /// component with <c>openat</c> + <c>O_NOFOLLOW</c>; the one permitted link traversal
    /// is the registered <c>projects/&lt;name&gt;</c> hop, whose canonical target is opened
    /// directly so the no-follow discipline continues inside the target.</summary>
    private FileStream OpenRead(string path, IReadOnlyList<string> segments)
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

        var segmentsLocal = segments;
        using var rootHandle = OpenUnixHandle(
            RootPath,
            UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
        SafeFileHandle? childDirectory = null;
        try
        {
            var directoryHandle = rootHandle;
            for (var index = 0; index < segmentsLocal.Count - 1; index++)
            {
                SafeFileHandle nextDirectory;
                try
                {
                    nextDirectory = OpenUnixHandleAt(
                        directoryHandle,
                        segmentsLocal[index],
                        UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
                }
                catch (Exception potentialLink) when (
                    potentialLink is WorkspaceException { Code: "workspace_symbolic_link_not_allowed" } or IOException)
                {
                    // A symlinked directory component surfaces as ELOOP on some kernels and
                    // as ENOTDIR under O_NOFOLLOW|O_DIRECTORY on others; a validated walk can
                    // only reach that state at the registered projects/<name> hop.
                    nextDirectory = OpenHopOrRethrow(potentialLink, index, segmentsLocal);
                }

                childDirectory?.Dispose();
                childDirectory = nextDirectory;
                directoryHandle = nextDirectory;
            }

            var fileHandle = OpenUnixHandleAt(
                directoryHandle,
                segmentsLocal[^1],
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

    private string LinkPathForHop(int index, IReadOnlyList<string> segments)
    {
        var builder = new StringBuilder(RootPath);
        for (var position = 0; position <= index; position++)
            builder.Append(Path.DirectorySeparatorChar).Append(segments[position]);

        return builder.ToString();
    }

    private SafeFileHandle OpenHopOrRethrow(
        Exception original,
        int index,
        IReadOnlyList<string> segments)
    {
        if (ResolveRegisteredLink(segments, index, LinkPathForHop(index, segments)) is { } hopTarget)
            return OpenUnixHandle(hopTarget, UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);

        ExceptionDispatchInfo.Capture(original).Throw();
        throw new UnreachableException("ExceptionDispatchInfo.Throw never returns.");
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

        return value;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAt(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int flags);

    [Flags]
    private enum UnixOpenFlags
    {
        NonBlocking = 1,
        NoFollow = 2,
        Directory = 4
    }
}

internal sealed record WorkspacePath(
    string FullPath,
    string Uri,
    FileAttributes Attributes,
    IReadOnlyList<string> Segments)
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
