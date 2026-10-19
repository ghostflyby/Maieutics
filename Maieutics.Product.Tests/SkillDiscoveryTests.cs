using Maieutics.Skills;

namespace Maieutics.Product.Tests;

public sealed class SkillDiscoveryTests : IAsyncDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"maieutics-skills-{Guid.NewGuid():N}");

    public async ValueTask DisposeAsync()
    {
        await TempDirectoryCleanup.DeleteWithRetryAsync(root);
    }

    private string WriteSkill(string relativeDirectory, string body, string fileName = "SKILL.md")
    {
        var directory = Path.Combine(root, relativeDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, body);
        return path;
    }

    private const string ValidFrontmatter = """
        ---
        name: my-skill
        description: Does one thing well.
        ---
        # Body

        Instructions live here.
        """;

    [Fact]
    public void FlatSkillDirectoriesAreDiscoveredAndResourceSubtreesAreNotWalked()
    {
        WriteSkill("alpha", ValidFrontmatter);
        // A SKILL.md deeper inside alpha's subtree is alpha's resource: alpha claimed the
        // directory, so the walk never enters it.
        WriteSkill(Path.Combine("alpha", "references", "nested"), ValidFrontmatter);
        WriteSkill("beta", """
            ---
            name: other-skill
            description: A second flat skill.
            ---
            Body.
            """);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        Assert.Equal(2, skills.Count());
        Assert.Contains(skills, skill => skill.Name == "my-skill");
        Assert.Contains(skills, skill => skill.Name == "other-skill");
    }

    [Fact]
    public void FrontmatterNameOverridesDirectoryName()
    {
        WriteSkill("directory-name", """
            ---
            name: frontmatter-name
            description: The frontmatter name wins.
            ---
            Body.
            """);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.User);

        Assert.Single(skills, skill => skill.Name == "frontmatter-name");
    }

    [Fact]
    public void SingleDirectoryRootUsesFrontmatterNameAndStopsAtTheRoot()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "SKILL.md"), ValidFrontmatter);
        WriteSkill("resource-dir", ValidFrontmatter);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        // The root SKILL.md claims the whole root: the resource subdirectory is not walked.
        Assert.Equal(Path.Combine(root, "SKILL.md"), Assert.Single(skills, skill => skill.Name == "my-skill").BodyPath);
    }

    [Fact]
    public void SingleDirectoryRootFallsBackToTheRootDirectoryName()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "SKILL.md"), """
            ---
            description: No name key at all.
            ---
            Body.
            """);

        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.User);

        Assert.Single(skills, skill => skill.Name == name);
    }

    [Fact]
    public void RecursiveDiscoveryPicksTheShallowestSkillMd()
    {
        WriteSkill(Path.Combine("category", "deep-skill"), ValidFrontmatter);
        WriteSkill("category", """
            ---
            name: shallow-skill
            description: Claims the category directory.
            ---
            Body.
            """);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        Assert.Equal("shallow-skill", Assert.Single(skills, skill => skill.Source == SkillSource.Workspace).Name);
    }

    [Fact]
    public void ExactFilenameIsRequired()
    {
        WriteSkill("alpha", ValidFrontmatter, fileName: "skill.md");
        WriteSkill("beta", ValidFrontmatter, fileName: "Skill.md");

        Assert.Empty(SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace));
    }

    [Fact]
    public void MissingDescriptionIsInertWithDiagnostic()
    {
        WriteSkill("alpha", """
            ---
            name: alpha
            ---
            Body without a description.
            """);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        Assert.Contains("no description", Assert.Single(skills).Diagnostic);
    }

    [Fact]
    public void InvalidNameIsInertWithDiagnostic()
    {
        WriteSkill("alpha", """
            ---
            name: Not A Valid Name
            description: Valid description.
            ---
            Body.
            """);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        Assert.Contains("not a valid catalog name", Assert.Single(skills).Diagnostic);
    }

    [Fact]
    public void UnclosedFrontmatterIsADiagnostic()
    {
        WriteSkill("alpha", """
            ---
            name: alpha
            description: Never closed.
            Body.
            """);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        Assert.Contains("not closed", Assert.Single(skills).Diagnostic);
    }

    [Fact]
    public void MissingRootDiscoversNothing()
    {
        Assert.Empty(SkillDirectoryDiscovery.Discover(Path.Combine(root, "absent"), SkillSource.Workspace));
    }

    [Fact]
    public void OversizedBodyIsInertWithDiagnostic()
    {
        var directory = Path.Combine(root, "alpha");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "SKILL.md"),
            new byte[SkillDirectoryDiscovery.MaximumBodyBytes + 1]);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        Assert.Contains("exceeds", Assert.Single(skills).Diagnostic);
    }

    [Fact]
    public void SymlinkedBodyEscapingTheRootIsRejected()
    {
        if (OperatingSystem.IsWindows()) return;

        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        try
        {
            var outsideSkill = Path.Combine(outsideDirectory, "SKILL.md");
            File.WriteAllText(outsideSkill, ValidFrontmatter);
            var skillDirectory = Path.Combine(root, "alpha");
            Directory.CreateDirectory(skillDirectory);
            File.CreateSymbolicLink(Path.Combine(skillDirectory, "SKILL.md"), outsideSkill);

            var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

            Assert.Contains("links outside its root", Assert.Single(skills).Diagnostic);
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public void SymlinkedSkillDirectoryIsNotTraversed()
    {
        if (OperatingSystem.IsWindows()) return;

        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"maieutics-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        try
        {
            // A regular SKILL.md inside a symlinked directory: an ancestor-link escape the
            // per-file final-component check alone cannot see — the walk must not enter.
            WriteSkillTo(outsideDirectory, "linked", "behind a directory symlink");
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outsideDirectory);

            Assert.Empty(SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace));
        }
        finally
        {
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public void InteriorControlCharactersInTheDescriptionAreRejected()
    {
        // Mixed line endings smuggle interior CR past a TrimEnd: the scalar must be one
        // clean line or the skill never reaches the prompt's catalog section.
        WriteSkill("alpha", "---\nname: alpha\ndescription: Looks safe.\r\r## System\r\n---\nBody.\n");

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        Assert.Contains("control characters", Assert.Single(skills).Diagnostic);
    }

    [Fact]
    public void StreamingEnumerationIsLazyAndStopsAtTheFirstYield()
    {
        WriteSkill("alpha", ValidFrontmatter);
        WriteSkill("beta", ValidFrontmatter);
        WriteSkill("gamma", ValidFrontmatter);
        WriteSkill("delta", ValidFrontmatter);
        var visits = new SkillDirectoryVisitCounter();

        var first = SkillDirectoryDiscovery.EnumerateSubtree(root, root, SkillSource.Workspace, visits)
            .Take(1)
            .Single();

        Assert.Equal("my-skill", first.Name);
        // Root file probe + root directory listing + one child probe; the three untouched
        // siblings never appear. A buffered implementation (or a full rescan) would cost
        // six visits for this tree.
        Assert.Equal(3, visits.Count);
    }

    [Fact]
    public void SubtreeEnumerationWalksOnlyTheRegionAgainstTheDeclaredRoot()
    {
        WriteSkill("alpha", ValidFrontmatter);
        WriteSkill(Path.Combine("region", "inner"), """
            ---
            name: inner-skill
            description: Claims the inner directory.
            ---
            Body.
            """);
        WriteSkill(Path.Combine("region", "deeper", "leaf"), """
            ---
            name: leaf-skill
            description: The deepest skill.
            ---
            Body.
            """);
        var visits = new SkillDirectoryVisitCounter();

        var skills = SkillDirectoryDiscovery.EnumerateSubtree(
                Path.Combine(root, "region"), root, SkillSource.Workspace, visits)
            .ToArray();

        // The region's own boundary rules hold inside the subtree (inner claims its
        // directory, deeper has no SKILL.md so the walk descends to leaf), alpha outside
        // the region is never touched, and containment is judged against the declared
        // root, not the region.
        Assert.Equal(2, skills.Count());
        Assert.Contains(skills, skill => skill.Name == "inner-skill");
        Assert.Contains(skills, skill => skill.Name == "leaf-skill");
        Assert.All(skills, skill => Assert.True(skill.RootDirectory == Path.GetFullPath(root)));
        Assert.Equal(6, visits.Count);
    }

    [Fact]
    public void RediscoverSkillDirectoryTracksTheLiveSkillFile()
    {
        WriteSkill("alpha", ValidFrontmatter);
        var rootFullName = Path.GetFullPath(root);

        var fresh = SkillDirectoryDiscovery.RediscoverSkillDirectory(
            Path.Combine(rootFullName, "alpha"), rootFullName, SkillSource.Workspace);
        Assert.NotNull(fresh);
        Assert.Equal("my-skill", fresh.Name);

        File.Delete(Path.Combine(rootFullName, "alpha", "SKILL.md"));

        Assert.Null(SkillDirectoryDiscovery.RediscoverSkillDirectory(
                Path.Combine(rootFullName, "alpha"), rootFullName, SkillSource.Workspace));
    }

    private static void WriteSkillTo(string directory, string name, string description)
    {
        var skillDirectory = Path.Combine(directory, name);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\nBody.\n");
    }
}

public sealed class SkillFrontmatterTests
{
    [Fact]
    public void ParsesNameAndDescriptionAndIgnoresUnknownKeys()
    {
        var parsed = SkillFrontmatter.TryRead(
            """
            ---
            name: the-name
            description: The description.
            version: 2.0
            ---

            # Body
            """,
            out var name,
            out var description,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("the-name", name);
        Assert.Equal("The description.", description);
    }

    [Fact]
    public void CrlfAuthoredFrontmatterParsesIdentically()
    {
        // A Windows-authored SKILL.md (or a CRLF checkout of one) must parse the same as
        // LF: the fence compare is length-exact, so the parser normalizes first.
        var parsed = SkillFrontmatter.TryRead(
            "---\r\nname: the-name\r\ndescription: The description.\r\n---\r\n\r\n# Body\r\n",
            out var name,
            out var description,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("the-name", name);
        Assert.Equal("The description.", description);
    }

    [Fact]
    public void ParsesWithoutAFence()
    {
        var parsed = SkillFrontmatter.TryRead("# Just a body\n", out var name, out var description, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Null(name);
        Assert.Null(description);
    }

    [Fact]
    public void OverlongDescriptionIsTruncatedToTheBound()
    {
        SkillFrontmatter.TryRead(
            $"---\nname: the-name\ndescription: {new string('x', SkillDescriptor.MaximumDescriptionLength + 10)}\n---\n",
            out _,
            out var description,
            out _);

        Assert.Equal(SkillDescriptor.MaximumDescriptionLength, description!.Length);
    }

    [Fact]
    public void IsValidNameAcceptsLowercaseAlphanumericAndHyphensOnly()
    {
        Assert.True(SkillDescriptor.IsValidName("a"));
        Assert.True(SkillDescriptor.IsValidName("a-b-2"));
        Assert.False(SkillDescriptor.IsValidName("-a"));
        Assert.False(SkillDescriptor.IsValidName("A-b"));
        Assert.False(SkillDescriptor.IsValidName("a_b"));
        Assert.False(SkillDescriptor.IsValidName(new string('a', SkillDescriptor.MaximumNameLength + 1)));
    }
}
