namespace Maieutics.Product.Tests;

/// <summary>Workspace-shaped wrapper over the shared <see cref="Testing.TempArea" />: a
/// parent temp area whose <c>workspace</c> subdirectory is the tested workspace root.
/// Disposal delegates to the area's reparse-safe delete.</summary>
internal sealed class TemporaryWorkspace : IDisposable
{
    private readonly TempArea _area;

    private TemporaryWorkspace(TempArea area)
    {
        _area = area;
        ParentPath = area.Path;
        Path = System.IO.Path.Combine(area.Path, "workspace");
    }

    internal string ParentPath { get; }

    internal string Path { get; }

    public void Dispose()
    {
        _area.Dispose();
    }

    internal static TemporaryWorkspace Create()
    {
        var area = TempArea.Create("workspace-tests");
        Directory.CreateDirectory(System.IO.Path.Combine(area.Path, "workspace"));
        return new TemporaryWorkspace(area);
    }
}
