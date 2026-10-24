using System.Text;
using System.Text.Json;
using Maieutics.Control;
using Maieutics.Permissions;
using Maieutics.Skills;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins.Contributions;

/// <summary>The host-state input one skills reconcile pass captures under the manager's
/// gate before any await: the generation token, the descriptor (null = gone or
/// approval-blocked — the whole slot goes), the plugin's declared Skills entries, its
/// live Skills generator exports, and whether it currently holds a slot.</summary>
internal sealed record SkillPassInput(
    CancellationToken InvokeToken,
    PluginDescriptor? Descriptor,
    IReadOnlyList<PluginExtensionEntry> DeclaredEntries,
    IReadOnlyList<string> GeneratorExports,
    bool HoldsContribution);

/// <summary>The skills contribution kind's delivery endpoint: PerPlugin shape — one
/// targeted pass per plugin. The pass body is the former
/// <c>PluginHostManager.ReconcilePluginSkillsAsync</c> moved verbatim: declarative
/// roots enumerate fresh, worker generator output invokes with the approval gate, each
/// export keeps its own last-good contribution on failure, and the slot commits under
/// the host gate with the generation-token recheck that declines a commit racing the
/// stop path. The FS-watching catalog consumer
/// (<see cref="Skills.SkillCatalog"/>) is untouched — this adapter only recomposes the
/// plugin's contribution slot and commits it.</summary>
internal sealed class SkillsContributionDelivery(
    PluginHostManager host,
    string kindName,
    string extensionPointName,
    ContributionSlotTable table,
    ContributionCoordinator engine,
    SkillCatalog? skillCatalog,
    VariableTable? manifestVariables,
    Func<string?>? workspaceRootAccessor,
    ILogger logger) : IContributionDelivery
{
    /// <summary>The kind's bounds come from the contract's metadata table (framework
    /// §5) — the former private constants, now one source shared with the catalog.</summary>
    private static int MaximumDeclaredSkillRoots =>
        SkillsContributionKind.Instance.Metadata.MaxDeclaredEntriesPerDeclaration
        ?? throw new InvalidOperationException("The skills metadata must declare the roots bound.");

    private static int MaximumComputedSkillEntries =>
        SkillsContributionKind.Instance.Metadata.MaxComputedEntries
        ?? throw new InvalidOperationException("The skills metadata must declare the computed-entries bound.");

    private readonly PluginHostManager _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly ContributionSlotTable _table = table ?? throw new ArgumentNullException(nameof(table));
    private readonly ContributionCoordinator _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public string KindName { get; } = kindName;

    public ContributionDeliveryShape Shape => ContributionDeliveryShape.PerPlugin;

    public string ExtensionPointName { get; } = extensionPointName;

    public bool HoldsFace(string pluginId)
    {
        // The slot table owns no lock: every method — this read included — runs under the
        // host manager's gate, matching the reconcile and commit paths.
        lock (_host.Gate)
        {
            return _table.HoldsFace(pluginId);
        }
    }

    public void PublishFrame(ContributionFrameInput frame, bool guardDisposed)
    {
        // PerPlugin kinds take targeted passes, never frame publications; the
        // coordinator only calls this on RegistryWide shapes.
        throw new InvalidOperationException(
            "The skills contribution kind has no frame publication.");
    }

    public async Task ReconcileAsync(string pluginId, CancellationToken cancellationToken)
    {
        if (skillCatalog is null) return;

        // `host` is deliberately touched outside the gate here and inside `lock (host.Gate)`
        // elsewhere: CaptureSkillPassInputLock takes the gate itself, and the invoke below
        // must await without holding it.
        // ReSharper disable once InconsistentlySynchronizedField
        var input = _host.CaptureSkillPassInputLock(pluginId, KindName, ExtensionPointName);
        if (input.Descriptor is null)
        {
            RemoveContribution(pluginId);
            return;
        }

        // No face and no held slot: nothing to contribute and nothing to clear (an
        // event that did not change this plugin's skill surface must stay a no-op).
        if (input.DeclaredEntries.Count == 0 && input.GeneratorExports.Count == 0 && !input.HoldsContribution) return;

        try
        {
            var contribution = new List<SkillDescriptor>();
            foreach (var entry in input.DeclaredEntries)
                CollectDeclarativeSkills(
                    pluginId, input.Descriptor.RootDirectory, input.Descriptor.Permissions, entry.Data, contribution);

            // A failed or unusable generator invoke keeps the export's sticky part, but
            // the failure is remembered: the next registry frame retries the plugin (the
            // export-set diff alone would never fire again), matching the MCP
            // coordinator's "never-succeeded stays new" discipline.
            var invokeFailed = false;
            // `host` is deliberately touched outside the gate here: the invoke must await
            // without holding the manager's gate (the sticky commit re-acquires it).
            // ReSharper disable once InconsistentlySynchronizedField
            foreach (var exportName in input.GeneratorExports)
            {
                var outcome = await _host.InvokeExtensionPointAsync(
                    pluginId,
                    exportName,
                    ExtensionPointName,
                    SkillsInvokeRequest(),
                    input.InvokeToken).ConfigureAwait(false);
                if (outcome.IsError)
                {
                    invokeFailed = true;
                    _logger.LogWarning(
                        "Plugin '{PluginId}' skill generator '{Export}' failed ({Code}): {Message}; keeping its last good contribution.",
                        pluginId,
                        exportName,
                        outcome.Code,
                        outcome.Message);
                    continue;
                }

                if (ParseGeneratedSkills(outcome.Value) is not { Count: > 0 } parsed)
                {
                    invokeFailed = true;
                    _logger.LogWarning(
                        "Plugin '{PluginId}' skill generator '{Export}' returned no usable descriptors; keeping its last good contribution.",
                        pluginId,
                        exportName);
                    continue;
                }

                // Sticky state is per-export and mutates under the gate (the
                // capability path reads it there); the commit below concatenates
                // every export's part, so sibling exports never replace each other.
                // Exports that vanished keep their sticky part until the plugin's
                // slot itself is removed.
                lock (_host.Gate)
                {
                    // The token recheck keeps a straggler pass from writing a dead
                    // generation's sticky part into a cleared slot bag: the stop path
                    // cancels the lifetime before it clears, so a pass that already
                    // received its invokes must not commit them afterwards.
                    if (input.InvokeToken.IsCancellationRequested) return;
                    _table.SetGenerated(
                        pluginId,
                        new SlotSourceKey(pluginId, exportName, ExtensionPointName),
                        Wrap(parsed));
                }
            }

            // Compose and commit under the gate so a concurrent skills.publish
            // cannot interleave: whichever commits later reads the other's parts.
            // The generation token check declines a commit racing the stop path —
            // the stop cancels the lifetime BEFORE it clears every contribution,
            // so a straggler pass cannot resurrect a cleared slot.
            lock (_host.Gate)
            {
                if (input.InvokeToken.IsCancellationRequested || _host.IsApprovalBlockedLock(pluginId)) return;
                _engine.SetRetryPending(KindName, pluginId, invokeFailed);
                _table.SetDeclared(pluginId, Wrap(contribution));
                skillCatalog.UpdatePluginContribution(pluginId, Unwrap(_table.Compose(pluginId)));
                _table.MarkSlotHeld(pluginId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // One plugin's poison degrades only that plugin: its sticky parts stay
            // as the contribution, and other plugins' passes are unaffected.
            _logger.LogWarning(
                exception,
                "Reconciling plugin '{PluginId}' skill contributions failed; its last contributions stay active.",
                pluginId);
        }
    }

    /// <summary>Removes one plugin's whole contribution slot (descriptor presence and
    /// approval keyed — the plugin is gone from the descriptor set or blocked). The
    /// slot table mutates only under the host gate.</summary>
    public void RemoveContribution(string pluginId)
    {
        if (skillCatalog is null) return;
        lock (_host.Gate)
        {
            if (!_table.Remove(pluginId)) return;
            // The slot is gone (blocked or removed): no retry can succeed anymore.
            _engine.SetRetryPending(KindName, pluginId, pending: false);
            skillCatalog.RemovePluginContribution(pluginId);
        }
    }

    /// <summary>Replaces one plugin's published skill set (ADR 0039 stage 3) and commits
    /// the recomposed contribution slot. Composition happens under the gate so a
    /// concurrent reconcile pass cannot interleave its commit.</summary>
    /// <returns>The number of usable published entries, or null when this host carries no
    /// skill catalog (the capability is unavailable).</returns>
    public int? Publish(string pluginId, IReadOnlyList<SkillDescriptor> published)
    {
        if (skillCatalog is null) return null;

        lock (_host.Gate)
        {
            _table.SetPublished(pluginId, Wrap(published));
            skillCatalog.UpdatePluginContribution(pluginId, Unwrap(_table.Compose(pluginId)));
            _table.MarkSlotHeld(pluginId);
        }

        return published.Count(static skill => skill.Diagnostic is null);
    }

    /// <summary>Clears every plugin's slot (manager disposal) and returns the plugin ids
    /// that held one, so the caller can detach them from the catalog under the gate.</summary>
    public IReadOnlyList<string> ClearAll()
    {
        lock (_host.Gate)
        {
            return _table.ClearAll();
        }
    }

    private static IReadOnlyList<ContributionDescriptor> Wrap(IReadOnlyList<SkillDescriptor> skills)
    {
        return skills.Select(static skill => (ContributionDescriptor)new SkillEntryContribution(skill)).ToArray();
    }

    private static IReadOnlyList<SkillDescriptor> Unwrap(IReadOnlyList<ContributionDescriptor> entries)
    {
        return entries
            .Select(static entry => entry is SkillEntryContribution skill
                ? skill.Skill
                : throw new InvalidOperationException(
                    $"The skills slot carries a foreign descriptor type '{entry.GetType().Name}'."))
            .ToArray();
    }

    /// <summary>Interprets one declarative Skills entry: <c>roots</c> expand through the
    /// manifest variable table, resolve against the plugin root, and must either stay
    /// inside it or fall inside the plugin's own (fingerprinted) read grants — the rule
    /// that makes literal-pattern fingerprinting safe (ADR 0039 stage 2a). Every failure
    /// is a per-root inert diagnostic; a poison root never aborts the pass.</summary>
    private void CollectDeclarativeSkills(
        string pluginId,
        string rootDirectory,
        PluginPermissionGrants grants,
        JsonElement data,
        List<SkillDescriptor> results)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("roots", out var roots) ||
            roots.ValueKind != JsonValueKind.Array)
        {
            results.Add(InertSkill(pluginId, "roots", "The Skills entry declares no 'roots' array."));
            return;
        }

        var index = 0;
        foreach (var root in roots.EnumerateArray())
        {
            if (index >= MaximumDeclaredSkillRoots)
            {
                results.Add(InertSkill(
                    pluginId,
                    $"roots[{index}]",
                    $"The Skills entry declares more than {MaximumDeclaredSkillRoots} roots; the excess is ignored."));
                return;
            }

            var pattern = root.ValueKind == JsonValueKind.String ? root.GetString() : null;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                results.Add(InertSkill(pluginId, $"roots[{index}]", "The skill root is not a string."));
                index++;
                continue;
            }

            string expanded;
            try
            {
                expanded = manifestVariables is { } variables ? variables.Expand(pattern) : pattern;
            }
            catch (PermissionException exception)
            {
                results.Add(InertSkill(pluginId, $"roots[{index}]", exception.Message));
                index++;
                continue;
            }

            string resolved;
            try
            {
                resolved = Path.IsPathRooted(expanded)
                    ? Path.GetFullPath(expanded)
                    : Path.GetFullPath(Path.Combine(rootDirectory, expanded));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                results.Add(InertSkill(
                    pluginId,
                    $"roots[{index}]",
                    $"The skill root cannot be resolved: {exception.Message}"));
                index++;
                continue;
            }

            if (!SkillDirectoryDiscovery.IsWithinRoot(rootDirectory, resolved) &&
                !IsCoveredByReadGrant(grants.Read, rootDirectory, resolved))
            {
                results.Add(InertSkill(
                    pluginId,
                    $"roots[{index}]",
                    $"The skill root '{resolved}' resolves outside the plugin root and is not covered by a read grant."));
                index++;
                continue;
            }

            results.AddRange(SkillDirectoryDiscovery.Discover(resolved, SkillSource.PluginDeclared));
            index++;
        }
    }

    /// <summary>Kernel-side read-grant coverage for a skill root resolving outside the
    /// plugin root: grant values normalize (file:// stripped, relative resolved against
    /// the plugin root) and cover the root when the grant is it or an ancestor of it.</summary>
    private static bool IsCoveredByReadGrant(PluginPermissionGrant read, string pluginRoot, string resolvedRoot)
    {
        if (read.AllowAll) return true;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var declared in read.Values)
        {
            var grant = declared.Trim();
            if (grant.Length == 0) continue;
            if (grant.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                grant = grant["file://".Length..];
            if (!Path.IsPathRooted(grant)) grant = Path.Combine(pluginRoot, grant);
            string absolute;
            try
            {
                absolute = Path.GetFullPath(grant).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (string.Equals(absolute, resolvedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison) ||
                resolvedRoot.StartsWith(absolute + Path.DirectorySeparatorChar, comparison))
                return true;
        }

        return false;
    }

    /// <summary>Validates a computed catalog (generator output or published entries): an
    /// array of {name, description, body?} objects under the same bounds a filesystem
    /// skill satisfies. Invalid entries ride as inert diagnostics; a non-array value is
    /// null.</summary>
    internal static List<SkillDescriptor>? ParseGeneratedSkills(
        JsonElement? value,
        SkillSource source = SkillSource.PluginGenerated)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array) return null;
        var results = new List<SkillDescriptor>();
        foreach (var entry in array.EnumerateArray())
        {
            if (results.Count >= MaximumComputedSkillEntries)
            {
                results.Add(InertSkill(
                    SourceLabel(source),
                    "entry",
                    $"The contribution exceeds {MaximumComputedSkillEntries} entries; the excess is ignored."));
                break;
            }

            if (entry.ValueKind != JsonValueKind.Object)
            {
                results.Add(InertSkill(SourceLabel(source), "entry", "The generated entry is not an object."));
                continue;
            }

            var name = entry.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var description = entry.TryGetProperty("description", out var descriptionElement) &&
                              descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;
            var body = entry.TryGetProperty("body", out var bodyElement) &&
                       bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name) || !SkillDescriptor.IsValidName(name))
            {
                results.Add(InertSkill(
                    SourceLabel(source),
                    "entry",
                    $"The generated skill name '{SanitizeEcho(name)}' is not a valid catalog name."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(description) || !SkillFrontmatter.IsCleanScalar(description))
            {
                results.Add(InertSkill(name, "entry", "The generated skill has no clean one-line description."));
                continue;
            }

            if (description.Length > SkillDescriptor.MaximumDescriptionLength)
                description = description[..SkillDescriptor.MaximumDescriptionLength];
            if (body is { Length: > SkillDescriptor.MaximumGeneratedBodyCharacters })
            {
                results.Add(InertSkill(name, "entry", "The generated skill body exceeds the size bound."));
                continue;
            }

            results.Add(new SkillDescriptor(
                name,
                description,
                source,
                RootDirectory: "/",
                BodyText: body));
        }

        return results;
    }

    /// <summary>Truncates and cleans a plugin-controlled string echoed into a diagnostic
    /// message (log hygiene only — diagnostics never reach the model).</summary>
    private static string SanitizeEcho(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder();
        foreach (var character in value.Length > 32 ? value[..32] : value)
            builder.Append(character < ' ' || character == '\u007f' ? '?' : character);
        return builder.ToString();
    }

    private static string SourceLabel(SkillSource source)
    {
        return source == SkillSource.PluginPublished ? "published" : "generator";
    }

    /// <summary>The generator invoke request: the live workspace root (or null) — never
    /// arbitrary environment, per ADR 0039 stage 2b.</summary>
    private JsonElement? SkillsInvokeRequest()
    {
        var workspaceRoot = workspaceRootAccessor?.Invoke();
        return JsonSerializer.SerializeToElement(
            new SkillsInvokePayload(workspaceRoot),
            ReplControlJsonContext.Default.SkillsInvokePayload);
    }

    private static SkillDescriptor InertSkill(string pluginId, string slot, string diagnostic)
    {
        return new SkillDescriptor(
            $"{pluginId}:{slot}",
            string.Empty,
            SkillSource.PluginDeclared,
            RootDirectory: "/",
            Diagnostic: diagnostic);
    }
}
