using FluentAssertions;
using Maieutics.Control;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplControlCredentialRegistryTests
{
    [Fact]
    public void IssuedCredentialsResolveToTheirIdentity()
    {
        var registry = new ReplControlCredentialRegistry();
        var credential = registry.Issue("session-1");

        registry.TryResolve(credential, out var identity).Should().BeTrue();
        identity.Should().Be("session-1");
    }

    [Fact]
    public void UnknownCredentialsDoNotResolve()
    {
        var registry = new ReplControlCredentialRegistry();
        registry.TryResolve("not-issued", out _).Should().BeFalse();
    }

    [Fact]
    public void CredentialsAreUniquePerIssue()
    {
        var registry = new ReplControlCredentialRegistry();
        var first = registry.Issue("session-1");
        var second = registry.Issue("session-1");
        first.Should().NotBe(second);
    }

    [Fact]
    public void RemoveRevokesEveryCredentialOfTheIdentity()
    {
        var registry = new ReplControlCredentialRegistry();
        var first = registry.Issue("session-1");
        var second = registry.Issue("session-1");
        registry.Issue("session-2");

        registry.Remove("session-1");
        registry.TryResolve(first, out _).Should().BeFalse();
        registry.TryResolve(second, out _).Should().BeFalse();
        registry.TryResolve(registry.Issue("session-2"), out _).Should().BeTrue();
    }
}