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
        Directory.Delete(ParentPath, true);
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
