using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Execution;
using Maieutics.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

public sealed class SkillCatalogTests : IAsyncDisposable
{
    private readonly string workspaceRoot =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-ws-{Guid.NewGuid():N}");

    private readonly string userRoot =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-user-{Guid.NewGuid():N}");

    public async ValueTask DisposeAsync()
    {
        await TempArea.DeleteAsync(workspaceRoot);
        await TempArea.DeleteAsync(userRoot);
    }

    private static void WriteSkill(string root, string name, string description)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\nBody of {name}.\n");
    }

    private SkillCatalog CreateCatalog(FakeTimeProvider clock, bool userSource = true)
    {
        var roots = new List<(SkillSource, string)> { (SkillSource.Workspace, workspaceRoot) };
        if (userSource)
            roots.Add((SkillSource.User, userRoot));
        return SkillCatalog.Create(roots, clock, NullLogger<SkillCatalog>.Instance);
    }

    /// <summary>Waits out a watcher-driven rebuild under the test's cancellation budget via
    /// the shared signal-driven helper (see
    /// <see cref="SkillCatalogTestWaits.AwaitAsync"/>); returns the catalog so call sites
    /// stay expression-shaped.</summary>
    private static async Task<SkillCatalog> AwaitSkillsAsync(
        SkillCatalog catalog,
        FakeTimeProvider clock,
        Func<SkillCatalogSnapshot, bool> predicate)
    {
        await SkillCatalogTestWaits.AwaitAsync(
            catalog, clock, predicate, TestContext.Current.CancellationToken);
        return catalog;
    }

    [Fact(Timeout = 30_000)]
    public async Task WorkspaceSkillShadowsTheUserNameWithADiagnostic()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "shared", "workspace wins");
        WriteSkill(userRoot, "shared", "user loses");
        WriteSkill(userRoot, "user-only", "user contributes");
        await using var catalog = CreateCatalog(new FakeTimeProvider());

        var snapshot = catalog.Current;

        Assert.Equal(2, snapshot.Skills.Count());
        Assert.Equal(SkillSource.Workspace, snapshot.Skills.Single(skill => skill.Name == "shared").Source);
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Contains("shared") && diagnostic.Contains("shadowed"));
    }

    [Fact(Timeout = 30_000)]
    public async Task InRootDuplicateNamesShadowWithADiagnostic()
    {
        WriteSkill(workspaceRoot, "alpha", "first");
        // Two different directories declaring the same frontmatter name.
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "beta"));
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "beta", "SKILL.md"), "---\nname: alpha\ndescription: second\n---\nBody.\n", TestContext.Current.CancellationToken);
        await using var catalog = CreateCatalog(new FakeTimeProvider(), userSource: false);

        var snapshot = catalog.Current;

        Assert.Equal("first", Assert.Single(snapshot.Skills, skill => skill.Name == "alpha").Description);
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Contains("alpha") && diagnostic.Contains("shadowed"));
    }

    [Fact(Timeout = 30_000)]
    public async Task WatchedChangeRebuildsTheSnapshotAfterTheDebounce()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "initial", "the first skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "initial");

        WriteSkill(workspaceRoot, "added", "arrived after start");

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Any(skill => skill.Name == "added"));

        Assert.Equal(2, catalog.Current.Skills.Count());
    }

    [Fact(Timeout = 30_000)]
    public async Task AWriteBurstCollapsesIntoFewRebuilds()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "initial", "the first skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        var rebuildsBefore = catalog.RebuildCount;

        for (var index = 0; index < 5; index++)
            WriteSkill(workspaceRoot, $"burst-{index}", $"burst skill {index}");

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Count(skill => skill.Name.StartsWith("burst-", StringComparison.Ordinal)) == 5);

        // The burst coalesces: well below one rebuild per written file. The generous
        // bound keeps CI flake out while still failing if debouncing regresses to
        // per-event rescans.
        Assert.InRange(catalog.RebuildCount - rebuildsBefore, 1, 3);
        Assert.Equal(6, catalog.Current.Skills.Count());
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposalStopsTheWatchers()
    {
        WriteSkill(workspaceRoot, "initial", "the first skill");
        var clock = new FakeTimeProvider();
        var catalog = CreateCatalog(clock, userSource: false);
        await catalog.DisposeAsync();

        WriteSkill(workspaceRoot, "added", "arrived after disposal");

        // The watchers are gone, so no debounce registration can ever arrive: give the
        // filesystem a bounded window in which a regression would have to fire, then assert
        // the snapshot is unchanged. This is a genuine quiet-period assertion, not a poll
        // for a signal (there is none to await after disposal).
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        clock.Advance(SkillCatalog.RescanDebounce);

        Assert.Single(catalog.Current.Skills, skill => skill.Name == "initial");
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposalIsIdempotent()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "initial", "the first skill");
        var catalog = CreateCatalog(new FakeTimeProvider(), userSource: false);

        await catalog.DisposeAsync();
        await catalog.DisposeAsync();
    }

    /// <summary>Writes a skill into a nested directory of the workspace root (the flat
    /// <see cref="WriteSkill"/> helper only writes top-level directories).</summary>
    private void WriteSkillAt(string relativeDirectory, string name, string description)
    {
        var directory = Path.Combine(workspaceRoot, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\nBody of {name}.\n");
    }

    [Fact(Timeout = 30_000)]
    public async Task EditingOneSkillBodyRescansOnlyThatSkillDirectory()
    {
        for (var index = 0; index < 12; index++)
            WriteSkill(workspaceRoot, $"skill-{index:D2}", $"skill {index}");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Equal(12, catalog.Current.Skills.Count());

        var visitsBefore = catalog.DirectoryVisits;
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "skill-06", "SKILL.md"), "---\nname: skill-06\ndescription: the edited body\n---\nBody.\n", TestContext.Current.CancellationToken);

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Single(skill => skill.Name == "skill-06").Description == "the edited body");

        // A full-tree walk of twelve skills costs fourteen directory visits (root file
        // probe, root listing, one probe per skill directory); the differential path
        // revalidates only the edited skill's directory, a handful of visits at most.
        Assert.True(catalog.DirectoryVisits - visitsBefore < 10);
    }

    [Fact(Timeout = 30_000)]
    public async Task AMidTreeBoundarySkillMdPreemptsTheDeeperSkill()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkillAt(Path.Combine("category", "deep"), "deep-skill", "the deep skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "deep-skill");

        WriteSkillAt("category", "shallow-skill", "claims the whole category");

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Any(skill => skill.Name == "shallow-skill") &&
            snapshot.Skills.All(skill => skill.Name != "deep-skill"));

        // The new boundary SKILL.md claims the category: the deeper old skill was a
        // resource the moment the boundary appeared and must leave the catalog.
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "shallow-skill");
    }

    [Fact(Timeout = 30_000)]
    public async Task RemovingTheBoundarySkillMdReexposesTheDeeperSkill()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkillAt("category", "shallow-skill", "claims the whole category");
        WriteSkillAt(Path.Combine("category", "deep"), "deep-skill", "the deep skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "shallow-skill");

        File.Delete(Path.Combine(workspaceRoot, "category", "SKILL.md"));

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Any(skill => skill.Name == "deep-skill") &&
            snapshot.Skills.All(skill => skill.Name != "shallow-skill"));

        Assert.Single(catalog.Current.Skills, skill => skill.Name == "deep-skill");
    }

    [Fact(Timeout = 30_000)]
    public async Task ASkillAddedDeepInTheTreeIsDiscovered()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "top-level", "the flat skill");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "category"));
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "top-level");

        WriteSkillAt(Path.Combine("category", "nested"), "nested-skill", "arrived deep in the tree");

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Any(skill => skill.Name == "nested-skill"));

        Assert.Equal(2, catalog.Current.Skills.Count());
    }

    [Fact(Timeout = 30_000)]
    public async Task ADeletedSkillDirectoryLeavesTheCatalog()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "alpha", "the first skill");
        WriteSkill(workspaceRoot, "beta", "the surviving skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Equal(2, catalog.Current.Skills.Count());

        Directory.Delete(Path.Combine(workspaceRoot, "alpha"), recursive: true);

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.All(skill => skill.Name != "alpha"));

        Assert.Single(catalog.Current.Skills, skill => skill.Name == "beta");
    }

    [Fact(Timeout = 30_000)]
    public async Task ARenamedSkillDirectoryMovesToItsNewPath()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "alpha", "the renamed skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "alpha");

        Directory.Move(Path.Combine(workspaceRoot, "alpha"), Path.Combine(workspaceRoot, "beta"));

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Any(skill =>
                skill.Name == "alpha" &&
                skill.BodyPath == Path.Combine(workspaceRoot, "beta", "SKILL.md")));

        // The catalog name travels with the frontmatter; only the body location moves.
        Assert.Equal(Path.Combine(workspaceRoot, "beta", "SKILL.md"), Assert.Single(catalog.Current.Skills, skill => skill.Name == "alpha").BodyPath);
    }

    [Fact(Timeout = 30_000)]
    public async Task ARenameAcrossTheRootBoundaryRecordsOnlyTheInsideHalf()
    {
        WriteSkill(workspaceRoot, "alpha", "the moved skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "alpha");

        // The Windows rename-out shape: the Renamed event reports the destination
        // outside the watched root. The out-of-root half holds no state of this root
        // and must be dropped — walking it as a region would plant an inert phantom
        // entry (a permanent diagnostic occupying the per-root bound) that no later
        // event of this root can release.
        var outside = Path.Combine(Path.GetTempPath(), $"maieutics-skill-away-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(outside, "alpha"));
        await File.WriteAllTextAsync(Path.Combine(outside, "alpha", "SKILL.md"), "---\nname: stowaway\ndescription: lives outside the root\n---\nBody.\n", TestContext.Current.CancellationToken);
        try
        {
            Directory.Move(Path.Combine(workspaceRoot, "alpha"), Path.Combine(outside, "moved"));
            catalog.RecordWatchedRename(
                Path.Combine(workspaceRoot, "alpha"),
                Path.Combine(outside, "moved"));

            await AwaitSkillsAsync(catalog, clock, snapshot => snapshot.Skills.IsEmpty);
            Assert.Empty(catalog.Current.Diagnostics);

            // The rename-in shape is the mirror: only the half inside the root is
            // recorded, and the moved-in skill is discovered through it.
            Directory.Move(Path.Combine(outside, "moved"), Path.Combine(workspaceRoot, "beta"));
            catalog.RecordWatchedRename(
                Path.Combine(outside, "moved"),
                Path.Combine(workspaceRoot, "beta"));

            await AwaitSkillsAsync(catalog, clock, snapshot =>
                snapshot.Skills.Any(skill => skill.Name == "alpha"));
            Assert.Empty(catalog.Current.Diagnostics);
            Assert.Equal(Path.Combine(workspaceRoot, "beta", "SKILL.md"), Assert.Single(catalog.Current.Skills, skill => skill.Name == "alpha").BodyPath);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ACasedEventPathRevalidatesTheKnownSkillDirectory()
    {
        if (OperatingSystem.IsLinux())
            Assert.Skip("The watched-path comparison is ordinal on Linux by design.");

        WriteSkill(workspaceRoot, "alpha", "before the cased touch");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "alpha");

        // A mutator touching the tree through a differently-cased spelling of the same
        // directory (case-insensitive volume): the event must match the known key and
        // revalidate it — not plant an out-of-root phantom beside the stale entry.
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "alpha", "SKILL.md"), "---\nname: alpha\ndescription: the edited body\n---\nBody.\n", TestContext.Current.CancellationToken);
        catalog.RecordWatchedChange(Path.Combine(workspaceRoot, "ALPHA", "SKILL.md"));

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Any(skill => skill.Name == "alpha" && skill.Description == "the edited body"));
        Assert.Empty(catalog.Current.Diagnostics);
        Assert.Single(catalog.Current.Skills);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheInitialScanCommitsEachSkillDirectoryProgressivelyInPrecedenceOrder()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "ws-only", "workspace contributes");
        WriteSkill(workspaceRoot, "shared", "the workspace winner");
        WriteSkill(userRoot, "shared", "the user's transient view");
        WriteSkill(userRoot, "user-only", "user contributes");
        var commits = new List<SkillCatalogSnapshot>();
        var clock = new FakeTimeProvider();
        await using var catalog = SkillCatalog.Create(
            new List<(SkillSource, string)> { (SkillSource.Workspace, workspaceRoot), (SkillSource.User, userRoot) },
            clock,
            NullLogger<SkillCatalog>.Instance,
            commits.Add);

        // One commit per discovered skill directory: the catalog goes live skill by
        // skill during the initial scan, not only at its end.
        Assert.Equal(4, commits.Count());
        Assert.NotEmpty(commits[0].Skills);

        // Every intermediate state is a valid merged snapshot and the active set only
        // grows (a later lower-source same-name entry arrives shadowed, never
        // unshadowed first): the higher-precedence root commits before the lower one,
        // so no transient ever shows the user's "shared" as the winner.
        for (var index = 1; index < commits.Count; index++)
            Assert.Subset(commits[index].Skills.Select(static skill => skill.Name).ToHashSet(), commits[index - 1].Skills.Select(static skill => skill.Name).ToHashSet());
        foreach (var state in commits)
            if (state.Skills.Any(skill => skill.Name == "shared"))
                Assert.Equal(SkillSource.Workspace, state.Skills.Single(skill => skill.Name == "shared").Source);

        Assert.Equal(3, commits[^1].Skills.Count());
        Assert.Equal(3, catalog.Current.Skills.Count());
        Assert.Single(catalog.Current.Diagnostics, diagnostic =>
            diagnostic.Contains("shared") && diagnostic.Contains("shadowed"));
    }

    [Fact(Timeout = 30_000)]
    public async Task RenamingTheSkillFileAwayRemovesTheSkill()
    {
        _ = TestContext.Current.CancellationToken;
        WriteSkill(workspaceRoot, "alpha", "the first skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        Assert.Single(catalog.Current.Skills, skill => skill.Name == "alpha");

        File.Move(
            Path.Combine(workspaceRoot, "alpha", "SKILL.md"),
            Path.Combine(workspaceRoot, "alpha", "renamed.md"));

        await AwaitSkillsAsync(catalog, clock, snapshot => snapshot.Skills.IsEmpty);

        Assert.Empty(catalog.Current.Skills);
    }
}

