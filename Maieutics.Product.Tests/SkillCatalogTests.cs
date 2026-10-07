using FluentAssertions;
using Maieutics.Agent;
using Maieutics.Commands;
using Maieutics.Execution;
using Maieutics.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

public sealed class SkillCatalogTests : IDisposable
{
    private readonly string workspaceRoot =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-ws-{Guid.NewGuid():N}");

    private readonly string userRoot =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-user-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var directory in new[] { workspaceRoot, userRoot })
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
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

    /// <summary>Advances the fake clock in small steps while waiting for a watcher-driven
    /// rebuild, so the test is insensitive to whether the filesystem event lands before or
    /// after the pump's debounce delay starts.</summary>
    private async Task<SkillCatalog> AwaitSkillsAsync(
        SkillCatalog catalog,
        FakeTimeProvider clock,
        Func<SkillCatalogSnapshot, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(8));
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(catalog.Current)) return catalog;
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return catalog;
    }

    [Fact]
    public async Task WorkspaceSkillShadowsTheUserNameWithADiagnostic()
    {
        WriteSkill(workspaceRoot, "shared", "workspace wins");
        WriteSkill(userRoot, "shared", "user loses");
        WriteSkill(userRoot, "user-only", "user contributes");
        await using var catalog = CreateCatalog(new FakeTimeProvider());

        var snapshot = catalog.Current;

        snapshot.Skills.Should().HaveCount(2);
        snapshot.Skills.Single(skill => skill.Name == "shared").Source.Should().Be(SkillSource.Workspace);
        snapshot.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Contains("shared") && diagnostic.Contains("shadowed"));
    }

    [Fact]
    public async Task InRootDuplicateNamesShadowWithADiagnostic()
    {
        WriteSkill(workspaceRoot, "alpha", "first");
        // Two different directories declaring the same frontmatter name.
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "beta"));
        File.WriteAllText(
            Path.Combine(workspaceRoot, "beta", "SKILL.md"),
            "---\nname: alpha\ndescription: second\n---\nBody.\n");
        await using var catalog = CreateCatalog(new FakeTimeProvider(), userSource: false);

        var snapshot = catalog.Current;

        snapshot.Skills.Should().ContainSingle(skill => skill.Name == "alpha")
            .Which.Description.Should().Be("first");
        snapshot.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Contains("alpha") && diagnostic.Contains("shadowed"));
    }

    [Fact]
    public async Task WatchedChangeRebuildsTheSnapshotAfterTheDebounce()
    {
        WriteSkill(workspaceRoot, "initial", "the first skill");
        var clock = new FakeTimeProvider();
        await using var catalog = CreateCatalog(clock, userSource: false);
        catalog.Current.Skills.Should().ContainSingle(skill => skill.Name == "initial");

        WriteSkill(workspaceRoot, "added", "arrived after start");

        await AwaitSkillsAsync(catalog, clock, snapshot =>
            snapshot.Skills.Any(skill => skill.Name == "added"));

        catalog.Current.Skills.Should().HaveCount(2);
    }

    [Fact]
    public async Task DisposalStopsTheWatchers()
    {
        WriteSkill(workspaceRoot, "initial", "the first skill");
        var clock = new FakeTimeProvider();
        var catalog = CreateCatalog(clock, userSource: false);
        await catalog.DisposeAsync();

        WriteSkill(workspaceRoot, "added", "arrived after disposal");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        catalog.Current.Skills.Should().ContainSingle(skill => skill.Name == "initial");
    }

    [Fact]
    public async Task DisposalIsIdempotent()
    {
        WriteSkill(workspaceRoot, "initial", "the first skill");
        var catalog = CreateCatalog(new FakeTimeProvider(), userSource: false);

        await catalog.DisposeAsync();
        var act = () => catalog.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}

public sealed class SkillPromptComposerTests
{
    [Fact]
    public void EmptyCatalogLeavesThePromptNull()
    {
        SkillPromptComposer.Compose("base prompt", SkillCatalogSnapshot.Empty).Should().BeNull();
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

        composed.Should()
            .StartWith("base prompt")
            .And.Contain("## Skills")
            .And.Contain("### Workspace skills")
            .And.Contain("### User skills")
            .And.Contain("- alpha: workspace skill (skill://alpha)")
            .And.Contain("- beta: user skill (skill://beta)");
    }

    [Fact]
    public void NullBasePromptComposesTheSectionAlone()
    {
        var snapshot = new SkillCatalogSnapshot(
        [
            new SkillDescriptor("alpha", "workspace skill", SkillSource.Workspace, "/ws")
        ], []);

        var composed = SkillPromptComposer.Compose(null, snapshot);

        composed.Should().StartWith("## Skills");
    }
}

