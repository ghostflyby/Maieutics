namespace Maieutics.Execution;

/// <summary>The built-in provider for <c>workspace://local/...</c> URIs. Reads flow
/// through the existing <see cref="Workspace"/> containment path (symlink rejection,
/// openat traversal), so workspace reads keep their exact error codes and
/// zero-copy file access (ADR 0026 decision 1). The workspace directory listing is
/// served by <c>list_directory</c>, so this provider has no catalog.</summary>
internal sealed class WorkspaceResourceProvider : IResourceProvider
{
    private readonly Workspace workspace;

    internal WorkspaceResourceProvider(Workspace workspace)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public string Id => "workspace.local";

    public ResourceProviderClass Class => ResourceProviderClass.BuiltIn;

    public IReadOnlyList<ResourceClaim> Claims { get; } = [new ResourceClaim("workspace", "local")];

    public async ValueTask<ResourceReadResult> ReadAsync(
        string uri,
        ResourceReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = workspace.Capture();
        var path = snapshot.Resolve(uri, allowRoot: false);
        if (path.IsDirectory)
            throw new WorkspaceException(
                "workspace_not_file",
                "The workspace URI does not identify a regular file.");

        if (!path.IsRegularFile)
            throw new WorkspaceException(
                "workspace_not_regular_file",
                "Workspace text tools can read only regular files.");

        var stream = snapshot.OpenVerifiedRead(path);
        try
        {
            if (stream.Length > request.MaxBytes)
                throw new ResourceException(
                    "resource_too_large",
                    $"The resource exceeds the {request.MaxBytes} byte read limit.");

            return new ResourceReadResult(stream, null);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