public sealed class SkillPromptComposerTests
{
    [Fact]
    public void EmptyCatalogLeavesThePromptNull()
    {
        Assert.Null(SkillPromptComposer.Compose("base prompt", SkillCatalogSnapshot.Empty));
    }

    [Fact]
    public void CompositionGroupsBySourceAndPointsAtSkillUris()
    {
        var snapshot = new SkillCatalogSnapshot(
        [
            new SkillDescriptor("alpha", "workspace skill", SkillSource.Workspace, "/ws"),
            new SkillDescriptor("beta", "user skill", SkillSource.User, "/user")
        ], []);

        var composed = SkillPromptComposer.Compose("base prompt", snapshot);

        Assert.StartsWith("base prompt", composed ?? throw new InvalidOperationException("the assertion subject is null"));
        Assert.Contains("## Skills", composed);
        Assert.Contains("### Workspace skills", composed);
        Assert.Contains("### User skills", composed);
        Assert.Contains("- alpha: workspace skill (skill://alpha)", composed);
        Assert.Contains("- beta: user skill (skill://beta)", composed);
    }

    [Fact]
    public void NullBasePromptComposesTheSectionAlone()
    {
        var snapshot = new SkillCatalogSnapshot(
        [
            new SkillDescriptor("alpha", "workspace skill", SkillSource.Workspace, "/ws")
        ], []);

        var composed = SkillPromptComposer.Compose(null, snapshot);

        Assert.StartsWith("## Skills", composed ?? throw new InvalidOperationException("the assertion subject is null"));
    }

