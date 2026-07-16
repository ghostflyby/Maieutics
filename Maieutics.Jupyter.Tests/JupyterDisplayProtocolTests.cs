using System.Text.Json;
using FluentAssertions;
using Maieutics.Jupyter.Shared;

namespace Maieutics.Jupyter.Tests;

public sealed class JupyterDisplayProtocolTests
{
    [Fact]
    public void DisplayIdValidatesAndCreatesValues()
    {
        var created = JupyterDisplayId.Create();

        created.Value.Should().NotBeNullOrWhiteSpace();
        created.ToString().Should().Be(created.Value);
        var empty = () => new JupyterDisplayId(" ");
        var defaultValue = () => JupyterDisplayTransient.Create(default);
        empty.Should().Throw<ArgumentException>();
        defaultValue.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateDisplayRoundTripPreservesTransientFields()
    {
        var displayId = new JupyterDisplayId("display-1");
        var transient = new Dictionary<string, JsonElement>
        {
            [JupyterDisplayTransient.DisplayIdPropertyName] = JsonSerializer.SerializeToElement(displayId.Value),
            ["future"] = JsonSerializer.SerializeToElement(42)
        };
        var update = new JupyterUpdateDisplayData(
            new Dictionary<string, JsonElement>
            {
                ["text/plain"] = JsonSerializer.SerializeToElement("updated")
            },
            new Dictionary<string, JsonElement>(),
            transient);

        var json = JsonSerializer.Serialize(update, JupyterJsonContext.Default.JupyterUpdateDisplayData);
        var roundTripped = JsonSerializer.Deserialize(json, JupyterJsonContext.Default.JupyterUpdateDisplayData);

        roundTripped.Should().NotBeNull();
        JupyterDisplayTransient.GetDisplayId(roundTripped!.Transient).Should().Be(displayId);
        roundTripped.Transient!["future"].GetInt32().Should().Be(42);
    }

    [Fact]
    public void ClearOutputMissingWaitUsesFalse()
    {
        var clear = JsonSerializer.Deserialize("{}", JupyterJsonContext.Default.JupyterClearOutputContent);

        clear.Should().NotBeNull();
        clear!.Wait.Should().BeFalse();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"\"")]
    public void InvalidTransientDisplayIdIsRejected(string displayIdJson)
    {
        using var document = JsonDocument.Parse(displayIdJson);
        var transient = new Dictionary<string, JsonElement>
        {
            [JupyterDisplayTransient.DisplayIdPropertyName] = document.RootElement.Clone()
        };

        var act = () => JupyterDisplayTransient.GetDisplayId(transient);

        act.Should().Throw<JupyterProtocolException>();
    }
}