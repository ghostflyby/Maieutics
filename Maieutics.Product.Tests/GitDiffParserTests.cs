using Maieutics.Execution;

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

        var file = Assert.Single(document.Files);
        Assert.Equal("src/app.ts", file.OldPath);
        Assert.Equal("src/app.ts", file.NewPath);
        Assert.False(file.Binary);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(5, hunk.Lines.Count());
        Assert.Equal(3, hunk.PreImageCount);
        Assert.Equal(' ', hunk.Lines[0].Origin);
        Assert.Equal('-', hunk.Lines[1].Origin);
        Assert.Equal("removed line", hunk.Lines[1].Text);
        Assert.Equal('+', hunk.Lines[2].Origin);
        Assert.Equal("added tail", hunk.Lines[4].Text);
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

        Assert.Equal(2, document.Files.Count());
        Assert.Equal("one.txt", document.Files[0].NewPath);
        Assert.Equal(3, Assert.Single(document.Files[1].Hunks).OldStart);
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

        Assert.Equal(2, document.Files.Count());
        Assert.Null(document.Files[0].OldPath);
        Assert.Equal(0, Assert.Single(document.Files[0].Hunks).PreImageCount);
        Assert.Null(document.Files[1].NewPath);
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

        var file = Assert.Single(document.Files);
        Assert.Equal("old-name.ts", file.OldPath);
        Assert.Equal("new-name.ts", file.NewPath);
        Assert.Empty(file.Hunks);
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

        var file = Assert.Single(document.Files);
        Assert.True(file.Binary);
        Assert.Empty(file.Hunks);
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

        var hunk = Assert.Single(Assert.Single(document.Files).Hunks);
        Assert.Equal(2, hunk.Lines.Count());
        Assert.Equal(1, hunk.PreImageCount);
    }

    [Fact]
    public void MalformedDiffsWithTypedFailures()
    {
        Assert.Equal("ide_selector_patch_malformed", Assert.ThrowsAny<WorkspaceException>(() => GitDiffParser.Parse("not a diff at all")).Code);
        Assert.Equal("ide_selector_patch_malformed", Assert.ThrowsAny<WorkspaceException>(() => GitDiffParser.Parse(
                """
                --- a/x.txt
                @@ -1 +1 @@
                -old
                """)).Code);
        Assert.Equal("ide_selector_patch_malformed", Assert.ThrowsAny<WorkspaceException>(() => GitDiffParser.Parse(
                """
                --- a/x.txt
                +++ b/x.txt
                @@ -1 +1 @@
                -old
                """)).Code);
        Assert.Equal("ide_selector_patch_malformed", Assert.ThrowsAny<WorkspaceException>(() => GitDiffParser.Parse(
                """
                --- a/x.txt
                +++ b/x.txt
                @@ not-a-header @@
                -old
                """)).Code);
        Assert.Equal("ide_selector_patch_malformed", Assert.ThrowsAny<WorkspaceException>(() => GitDiffParser.Parse(string.Empty)).Code);
    }

    [Fact]
    public void OversizedDiffIsTypedTooLarge()
    {
        var lines = new System.Text.StringBuilder();
        for (var i = 0; i <= GitDiffParser.MaxDiffLines; i++) lines.AppendLine("decoration");
        Assert.Equal("ide_selector_patch_too_large", Assert.ThrowsAny<WorkspaceException>(() => GitDiffParser.Parse(lines.ToString())).Code);
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

        var hunk = Assert.Single(Assert.Single(document.Files).Hunks);
        Assert.Equal(2, hunk.OldStart);
        Assert.Equal(['-', '+'], hunk.Lines.Select(static line => line.Origin));
    }
}
