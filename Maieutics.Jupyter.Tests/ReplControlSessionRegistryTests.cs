using FluentAssertions;
using Maieutics.Control;

namespace Maieutics.Jupyter.Tests;

public sealed class ReplControlSessionRegistryTests
{
    [Fact]
    public void RegisterAndUnregisterRoundTrip()
    {
        var registry = new ReplControlSessionRegistry();
        registry.TryGetSession(42, out _).Should().BeFalse();

        registry.Register(42, "session-a");
        registry.TryGetSession(42, out var sessionId).Should().BeTrue();
        sessionId.Should().Be("session-a");

        registry.Unregister(42);
        registry.TryGetSession(42, out _).Should().BeFalse();
    }

    [Fact]
    public void RegisterReplacesExistingMapping()
    {
        var registry = new ReplControlSessionRegistry();
        registry.Register(42, "session-a");
        registry.Register(42, "session-b");

        registry.TryGetSession(42, out var sessionId).Should().BeTrue();
        sessionId.Should().Be("session-b");
    }

    [Fact]
    public void RejectsInvalidRegistration()
    {
        var registry = new ReplControlSessionRegistry();
        registry.Invoking(r => r.Register(0, "session")).Should().Throw<ArgumentOutOfRangeException>();
    }
}
