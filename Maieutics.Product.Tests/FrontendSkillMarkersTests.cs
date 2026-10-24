using Maieutics.Frontend;
using Maieutics.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

public sealed class FrontendSkillMarkersTests
{
    private const string Reference = "[code-review](skill://code-review)";

    [Fact]
    public void ParsesWellFormedReferencesInOrder()
    {
        var split = FrontendSkillMarkers.Split(
            $"before {Reference} middle [tdd](skill://tdd) after");

        Assert.Equivalent(new[] { new FrontendSkillMarkers.Marker("code-review", "code-review"), new FrontendSkillMarkers.Marker("tdd", "tdd") }, split.Markers, strict: true);
        Assert.Equal("before  middle  after", split.Remainder);
    }

    [Fact]
    public void LinkTextIsDisplayMetadataAndNeedNotMatchTheName()
    {
        var split = FrontendSkillMarkers.Split(
            "[按 code-review 执行](skill://code-review)");

        Assert.Equal(new FrontendSkillMarkers.Marker("code-review", "按 code-review 执行"), Assert.Single(split.Markers));
    }

    [Fact]
    public void NearMissesStayLiteralText()
    {
        const string text = """
            uppercase host: [x](skill://Code-Review)
            bad name char: [x](skill://code_review)
            url path: [x](skill://code-review/extra)
            url query: [x](skill://code-review?x=1)
            title: [x](skill://code-review "best")
            https scheme: [x](https://code-review)
            empty host tail: [x](skill://)
            """;

        Assert.Empty(FrontendSkillMarkers.Split(text).Markers);
    }

    [Fact]
    public void BackslashSuppressesRecognitionAndIsConsumed()
    {
        var split = FrontendSkillMarkers.Split(
            $"mention: \\{Reference} and real: {Reference}");

        Assert.Equal("code-review", Assert.Single(split.Markers).Name);
        Assert.Contains("[code-review](skill://code-review)", split.Remainder);
        Assert.DoesNotContain("\\[code-review](skill://code-review)", split.Remainder);
    }

    [Fact]
    public void MentionEscapeCarriesArbitraryLinkText()
    {
        var split = FrontendSkillMarkers.Split(
            """\[see the code-review skill](skill://code-review) real: [x](skill://real)""");

        Assert.Equal("real", Assert.Single(split.Markers).Name);
        Assert.Contains("[see the code-review skill](skill://code-review)", split.Remainder);
        Assert.DoesNotContain("\\[see the code-review skill", split.Remainder);
    }

    [Fact]
    public void PlainTextWithoutReferencesPassesThroughUnchanged()
    {
        const string text = "$code-review and $100 and [x](https://example.com) and skill://bare";
        var split = FrontendSkillMarkers.Split(text);
        Assert.Empty(split.Markers);
        Assert.Equal(text, split.Remainder);
    }
}

public sealed class FrontendSkillExpansionTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-expand-{Guid.NewGuid():N}");

    private readonly List<SkillCatalog> _catalogs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var catalog in _catalogs)
            await catalog.DisposeAsync();
        try
        {
            await TempArea.DeleteAsync(_root, CancellationToken.None);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }


    private SkillResourceProvider CreateProvider(params (string Name, string Body)[] skills)
    {
        foreach (var (name, body) in skills)
        {
            var directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "SKILL.md"),
                $"---\nname: {name}\ndescription: test skill\n---\n\n{body}\n");
        }

        var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, _root)],
            new FakeTimeProvider(),
            NullLogger<SkillCatalog>.Instance);
        _catalogs.Add(catalog);
        return new SkillResourceProvider(catalog);
    }

    [Fact(Timeout = 60_000)]
    public async Task ExpandsEachDistinctNameOnceWithFramedBody()
    {
        var provider = CreateProvider(("alpha", "Alpha body."));
        var markers = new[]
        {
            new FrontendSkillMarkers.Marker("alpha", "alpha"),
            new FrontendSkillMarkers.Marker("alpha", "alpha"),
        };

        var parts = await FrontendSkillExpansion.ExpandAsync(
            provider, markers, TestContext.Current.CancellationToken);

        Assert.Contains("[alpha](skill://alpha)", Assert.IsType<TextContent>(Assert.Single(parts)).Text);
        Assert.Contains("user explicitly selected", Assert.IsType<TextContent>(Assert.Single(parts)).Text);
        Assert.Contains("Alpha body.", Assert.IsType<TextContent>(Assert.Single(parts)).Text);
    }

    [Fact(Timeout = 60_000)]
    public async Task UnknownNameFailsTyped()
    {
        var provider = CreateProvider(("alpha", "Alpha body."));

        var act = () => FrontendSkillExpansion.ExpandAsync(
            provider,
            [new FrontendSkillMarkers.Marker("absent", "absent")],
            TestContext.Current.CancellationToken);

        Assert.Equal(FrontendErrors.SkillUnknown, (await Assert.ThrowsAnyAsync<FrontendFailureException>(async () => { await act.Invoke(); })).Code);
    }

    [Fact(Timeout = 60_000)]
    public async Task TurnBudgetOverflowFailsTheWholeSubmission()
    {
        var threeMiB = new string('x', 3 * 1024 * 1024);
        var provider = CreateProvider(
            ("one", threeMiB),
            ("two", threeMiB),
            ("three", threeMiB));

        var act = () => FrontendSkillExpansion.ExpandAsync(
            provider,
            [
                new FrontendSkillMarkers.Marker("one", "one"),
                new FrontendSkillMarkers.Marker("two", "two"),
                new FrontendSkillMarkers.Marker("three", "three"),
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(FrontendErrors.SkillBudgetExceeded, (await Assert.ThrowsAnyAsync<FrontendFailureException>(async () => { await act.Invoke(); })).Code);
    }
}
