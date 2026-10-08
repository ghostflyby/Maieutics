using FluentAssertions;
using Maieutics.Execution;
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

        split.Markers.Should().BeEquivalentTo(
        [
            new FrontendSkillMarkers.Marker("code-review", "code-review"),
            new FrontendSkillMarkers.Marker("tdd", "tdd"),
        ], options => options.WithStrictOrdering());
        split.Remainder.Should().Be("before  middle  after");
    }

    [Fact]
    public void LinkTextIsDisplayMetadataAndNeedNotMatchTheName()
    {
        var split = FrontendSkillMarkers.Split(
            "[按 code-review 执行](skill://code-review)");

        split.Markers.Should().ContainSingle().Which.Should().Be(
            new FrontendSkillMarkers.Marker("code-review", "按 code-review 执行"));
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

        FrontendSkillMarkers.Split(text).Markers.Should().BeEmpty();
    }

    [Fact]
    public void BackslashSuppressesRecognitionAndIsConsumed()
    {
        var split = FrontendSkillMarkers.Split(
            $"mention: \\{Reference} and real: {Reference}");

        split.Markers.Should().ContainSingle().Which.Name.Should().Be("code-review");
        split.Remainder.Should().Contain("[code-review](skill://code-review)")
            .And.NotContain("\\[code-review](skill://code-review)");
    }

    [Fact]
    public void MentionEscapeCarriesArbitraryLinkText()
    {
        var split = FrontendSkillMarkers.Split(
            """\[see the code-review skill](skill://code-review) real: [x](skill://real)""");

        split.Markers.Should().ContainSingle().Which.Name.Should().Be("real");
        split.Remainder.Should().Contain("[see the code-review skill](skill://code-review)")
            .And.NotContain("\\[see the code-review skill");
    }

    [Fact]
    public void PlainTextWithoutReferencesPassesThroughUnchanged()
    {
        const string text = "$code-review and $100 and [x](https://example.com) and skill://bare";
        var split = FrontendSkillMarkers.Split(text);
        split.Markers.Should().BeEmpty();
        split.Remainder.Should().Be(text);
    }
}

public sealed class FrontendSkillExpansionTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"maieutics-skill-expand-{Guid.NewGuid():N}");

    private readonly List<SkillCatalog> catalogs = [];

    public async void Dispose()
    {
        foreach (var catalog in catalogs)
            await catalog.DisposeAsync();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private SkillResourceProvider CreateProvider(params (string Name, string Body)[] skills)
    {
        foreach (var (name, body) in skills)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "SKILL.md"),
                $"---\nname: {name}\ndescription: test skill\n---\n\n{body}\n");
        }

        var catalog = SkillCatalog.Create(
            [(SkillSource.Workspace, root)],
            new FakeTimeProvider(),
            NullLogger<SkillCatalog>.Instance);
        catalogs.Add(catalog);
        return new SkillResourceProvider(catalog);
    }

    [Fact]
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

        parts.Should().ContainSingle().Which.Should().BeOfType<TextContent>()
            .Which.Text.Should().Contain("[alpha](skill://alpha)")
            .And.Contain("user explicitly selected")
            .And.Contain("Alpha body.");
    }

    [Fact]
    public async Task UnknownNameFailsTyped()
    {
        var provider = CreateProvider(("alpha", "Alpha body."));

        var act = () => FrontendSkillExpansion.ExpandAsync(
            provider,
            [new FrontendSkillMarkers.Marker("absent", "absent")],
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<FrontendFailureException>())
            .Which.Code.Should().Be(FrontendErrors.SkillUnknown);
    }

    [Fact]
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

        (await act.Should().ThrowAsync<FrontendFailureException>())
            .Which.Code.Should().Be(FrontendErrors.SkillBudgetExceeded);
    }
}
