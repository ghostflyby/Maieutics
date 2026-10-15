using System.Collections.Immutable;
using FluentAssertions;
using Maieutics.Execution;
using Xunit;

namespace Maieutics.Product.Tests;

/// <summary>Pure parser tests for the git unified diff selector component
/// (ADR 0042 stage B): shapes, decorations, bounds, and typed malformations.</summary>
public sealed class GitDiffParserTests
{
    [Fact]
    public void ParsesOneFileWithHunkOriginsAndCounts()
    {
        var document = GitDiffParser.Parse(
            """
            diff --git a/src/app.ts b/src/app.ts
            index 1234567..89abcde 100644
            --- a/src/app.ts
            +++ b/src/app.ts
            @@ -1,3 +1,4 @@ section heading
             context one
            -removed line
            +added line
             context two
            +added tail
            """);

        var file = document.Files.Should().ContainSingle().Which;
        file.OldPath.Should().Be("src/app.ts");
        file.NewPath.Should().Be("src/app.ts");
        file.Binary.Should().BeFalse();
        var hunk = file.Hunks.Should().ContainSingle().Which;
        hunk.OldStart.Should().Be(1);
        hunk.Lines.Should().HaveCount(5);
        hunk.PreImageCount.Should().Be(3, "the context and removed lines are the pre-image");
        hunk.Lines[0].Origin.Should().Be(' ');
        hunk.Lines[1].Origin.Should().Be('-');
        hunk.Lines[1].Text.Should().Be("removed line");
        hunk.Lines[2].Origin.Should().Be('+');
        hunk.Lines[4].Text.Should().Be("added tail");
    }

    [Fact]
    public void ParsesMultipleFilesAndStripsABPrefixes()
    {
        var document = GitDiffParser.Parse(
            """
            diff --git a/one.txt b/one.txt
            --- a/one.txt
            +++ b/one.txt
            @@ -1 +1 @@
            -old
            +new
            diff --git a/two.txt b/two.txt
            --- a/two.txt
            +++ b/two.txt
            @@ -3 +3 @@
            -x
            +y
            """);

        document.Files.Should().HaveCount(2);
        document.Files[0].NewPath.Should().Be("one.txt");
        document.Files[1].Hunks.Should().ContainSingle().Which.OldStart.Should().Be(3);
    }

    [Fact]
    public void ParsesCreationAndDeletionWithDevNullSides()
    {
        var document = GitDiffParser.Parse(
            """
            diff --git a/fresh.ts b/fresh.ts
            new file mode 100644
            --- /dev/null
            +++ b/fresh.ts
            @@ -0,0 +1,2 @@
            +first
            +second
            diff --git a/gone.ts b/gone.ts
            deleted file mode 100644
            --- a/gone.ts
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye
            """);

        document.Files.Should().HaveCount(2);
        document.Files[0].OldPath.Should().BeNull("a created file has no old side");
        document.Files[0].Hunks.Should().ContainSingle().Which.PreImageCount.Should().Be(0);
        document.Files[1].NewPath.Should().BeNull("a deleted file has no new side");
    }

    [Fact]
    public void ParsesRenameHeadersWithoutPathPair()
    {
        var document = GitDiffParser.Parse(
            """
            diff --git a/old-name.ts b/new-name.ts
            similarity index 100%
            rename from old-name.ts
            rename to new-name.ts
            """);

        var file = document.Files.Should().ContainSingle().Which;
        file.OldPath.Should().Be("old-name.ts");
        file.NewPath.Should().Be("new-name.ts");
        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void MarksBinarySectionsWithoutHunks()
    {
        var document = GitDiffParser.Parse(
            """
            diff --git a/logo.png b/logo.png
            index 1234567..89abcde 100644
            Binary files a/logo.png and b/logo.png differ
            """);

        var file = document.Files.Should().ContainSingle().Which;
        file.Binary.Should().BeTrue();
        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void ToleratesNoNewlineMarkersAndCountsThemOut()
    {
        var document = GitDiffParser.Parse(
            """
            --- a/tail.txt
            +++ b/tail.txt
            @@ -1 +1 @@
            -old without newline
            \ No newline at end of file
            +new without newline
            \ No newline at end of file
            """);

        var hunk = document.Files.Should().ContainSingle().Which.Hunks.Should().ContainSingle().Which;
        hunk.Lines.Should().HaveCount(2, "the no-newline markers are formatting, not lines");
        hunk.PreImageCount.Should().Be(1);
    }

    [Fact]
    public void MalformedDiffsWithTypedFailures()
    {
        FluentAssertions.FluentActions.Invoking(() => GitDiffParser.Parse("not a diff at all"))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("ide_selector_patch_malformed");
        FluentAssertions.FluentActions.Invoking(() => GitDiffParser.Parse(
                """
                --- a/x.txt
                @@ -1 +1 @@
                -old
                """))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("ide_selector_patch_malformed");
        FluentAssertions.FluentActions.Invoking(() => GitDiffParser.Parse(
                """
                --- a/x.txt
                +++ b/x.txt
                @@ -1 +1 @@
                -old
                """))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("ide_selector_patch_malformed");
        FluentAssertions.FluentActions.Invoking(() => GitDiffParser.Parse(
                """
                --- a/x.txt
                +++ b/x.txt
                @@ not-a-header @@
                -old
                """))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("ide_selector_patch_malformed");
        FluentAssertions.FluentActions.Invoking(() => GitDiffParser.Parse(string.Empty))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("ide_selector_patch_malformed");
    }

    [Fact]
    public void OversizedDiffIsTypedTooLarge()
    {
        var lines = new System.Text.StringBuilder();
        for (var i = 0; i <= GitDiffParser.MaxDiffLines; i++) lines.AppendLine("decoration");
        FluentAssertions.FluentActions.Invoking(() => GitDiffParser.Parse(lines.ToString()))
            .Should().Throw<WorkspaceException>().Which.Code.Should().Be("ide_selector_patch_too_large");
    }

    [Fact]
    public void SingleLineCountHeaderDefaultsAreHonored()
    {
        var document = GitDiffParser.Parse(
            """
            --- a/solo.txt
            +++ b/solo.txt
            @@ -2 +2 @@
            -before
            +after
            """);

        var hunk = document.Files.Should().ContainSingle().Which.Hunks.Should().ContainSingle().Which;
        hunk.OldStart.Should().Be(2, "a bare '-n' header means one line at n");
        hunk.Lines.Select(static line => line.Origin).Should().Equal('-', '+');
    }
}
