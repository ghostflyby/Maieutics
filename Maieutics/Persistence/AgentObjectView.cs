namespace Maieutics.Persistence;

/// <summary>
///     Rebuilds the derived inspection view: <c>view/sessions/&lt;session-id&gt;/objects/&lt;sha256&gt;</c>
///     relative links pointing into the shared object store. The view is a projection of the
///     canonical data — repair flows one way (databases → links), never re-enters decision
///     paths, and is idempotent. Creation is best effort: on Windows without Developer Mode
///     (and for regular files blocking a link path) links are simply absent; deleting the whole
///     view tree is always safe because this repair restores it.
/// </summary>
internal static class AgentObjectView
{
    public static int Repair(
        string viewSessionsRoot,
        string objectsRoot,
        IEnumerable<SqliteTranscriptStore> stores)
    {
        var ensured = 0;
        foreach (var store in stores)
        {
            foreach (var (session, sha256) in store.GetSessionObjectReferences())
            {
                var objectPath = ObjectStore.ObjectPath(objectsRoot, sha256);
                if (!File.Exists(objectPath)) continue;

                var linkDirectory = Path.Combine(viewSessionsRoot, session.Value.ToString("N"), "objects");
                Directory.CreateDirectory(linkDirectory);
                if (TryEnsureLink(Path.Combine(linkDirectory, sha256), objectPath)) ensured++;
            }
        }

        return ensured;
    }

    private static bool TryEnsureLink(string linkPath, string targetPath)
    {
        try
        {
            // A dangling link still resolves as a link target, so this detects existing links
            // (idempotent re-runs) without following them.
            if (File.ResolveLinkTarget(linkPath, returnFinalTarget: false) is not null) return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing at the path yet: fall through to creation.
        }

        try
        {
            if (File.Exists(linkPath)) return false; // a regular file occupies the path; leave it alone

            File.CreateSymbolicLink(linkPath, Path.GetRelativePath(Path.GetDirectoryName(linkPath)!, targetPath));
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // Privileged symlink creation (Windows) or a filesystem without link support: the
            // view degrades, the canonical store does not care.
            return false;
        }
    }
}
