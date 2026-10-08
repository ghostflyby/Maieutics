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
    private const string Marker = """[[maieutics:skill name="code-review"]]""";

    [Fact]
    public void ParsesWellFormedMarkersInOrder()
    {
        var split = FrontendSkillMarkers.Split(
            $"before {Marker} middle [[maieutics:skill name=\"tdd\"]] after");

        split.Markers.Should().BeEquivalentTo(
        [
            new FrontendSkillMarkers.Marker("code-review"),
            new FrontendSkillMarkers.Marker("tdd"),
        ], options => options.WithStrictOrdering());
        split.Remainder.Should().Be("before  middle  after");
    }

    [Fact]
    public void NearMissesStayLiteralText()
    {
        const string text = """
            uppercase: [[maieutics:skill name="Code-Review"]]
            space: [[maieutics:skill  name="code-review"]]
            bad char: [[maieutics:skill name="code_review"]]
            missing quote: [[maieutics:skill name=code-review]]
            unterminated: [[maieutics:skill name="code-review"
            other scheme: [[maieutics:object sha256="abc"]]
            """;

        FrontendSkillMarkers.Split(text).Markers.Should().BeEmpty();
    }

    [Fact]
    public void BackslashSuppressesRecognitionAndIsConsumed()
    {
        var split = FrontendSkillMarkers.Split(
            $"mention: \\{Marker} and real: {Marker}");

        split.Markers.Should().ContainSingle().Which.Name.Should().Be("code-review");
        split.Remainder.Should().Contain("[[maieutics:skill name=\"code-review\"]]")
            .And.NotContain("\\[[maieutics:skill");
    }

    [Fact]
    public void PlainTextWithoutMarkersPassesThroughUnchanged()
    {
        const string text = "$code-review and $100 and [[maieutics:totally-different]]";
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
            new FrontendSkillMarkers.Marker("alpha"),
            new FrontendSkillMarkers.Marker("alpha"),
        };

        var parts = await FrontendSkillExpansion.ExpandAsync(
            provider, markers, TestContext.Current.CancellationToken);

        parts.Should().ContainSingle().Which.Should().BeOfType<TextContent>()
            .Which.Text.Should().Contain("[[maieutics:skill name=\"alpha\"]]")
            .And.Contain("user explicitly selected")
            .And.Contain("Alpha body.");
    }

    [Fact]
    public async Task UnknownNameFailsTyped()
    {
        var provider = CreateProvider(("alpha", "Alpha body."));

        var act = () => FrontendSkillExpansion.ExpandAsync(
            provider,
            [new FrontendSkillMarkers.Marker("absent")],
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
                new FrontendSkillMarkers.Marker("one"),
                new FrontendSkillMarkers.Marker("two"),
                new FrontendSkillMarkers.Marker("three"),
            ],
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<FrontendFailureException>())
            .Which.Code.Should().Be(FrontendErrors.SkillBudgetExceeded);
    }
}
