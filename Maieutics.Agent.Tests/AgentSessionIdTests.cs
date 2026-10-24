namespace Maieutics.Agent.Tests;

public sealed class AgentSessionIdTests
{
    [Fact]
    public void CreatedIdentifiersCarryATimeOrderedUuid7Timestamp()
    {
        // UUIDv7: the first 12 hex digits of the "N" form are the 48-bit Unix millisecond
        // timestamp. .NET does not order ids within one millisecond, so the guarantee asserted
        // here is a non-decreasing timestamp plus uniqueness.
        var identifiers = Enumerable.Range(0, 64).Select(_ => AgentSessionId.Create()).ToArray();

        var stamps = identifiers.Select(id => id.Value.ToString("N")[..12]).ToArray();
        Assert.Equal(stamps.OrderBy(static stamp => stamp, StringComparer.Ordinal), stamps);
        Assert.All(identifiers.Select(id => id.Value.ToString("N")[12]), version => Assert.True(version == '7'));
        Assert.Equal(identifiers.Select(id => id.Value).Count(), identifiers.Select(id => id.Value).Distinct().Count());
    }
}
