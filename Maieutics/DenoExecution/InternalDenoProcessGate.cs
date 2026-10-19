namespace Maieutics.DenoExecution;

/// <summary>Test-only concurrency gate over internal Deno children: while enabled, a
/// start waits (asynchronously, within a bounded budget, cooperatively with
/// cancellation) until fewer than the configured number of children are live, and
/// each child releases its slot exactly when it stops being live. Production never
/// enables the gate — it exists so the test assembly (via <c>InternalsVisibleTo</c>
/// and a module initializer) can keep parallel test classes from oversubscribing
/// shared CI runners with process startup storms; disabled callers take the no-op
/// fast path.</summary>
internal static class InternalDenoProcessGate
{
    /// <summary>Safety net for the cancellation-free path: a slot held forever by a
    /// wedged child must fail a start loudly instead of hanging it indefinitely.</summary>
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(120);

    private static SemaphoreSlim? liveChildren;

    /// <summary>Enables the gate with <paramref name="limit"/> live-child slots.</summary>
    internal static void EnableForTests(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        liveChildren = new SemaphoreSlim(limit, limit);
    }

    /// <summary>Acquires one live-child slot when the gate is enabled, or returns the
    /// no-op lease when it is not. The caller disposes the lease exactly when its
    /// child stops counting as live (helper exit, supervised-process completion).</summary>
    internal static async Task<ChildSlotLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        var gate = liveChildren;
        if (gate is null) return ChildSlotLease.None;

        try
        {
            await gate.WaitAsync(WaitBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"The internal Deno child gate had no free slot after {WaitBudget.TotalSeconds}s; " +
                "a leaked child is likely holding every slot.",
                exception);
        }

        return ChildSlotLease.Acquired(gate);
    }
}

/// <summary>Releases one live-child slot; the default instance is the disabled-gate
/// no-op. Supervised processes hand the lease to <see cref="DenoRunProcess.Start"/>,
/// which releases it when the child's completion settles; helpers dispose it when
/// their child exits.</summary>
internal readonly struct ChildSlotLease : IDisposable
{
    internal static readonly ChildSlotLease None = default;

    private readonly SemaphoreSlim? gate;

    private ChildSlotLease(SemaphoreSlim gate) => this.gate = gate;

    internal static ChildSlotLease Acquired(SemaphoreSlim gate) => new(gate);

    /// <summary>Whether the lease carries a real slot (the gate is enabled).</summary>
    internal bool HasSlot => gate is not null;

    public void Dispose() => gate?.Release();
}