    [Fact]
    public void AnOverBudgetCatalogTruncatesWithAVisibleOmissionLine()
    {
        var skills = Enumerable.Range(0, SkillPromptComposer.MaximumComposedEntries + 20)
            .Select(index => new SkillDescriptor(
                $"skill-{index:D4}",
                "description",
                SkillSource.Workspace,
                $"/ws/{index:D4}"))
            .ToArray();
        var snapshot = new SkillCatalogSnapshot([.. skills], []);

        var composed = SkillPromptComposer.Compose(null, snapshot);

        Assert.Contains("- skill-0000: description (skill://skill-0000)", composed);
        Assert.Contains("20 more skill(s) omitted", composed);
        Assert.DoesNotContain("skill-0147", composed);
        Assert.True(composed is { } text && text.Length <= SkillPromptComposer.MaximumComposedCharacters + 4096);
    }
}

public sealed class SkillResourceProviderTests : IAsyncDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-plane-{Guid.NewGuid():N}");

    public async ValueTask DisposeAsync()
    {
        await TempArea.DeleteAsync(root);
    }

    private SkillCatalog CreateCatalog()
    {
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(
            Path.Combine(root, "alpha", "SKILL.md"),
            "---\nname: alpha\ndescription: a skill\n---\n\nThe body.\n");
        return SkillCatalog.Create(
            [(SkillSource.Workspace, root)],
            new FakeTimeProvider(),
            NullLogger<SkillCatalog>.Instance);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServesTheCurrentBodyFromDisk()
    {
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);

        var result = await provider.ReadAsync(
            "skill://alpha",
            new ResourceReadRequest(long.MaxValue),
            TestContext.Current.CancellationToken);

        using var reader = new StreamReader(result.Content);
        Assert.Contains("The body.", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text/markdown", result.MimeType);
    }

    [Fact(Timeout = 30_000)]
    public async Task RejectsEveryUriShapeExceptTheVerbatimSkillForm()
    {
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);

        foreach (var uri in new[]
                     { "skill://Alpha", "skill://alpha/extra", "skill://alpha#frag", "not-a-uri", "skill://" })
        {
            var act = () => provider.ReadAsync(uri, new ResourceReadRequest(long.MaxValue), TestContext.Current.CancellationToken);
            Assert.Equal("resource_invalid_uri", (await Assert.ThrowsAnyAsync<ResourceException>(async () => { await act.Invoke(); })).Code);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task UnknownSkillIsATypedNotFound()
    {
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);

        var act = () => provider.ReadAsync(
            "skill://absent",
            new ResourceReadRequest(long.MaxValue),
            TestContext.Current.CancellationToken);

        Assert.Equal("resource_not_found", (await Assert.ThrowsAnyAsync<ResourceException>(async () => { await act.Invoke(); })).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task DeletedBodyIsATypedNotFound()
    {
        await using var catalog = CreateCatalog();
        File.Delete(Path.Combine(root, "alpha", "SKILL.md"));
        var provider = new SkillResourceProvider(catalog);

        var act = () => provider.ReadAsync(
            "skill://alpha",
            new ResourceReadRequest(long.MaxValue),
            TestContext.Current.CancellationToken);

        Assert.Equal("resource_not_found", (await Assert.ThrowsAnyAsync<ResourceException>(async () => { await act.Invoke(); })).Code);
    }

    [Fact(Timeout = 30_000)]
    public async Task CatalogListsActiveSkillsOnly()
    {
        Directory.CreateDirectory(Path.Combine(root, "inert"));
        await File.WriteAllTextAsync(Path.Combine(root, "inert", "SKILL.md"), "---\nname: inert\n---\nNo description.\n", TestContext.Current.CancellationToken);
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);

        var entries = await provider.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("skill://alpha", Assert.Single(entries).Uri);
    }

    [Fact(Timeout = 30_000)]
    public async Task BodyRelinkedOutsideTheRootAfterDiscoveryIsATypedNotFound()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The test exercises symlink semantics, which are POSIX-only.");


        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        try
        {
            await using var catalog = CreateCatalog();
            var provider = new SkillResourceProvider(catalog);
            var bodyPath = Path.Combine(root, "alpha", "SKILL.md");
            File.Delete(bodyPath);
            await File.WriteAllTextAsync(Path.Combine(outsideDirectory, "SKILL.md"), "Stolen body.\n", TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(bodyPath, Path.Combine(outsideDirectory, "SKILL.md"));

            var act = () => provider.ReadAsync(
                "skill://alpha",
                new ResourceReadRequest(long.MaxValue),
                TestContext.Current.CancellationToken);

            Assert.Equal("resource_not_found", (await Assert.ThrowsAnyAsync<ResourceException>(async () => { await act.Invoke(); })).Code);
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OversizedBodyServingIsRefused()
    {
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);
        await File.WriteAllBytesAsync(Path.Combine(root, "alpha", "SKILL.md"), new byte[16 * 1024], TestContext.Current.CancellationToken);

        var act = () => provider.ReadAsync(
            "skill://alpha",
            new ResourceReadRequest(1024),
            TestContext.Current.CancellationToken);

        Assert.Equal("resource_too_large", (await Assert.ThrowsAnyAsync<ResourceException>(async () => { await act.Invoke(); })).Code);
    }
}

public sealed class SkillsProfileProviderTests : IAsyncDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-prompt-{Guid.NewGuid():N}");

    public async ValueTask DisposeAsync()
    {
        await TempArea.DeleteAsync(root);
    }

    private static async Task<AgentRunProfile> AcquireAsync(SkillsProfileProvider provider)
    {
        var lease = await provider.AcquireAsync();
        return lease.Profile;
    }

    [Fact(Timeout = 30_000)]
    public async Task EmptyCatalogPassesTheLeaseThroughUntouched()
    {
        _ = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root);
        await using var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, root)],
            new FakeTimeProvider(),
            NullLogger<SkillCatalog>.Instance);
        var provider = new SkillsProfileProvider(new FixedProfileProvider(), catalog);

        var profile = await AcquireAsync(provider);

        Assert.Null(profile.Options.SystemPrompt);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheNextAcquisitionCarriesTheRebuiltCatalog()
    {
        Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider();
        await using var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, root)],
            clock,
            NullLogger<SkillCatalog>.Instance);
        var provider = new SkillsProfileProvider(new FixedProfileProvider(), catalog);
        Assert.Null((await AcquireAsync(provider)).Options.SystemPrompt);

        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        await File.WriteAllTextAsync(Path.Combine(root, "alpha", "SKILL.md"), "---\nname: alpha\ndescription: arrived later\n---\nBody.\n", TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        await SkillCatalogTestWaits.AwaitAsync(
            catalog, clock, snapshot => !snapshot.Skills.IsEmpty, deadline.Token);

        var composed = (await AcquireAsync(provider)).Options.SystemPrompt;

        Assert.Contains("## Skills", composed);
        Assert.Contains("skill://alpha", composed);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheComposedPromptRidesTheConfiguredBasePrompt()
    {
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        await File.WriteAllTextAsync(Path.Combine(root, "alpha", "SKILL.md"), "---\nname: alpha\ndescription: present from start\n---\nBody.\n", TestContext.Current.CancellationToken);
        await using var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, root)],
            new FakeTimeProvider(),
            NullLogger<SkillCatalog>.Instance);
        var provider = new SkillsProfileProvider(new FixedProfileProvider("base instructions"), catalog);

        var composed = (await AcquireAsync(provider)).Options.SystemPrompt;

        Assert.StartsWith("base instructions", composed ?? throw new InvalidOperationException("the assertion subject is null"));
        Assert.Contains("skill://alpha", composed);
    }

    private sealed class FixedProfileProvider(string? systemPrompt = null) : IAgentRunProfileProvider
    {
        public Task<IAgentRunProfileLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IAgentRunProfileLease>(new Lease(new AgentRunProfile(
                new StubChatClient(),
                new AgentSessionOptions { SystemPrompt = systemPrompt })));
        }

        private sealed class Lease(AgentRunProfile profile) : IAgentRunProfileLease
        {
            public AgentRunProfile Profile { get; } = profile;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(new NotSupportedException());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}

public sealed class SkillsOptionsTests
{
    [Fact]
    public void RelativeRootsAreRejected()
    {
        var options = new SkillsOptions { WorkspaceRoot = "relative/path" };
        var act = () => options.Validate();
        var assertion31 = Assert.ThrowsAny<InvalidOperationException>(() => act.Invoke());
        Assert.Matches("(?is)^.*absolute.*$", assertion31.Message);

        var userOptions = new SkillsOptions { UserRoot = "relative/path" };
        var actUser = () => userOptions.Validate();
        var assertion32 = Assert.ThrowsAny<InvalidOperationException>(() => actUser.Invoke());
        Assert.Matches("(?is)^.*absolute.*$", assertion32.Message);
    }

    [Fact]
    public void DefaultsValidate()
    {
        new SkillsOptions().Validate();
    }
}
