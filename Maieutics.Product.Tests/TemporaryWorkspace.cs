namespace Maieutics.Product.Tests;

/// <summary>A throwaway workspace directory tree shared by workspace and resource
/// tool tests; disposing removes the whole tree.</summary>
internal sealed class TemporaryWorkspace : IDisposable
{
    private TemporaryWorkspace(string parentPath, string path)
    {
        ParentPath = parentPath;
        Path = path;
    }

    internal string ParentPath { get; }

    internal string Path { get; }

    public void Dispose()
    {
        DeleteTree(ParentPath);
    }

    /// <summary>Recursive deletion that never descends into reparse points. .NET's
    /// recursive delete throws ("the parameter is incorrect") the moment it meets a
    /// junction on Windows — it does not follow it — so managed links are removed
    /// non-recursively first and the remaining tree is deleted afterwards.</summary>
    internal static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var entry in Directory.EnumerateDirectories(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(entry, false);
            else
                DeleteTree(entry);
        }

        Directory.Delete(path, true);
    }

    internal static TemporaryWorkspace Create()
    {
        var parent = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"maieutics-workspace-tests-{Guid.NewGuid():N}");
        var path = System.IO.Path.Combine(parent, "workspace");
        Directory.CreateDirectory(path);
        return new TemporaryWorkspace(parent, path);
    }
}
