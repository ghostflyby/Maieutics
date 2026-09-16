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

    /// <summary>The errno a no-follow open reports for a symbolic link: ELOOP on macOS,
    /// ELOOP-via-ENOTDIR on the Linux variants this project targets.</summary>
    private static readonly int SymlinkErrno = OperatingSystem.IsMacOS() ? 62 : 40;

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

    /// <summary>The logical, decoded, forward-slash path used in diff headers: unlike the
    /// percent-escaped wire URI it is the plain path past the workspace root.</summary>
    internal static string ToDisplayPath(IReadOnlyList<string> segments)
    {
        return string.Join('/', segments);
    }

    /// <summary>Resolves a write target. Unlike <see cref="Resolve"/> the final segment may be
    /// missing, in which case the tool creates it; the parent chain is resolved through the same
    /// rules as reads (including the registered <c>projects/&lt;name&gt;</c> hop), and a parent
    /// that does not exist yet is created under the deepest ancestor that does.</summary>
    internal WorkspaceWriteTarget ResolveWriteTarget(string uri)
    {
        var segments = ParseSegments(uri, allowRoot: false);
        var parentCount = segments.Count - 1;

        WorkspacePath parent;
        var existing = parentCount;
        while (true)
        {
            try
            {
                parent = Resolve(
                    existing == 0 ? null : ToWorkspaceUri([.. segments.Take(existing)]));
                break;
            }
            catch (WorkspaceException exception) when (exception.Code == "workspace_path_not_found")
            {
                if (existing == 0) throw;

                existing--;
            }
        }

        if (!parent.IsDirectory)
            throw new WorkspaceException(
                "workspace_not_directory",
                "The workspace URI's parent path is not a directory.");

        // Segments below the resolved ancestor map one to one onto physical children, whether
        // that ancestor is inside the root or the canonical target of a registered link.
        var parentPath = parent.FullPath;
        for (var index = existing; index < parentCount; index++)
            parentPath = Path.Combine(parentPath, segments[index]);

        var fullPath = Path.Combine(parentPath, segments[^1]);
        var exists = true;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            exists = false;
            attributes = 0;
        }
        catch (DirectoryNotFoundException)
        {
            exists = false;
            attributes = 0;
        }

        if (exists && (attributes & FileAttributes.ReparsePoint) != 0)
            throw new WorkspaceException(
                "workspace_symbolic_link_not_allowed",
                "Workspace tools cannot write through symbolic links.");

        if (exists && (attributes & FileAttributes.Directory) != 0)
            throw new WorkspaceException(
                "workspace_not_regular_file",
                "Workspace edit tools can write only regular files.");

        return new WorkspaceWriteTarget(
            fullPath,
            parentPath,
            ToWorkspaceUri(segments),
            ToDisplayPath(segments),
            exists,
            segments);
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
        {
            ValidateForWindows(path, segments);
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
        {
            // The walk opens the registered target rather than the junction, so swapping
            // the junction cannot redirect this open — but a different directory placed at
            // the registered path can. The fingerprint pins the open to the registered
            // project (ADR 0027 follow-up); a capture that cannot run skips the check.
            var handle = OpenUnixHandle(hopTarget, UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
            PinLinkIdentity(
                segments[index],
                WorkspaceFileIdentityReader.TryReadFromDescriptor(
                    handle.DangerousGetHandle().ToInt32()));
            return handle;
        }

        ExceptionDispatchInfo.Capture(original).Throw();
        throw new UnreachableException("ExceptionDispatchInfo.Throw never returns.");
    }

    private const string LinkIdentityChangedCode = "workspace_link_identity_changed";

    /// <summary>The registered record when the segments traverse the managed hop, else
    /// null.</summary>
    private WorkspaceLinkRecord? RegisteredRecordFor(IReadOnlyList<string> segments)
    {
        if (segments.Count < 2 ||
            !segments[0].Equals(WorkspaceHome.ProjectsDirectoryName, StringComparison.Ordinal) ||
            Links is not { } links)
            return null;

        foreach (var record in links.Records)
        {
            if (string.Equals(record.Name, segments[1], StringComparison.OrdinalIgnoreCase))
                return record;
        }

        return null;
    }

    /// <summary>Pins an opened managed hop to the registered project. A fingerprint-less
    /// record (legacy registry, capture failure) or a capture that cannot run skips the
    /// check; a mismatch means the open no longer reaches the project that was registered,
    /// whatever replaced it, and fails typed instead of reading the wrong directory.</summary>
    private void PinLinkIdentity(string name, WorkspaceFileIdentity? actual)
    {
        if (Links is not { } links)
            return;
        WorkspaceLinkRecord? record = null;
        foreach (var candidate in links.Records)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                record = candidate;
                break;
            }
        }

        if (record?.Identity is not { } expected || actual is null) return;
        if (expected.Matches(actual)) return;

        throw new WorkspaceException(
            LinkIdentityChangedCode,
            $"The project link '{name}' no longer opens the project directory it was " +
            "registered with. Remount the workspace or close and reopen the link.");
    }

    /// <summary>Windows open-time discipline (ADR 0027 follow-up). When the URI traverses
    /// the managed hop, the registered target directory is opened and pinned by
    /// fingerprint; then every physical component strictly below the traversal base is
    /// re-checked for a reparse point, fresh at operation time. The final open still
    /// re-walks the whole path — a handle-relative walk is the remaining Tier B — so this
    /// narrows the swap windows; it does not close the last one.</summary>
    private void ValidateForWindows(string path, IReadOnlyList<string> segments)
    {
        var record = RegisteredRecordFor(segments);
        if (record is not null &&
            Links is { } links &&
            links.TargetsByName.TryGetValue(record.Name, out var target))
        {
            using var handle = WorkspaceFileIdentityReader.OpenDirectoryHandle(
                target,
                followReparse: true);
            PinLinkIdentity(record.Name, WorkspaceFileIdentityReader.TryRead(handle));
        }

        ValidateComponentsForWindows(path, record?.Target ?? RootPath);
    }

    private void ValidateComponentsForWindows(string physicalPath, string baseRoot)
    {
        if (!physicalPath.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase)) return;

        var separator = Path.DirectorySeparatorChar;
        var start = baseRoot.Length;
        if (start < physicalPath.Length && (physicalPath[start] == separator ||
            physicalPath[start] == Path.AltDirectorySeparatorChar))
            start++;

        for (var index = start; index < physicalPath.Length;)
        {
            var next = physicalPath.IndexOf(separator, index);
            var prefix = next < 0 ? physicalPath : physicalPath[..next];
            using var handle = WorkspaceFileIdentityReader.OpenDirectoryHandle(
                prefix,
                followReparse: false);
            if (handle.IsInvalid) return; // the operation itself fails typed on this path

            if (WorkspaceFileIdentityReader.HasReparseTag(handle))
                throw new WorkspaceException(
                    "workspace_symbolic_link_not_allowed",
                    "Workspace tools cannot read or traverse symbolic links.");

            if (next < 0) break;
            index = next + 1;
        }
    }

    /// <summary>Opens a write handle to a workspace file. Creation uses an exclusive create so a
    /// concurrent appearance fails typed; updates truncate the existing regular file. The Unix
    /// walk mirrors <see cref="OpenRead"/>: every component is pinned with <c>O_NOFOLLOW</c> and
    /// the one permitted link traversal is the registered hop.</summary>
    private FileStream OpenWrite(string path, IReadOnlyList<string> segments, bool create)
    {
        if (OperatingSystem.IsWindows())
        {
            ValidateForWindows(path, segments);
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
                EnsureRegular(File.GetAttributes(path));
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        using var rootHandle = OpenUnixHandle(
            RootPath,
            UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
        SafeFileHandle? childDirectory = null;
        try
        {
            var directoryHandle = rootHandle;
            for (var index = 0; index < segments.Count - 1; index++)
            {
                SafeFileHandle nextDirectory;
                try
                {
                    nextDirectory = OpenUnixHandleAt(
                        directoryHandle,
                        segments[index],
                        UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
                }
                catch (Exception potentialLink) when (
                    potentialLink is WorkspaceException { Code: "workspace_symbolic_link_not_allowed" } or IOException)
                {
                    nextDirectory = OpenHopOrRethrow(potentialLink, index, segments);
                }

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
                // 0o666; the process mask narrows it, and the mode is normalized through the
                // descriptor below so the result is deterministic.
                mode: create ? 0x1B6 : 0);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 17)
                    throw new WorkspaceException(
                        "workspace_path_exists",
                        "The workspace path already exists.");

                if (error == SymlinkErrno)
                    throw new WorkspaceException(
                        "workspace_symbolic_link_not_allowed",
                        "Workspace tools cannot write through symbolic links.");

                throw new IOException(
                    "The workspace file could not be opened.",
                    new Win32Exception(error));
            }

            var handle = new SafeFileHandle(descriptor, true);
            if (create)
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

    /// <summary>Deletes one regular file, with the same component pinning as a read. Directories
    /// are not deletable through this surface.</summary>
    private void DeleteFile(string path, IReadOnlyList<string> segments)
    {
        if (OperatingSystem.IsWindows())
        {
            ValidateForWindows(path, segments);
            EnsureRegular(File.GetAttributes(path));
            File.Delete(path);
            return;
        }

        using var rootHandle = OpenUnixHandle(
            RootPath,
            UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
        SafeFileHandle? childDirectory = null;
        try
        {
            var directoryHandle = rootHandle;
            for (var index = 0; index < segments.Count - 1; index++)
            {
                SafeFileHandle nextDirectory;
                try
                {
                    nextDirectory = OpenUnixHandleAt(
                        directoryHandle,
                        segments[index],
                        UnixOpenFlags.Directory | UnixOpenFlags.NoFollow);
                }
                catch (Exception potentialLink) when (
                    potentialLink is WorkspaceException { Code: "workspace_symbolic_link_not_allowed" } or IOException)
                {
                    nextDirectory = OpenHopOrRethrow(potentialLink, index, segments);
                }

                childDirectory?.Dispose();
                childDirectory = nextDirectory;
                directoryHandle = nextDirectory;
            }

            if (UnlinkAt(directoryHandle.DangerousGetHandle().ToInt32(), segments[^1], flags: 0) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 2)
                    throw new WorkspaceException(
                        "workspace_path_not_found",
                        "The workspace URI does not identify an existing path.");

                if (error == 21)
                    throw new WorkspaceException(
                        "workspace_not_regular_file",
                        "Workspace edit tools can delete only regular files.");

                if (error == SymlinkErrno)
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

    /// <summary>Opens a workspace file for a write or delete, resolving the URI anew so the write
    /// path revalidates every component exactly as a read does.</summary>
    internal FileStream OpenWrite(WorkspaceWriteTarget target, bool create)
    {
        return OpenWrite(target.FullPath, target.Segments, create);
    }

    /// <summary>Creates the target's parent chain, which <see cref="ResolveWriteTarget"/> already
    /// validated past the resolved ancestor.</summary>
    internal static void EnsureParentDirectories(WorkspaceWriteTarget target)
    {
        if (!Directory.Exists(target.ParentPath)) Directory.CreateDirectory(target.ParentPath);
    }

    /// <summary>Deletes a resolved write target through the pinned walk.</summary>
    internal void DeleteFile(WorkspaceWriteTarget target)
    {
        DeleteFile(target.FullPath, target.Segments);
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
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int flags,
        int mode);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static partial int UnlinkAt(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int flags);

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

internal sealed record WorkspacePath(
    string FullPath,
    string Uri,
    FileAttributes Attributes,
    IReadOnlyList<string> Segments)
{
    internal bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    internal bool IsRegularFile =>
        (Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

    /// <summary>Views this resolved file as an existing write target, so an update reuses the
    /// segments the read already validated.</summary>
    internal WorkspaceWriteTarget AsWriteTarget()
    {
        var parent = Path.GetDirectoryName(FullPath)
                     ?? throw new InvalidOperationException("A workspace path has no parent directory.");
        return new WorkspaceWriteTarget(
            FullPath,
            parent,
            Uri,
            WorkspaceSnapshot.ToDisplayPath(Segments),
            true,
            Segments);
    }
}

/// <summary>A resolved write target. The final segment may be missing (the tool creates it),
/// <see cref="ParentPath"/> is the physical parent chain to create when absent, and
/// <see cref="Segments"/> are the logical segments the pinned write walk replays.</summary>
internal sealed record WorkspaceWriteTarget(
    string FullPath,
    string ParentPath,
    string Uri,
    string DisplayPath,
    bool Exists,
    IReadOnlyList<string> Segments);

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
