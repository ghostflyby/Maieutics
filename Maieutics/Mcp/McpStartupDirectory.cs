namespace Maieutics.Mcp;

internal sealed class McpStartupDirectory
{
    internal McpStartupDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    internal string Path { get; }
}
