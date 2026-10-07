using FluentAssertions;
using Maieutics.Skills;

namespace Maieutics.Product.Tests;

public sealed class SkillDiscoveryTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"maieutics-skills-{Guid.NewGuid():N}");

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

        skills.Should().HaveCount(2)
            .And.Contain(skill => skill.Name == "my-skill")
            .And.Contain(skill => skill.Name == "other-skill");
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

        skills.Should().ContainSingle(skill => skill.Name == "frontmatter-name");
    }

    [Fact]
    public void SingleDirectoryRootUsesFrontmatterNameAndStopsAtTheRoot()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "SKILL.md"), ValidFrontmatter);
        WriteSkill("resource-dir", ValidFrontmatter);

        var skills = SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace);

        // The root SKILL.md claims the whole root: the resource subdirectory is not walked.
        skills.Should().ContainSingle(skill => skill.Name == "my-skill")
            .Which.BodyPath.Should().Be(Path.Combine(root, "SKILL.md"));
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

        skills.Should().ContainSingle(skill => skill.Name == name);
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

        skills.Should().ContainSingle(skill => skill.Source == SkillSource.Workspace)
            .Which.Name.Should().Be("shallow-skill");
    }

    [Fact]
    public void ExactFilenameIsRequired()
    {
        WriteSkill("alpha", ValidFrontmatter, fileName: "skill.md");
        WriteSkill("beta", ValidFrontmatter, fileName: "Skill.md");

        SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace).Should().BeEmpty();
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

        skills.Should().ContainSingle()
            .Which.Diagnostic.Should().Contain("no description");
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

        skills.Should().ContainSingle()
            .Which.Diagnostic.Should().Contain("not a valid catalog name");
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

        skills.Should().ContainSingle()
            .Which.Diagnostic.Should().Contain("not closed");
    }

    [Fact]
    public void MissingRootDiscoversNothing()
    {
        SkillDirectoryDiscovery.Discover(Path.Combine(root, "absent"), SkillSource.Workspace)
            .Should().BeEmpty();
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

        skills.Should().ContainSingle()
            .Which.Diagnostic.Should().Contain("exceeds");
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

            skills.Should().ContainSingle()
                .Which.Diagnostic.Should().Contain("links outside its root");
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

            SkillDirectoryDiscovery.Discover(root, SkillSource.Workspace).Should().BeEmpty();
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

        skills.Should().ContainSingle()
            .Which.Diagnostic.Should().Contain("control characters");
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

        parsed.Should().BeTrue();
        error.Should().BeNull();
        name.Should().Be("the-name");
        description.Should().Be("The description.");
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

        parsed.Should().BeTrue();
        error.Should().BeNull();
        name.Should().Be("the-name");
        description.Should().Be("The description.");
    }

    [Fact]
    public void ParsesWithoutAFence()
    {
        var parsed = SkillFrontmatter.TryRead("# Just a body\n", out var name, out var description, out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        name.Should().BeNull();
        description.Should().BeNull();
    }

    [Fact]
    public void OverlongDescriptionIsTruncatedToTheBound()
    {
        SkillFrontmatter.TryRead(
            $"---\nname: the-name\ndescription: {new string('x', SkillDescriptor.MaximumDescriptionLength + 10)}\n---\n",
            out _,
            out var description,
            out _);

        description!.Length.Should().Be(SkillDescriptor.MaximumDescriptionLength);
    }

    [Fact]
    public void IsValidNameAcceptsLowercaseAlphanumericAndHyphensOnly()
    {
        SkillDescriptor.IsValidName("a").Should().BeTrue();
        SkillDescriptor.IsValidName("a-b-2").Should().BeTrue();
        SkillDescriptor.IsValidName("-a").Should().BeFalse();
        SkillDescriptor.IsValidName("A-b").Should().BeFalse();
        SkillDescriptor.IsValidName("a_b").Should().BeFalse();
        SkillDescriptor.IsValidName(new string('a', SkillDescriptor.MaximumNameLength + 1)).Should().BeFalse();
    }
}
