using System.Collections.Immutable;
using Maieutics.Execution;

namespace Maieutics.Skills;

/// <summary>Serves the skill catalog as the read-only <c>skill://</c> resource plane
/// (ADR 0026, ADR 0039): <c>skill://{name}</c> streams the named skill's current body.
/// The whitelist is the catalog itself — the plane reads only files discovery accepted,
/// which is what lets user-root skills (outside the workspace) be readable without opening
/// any broader filesystem grant. Bodies are read fresh from disk on every request, and the
/// descriptor's root containment is re-checked so a post-discovery swap (rename, relink)
/// cannot serve a path outside the declared root. Inert skills never reach this plane.</summary>
internal sealed class SkillResourceProvider(SkillCatalog catalog) : IResourceProvider, IResourceCatalogProvider
{
    internal const string Scheme = "skill";

    private const long MaximumSkillBytes = 4 * 1024 * 1024;

    private const string MimeType = "text/markdown";

    public string Id => "skills";

    public ResourceProviderClass Class => ResourceProviderClass.BuiltIn;

    public IReadOnlyList<ResourceClaim> Claims => [new ResourceClaim(Scheme)];

    public ValueTask<ResourceReadResult> ReadAsync(
        string uri,
        ResourceReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();

        // The URI parser case-folds hosts, so an uppercase spelling would silently address a
        // different catalog entry; require the verbatim form `skill://{name}` (the objects
        // plane's rule) and a syntactically valid catalog name.
        if (!ResourceRegistry.TryParseUri(uri, out var parsed) ||
            parsed.Scheme != Scheme ||
            !SkillDescriptor.IsValidName(parsed.Host) ||
            parsed.AbsolutePath.Length > 1 ||
            !string.Equals(uri, $"{Scheme}://{parsed.Host}", StringComparison.Ordinal))
        {
            throw new ResourceException(
                "resource_invalid_uri",
                "The value must be a skill:// URI of the form skill://{name}.");
        }

        var descriptor = FindCurrent(parsed.Host);
        if (descriptor?.BodyPath is not { } bodyPath)
        {
            throw new ResourceException(
                "resource_not_found",
                $"No skill named '{parsed.Host}' is in the current catalog.");
        }

        // Read-time containment: lexical root check plus final link-target resolution, so a
        // file swapped or relinked after discovery still cannot read outside its root.
        if (!SkillDirectoryDiscovery.IsWithinRoot(descriptor.RootDirectory, bodyPath))
        {
            throw new ResourceException(
                "resource_not_found",
                $"The skill '{parsed.Host}' no longer resolves inside its root.");
        }

        try
        {
            var link = new FileInfo(bodyPath).ResolveLinkTarget(returnFinalTarget: true);
            if (link is not null && !SkillDirectoryDiscovery.IsWithinRoot(descriptor.RootDirectory, link.FullName))
            {
                throw new ResourceException(
                    "resource_not_found",
                    $"The skill '{parsed.Host}' no longer resolves inside its root.");
            }
        }
        catch (IOException exception)
        {
            throw new ResourceException("resource_not_found", exception.Message, exception);
        }

        Stream content;
        try
        {
            content = File.OpenRead(bodyPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ResourceException(
                "resource_not_found",
                $"The skill '{parsed.Host}' body can no longer be opened: {exception.Message}",
                exception);
        }

        var bounded = new MemoryStream();
        using (content)
        {
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                copied += read;
                if (copied > Math.Min(request.MaxBytes, MaximumSkillBytes))
                    throw new ResourceException(
                        "resource_too_large",
                        $"The skill '{parsed.Host}' exceeds the read limit.");
                bounded.Write(buffer, 0, read);
            }
        }

        bounded.Position = 0;
        return ValueTask.FromResult(new ResourceReadResult(bounded, MimeType));
    }

    public ValueTask<ImmutableArray<ResourceCatalogEntry>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = catalog.Current;
        var entries = snapshot.Skills
            .Select(skill => new ResourceCatalogEntry(
                Id,
                $"{Scheme}://{skill.Name}",
                skill.Name,
                skill.Description,
                MimeType,
                "resource",
                null))
            .ToImmutableArray();
        return ValueTask.FromResult(entries);
    }

    private SkillDescriptor? FindCurrent(string name)
    {
        foreach (var skill in catalog.Current.Skills)
            if (string.Equals(skill.Name, name, StringComparison.Ordinal))
                return skill;
        return null;
    }
}