public sealed class SkillResourceProviderTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-plane-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
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

    [Fact]
    public async Task ServesTheCurrentBodyFromDisk()
    {
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);

        var result = await provider.ReadAsync(
            "skill://alpha",
            new ResourceReadRequest(long.MaxValue),
            TestContext.Current.CancellationToken);

        using var reader = new StreamReader(result.Content);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).Should().Contain("The body.");
        result.MimeType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task RejectsEveryUriShapeExceptTheVerbatimSkillForm()
    {
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);

        foreach (var uri in new[]
                     { "skill://Alpha", "skill://alpha/extra", "skill://alpha#frag", "not-a-uri", "skill://" })
        {
            var act = () => provider.ReadAsync(uri, new ResourceReadRequest(long.MaxValue), TestContext.Current.CancellationToken);
            (await act.Should().ThrowAsync<ResourceException>()).Which.Code.Should().Be("resource_invalid_uri");
        }
    }

    [Fact]
    public async Task UnknownSkillIsATypedNotFound()
    {
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);

        var act = () => provider.ReadAsync(
            "skill://absent",
            new ResourceReadRequest(long.MaxValue),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ResourceException>()).Which.Code.Should().Be("resource_not_found");
    }

    [Fact]
    public async Task DeletedBodyIsATypedNotFound()
    {
        await using var catalog = CreateCatalog();
        File.Delete(Path.Combine(root, "alpha", "SKILL.md"));
        var provider = new SkillResourceProvider(catalog);

        var act = () => provider.ReadAsync(
            "skill://alpha",
            new ResourceReadRequest(long.MaxValue),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ResourceException>()).Which.Code.Should().Be("resource_not_found");
    }

    [Fact]
    public async Task CatalogListsActiveSkillsOnly()
    {
        Directory.CreateDirectory(Path.Combine(root, "inert"));
        File.WriteAllText(
            Path.Combine(root, "inert", "SKILL.md"),
            "---\nname: inert\n---\nNo description.\n");
        await using var catalog = CreateCatalog();
        var provider = new SkillResourceProvider(catalog);

        var entries = await provider.ListAsync(TestContext.Current.CancellationToken);

        entries.Should().ContainSingle()
            .Which.Uri.Should().Be("skill://alpha");
    }
}

public sealed class SkillsProfileProviderTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-prompt-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static async Task<AgentRunProfile> AcquireAsync(SkillsProfileProvider provider)
    {
        var lease = await provider.AcquireAsync();
        return lease.Profile;
    }

    [Fact]
    public async Task EmptyCatalogPassesTheLeaseThroughUntouched()
    {
        Directory.CreateDirectory(root);
        await using var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, root)],
            new FakeTimeProvider(),
            NullLogger<SkillCatalog>.Instance);
        var provider = new SkillsProfileProvider(new FixedProfileProvider(), catalog);

        var profile = await AcquireAsync(provider);

        profile.Options.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public async Task TheNextAcquisitionCarriesTheRebuiltCatalog()
    {
        Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider();
        await using var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, root)],
            clock,
            NullLogger<SkillCatalog>.Instance);
        var provider = new SkillsProfileProvider(new FixedProfileProvider(), catalog);
        (await AcquireAsync(provider)).Options.SystemPrompt.Should().BeNull();

        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(
            Path.Combine(root, "alpha", "SKILL.md"),
            "---\nname: alpha\ndescription: arrived later\n---\nBody.\n");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline && catalog.Current.Skills.IsEmpty)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var composed = (await AcquireAsync(provider)).Options.SystemPrompt;

        composed.Should().Contain("## Skills").And.Contain("skill://alpha");
    }

    [Fact]
    public async Task TheComposedPromptRidesTheConfiguredBasePrompt()
    {
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        File.WriteAllText(
            Path.Combine(root, "alpha", "SKILL.md"),
            "---\nname: alpha\ndescription: present from start\n---\nBody.\n");
        await using var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, root)],
            new FakeTimeProvider(),
            NullLogger<SkillCatalog>.Instance);
        var provider = new SkillsProfileProvider(new FixedProfileProvider("base instructions"), catalog);

        var composed = (await AcquireAsync(provider)).Options.SystemPrompt;

        composed.Should().StartWith("base instructions").And.Contain("skill://alpha");
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
        act.Should().Throw<InvalidOperationException>().WithMessage("*absolute*");

        var userOptions = new SkillsOptions { UserRoot = "relative/path" };
        var actUser = () => userOptions.Validate();
        actUser.Should().Throw<InvalidOperationException>().WithMessage("*absolute*");
    }

    [Fact]
    public void DefaultsValidate()
    {
        var act = () => new SkillsOptions().Validate();
        act.Should().NotThrow();
    }
}
