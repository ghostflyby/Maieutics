using Maieutics.Control;

namespace Maieutics.Product.Tests;

public sealed class ReplControlCredentialRegistryTests
{
    [Fact]
    public void IssuedCredentialsResolveToTheirIdentity()
    {
        var registry = new ReplControlCredentialRegistry();
        var credential = registry.Issue("session-1");

        Assert.True(registry.TryResolve(credential, out var identity));
        Assert.Equal("session-1", identity);
    }

    [Fact]
    public void UnknownCredentialsDoNotResolve()
    {
        var registry = new ReplControlCredentialRegistry();
        Assert.False(registry.TryResolve("not-issued", out _));
    }

    [Fact]
    public void CredentialsAreUniquePerIssue()
    {
        var registry = new ReplControlCredentialRegistry();
        var first = registry.Issue("session-1");
        var second = registry.Issue("session-1");
        Assert.NotEqual(second, first);
    }

    [Fact]
    public void RemoveRevokesEveryCredentialOfTheIdentity()
    {
        var registry = new ReplControlCredentialRegistry();
        var first = registry.Issue("session-1");
        var second = registry.Issue("session-1");
        registry.Issue("session-2");

        registry.Remove("session-1");
        Assert.False(registry.TryResolve(first, out _));
        Assert.False(registry.TryResolve(second, out _));
        Assert.True(registry.TryResolve(registry.Issue("session-2"), out _));
    }
}
