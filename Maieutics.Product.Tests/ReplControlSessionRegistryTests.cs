using Maieutics.Control;

namespace Maieutics.Product.Tests;

public sealed class ReplControlSessionRegistryTests
{
    [Fact]
    public void RegisterAndUnregisterRoundTrip()
    {
        var registry = new ReplControlSessionRegistry();
        Assert.False(registry.TryGetSession(42, out _));

        registry.Register(42, "session-a");
        Assert.True(registry.TryGetSession(42, out var sessionId));
        Assert.Equal("session-a", sessionId);

        registry.Unregister(42);
        Assert.False(registry.TryGetSession(42, out _));
    }

    [Fact]
    public void RegisterReplacesExistingMapping()
    {
        var registry = new ReplControlSessionRegistry();
        registry.Register(42, "session-a");
        registry.Register(42, "session-b");

        Assert.True(registry.TryGetSession(42, out var sessionId));
        Assert.Equal("session-b", sessionId);
    }

    [Fact]
    public void RejectsInvalidRegistration()
    {
        var registry = new ReplControlSessionRegistry();
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => (registry).Register(0, "session"));
    }
}