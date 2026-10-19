using Maieutics.DenoExecution;

namespace Maieutics.Product.Tests;

/// <summary>Wiring checks for the suite-wide child-process budget and the collection cap:
/// the budget must be enabled (not the production no-op path) and it must match the
/// parallel-collection count, so neither layer silently drifts back to unbounded starts.</summary>
public sealed class TestProcessBudgetTests
{
    [Fact]
    public void TheSuiteEnablesTheProcessBudget()
    {
        // A null limit is production's inert state; every test run must install a real gate.
        Assert.NotNull(InternalDenoProcessGate.EnabledLimit);
        Assert.Equal(TestDenoProcessGate.Limit, InternalDenoProcessGate.EnabledLimit);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(8, 4)]
    [InlineData(32, 4)]
    public void TheBudgetTracksCoresWithinATwoToFourWindow(int processorCount, int expected)
    {
        Assert.Equal(expected, TestDenoProcessGate.ComputeLimit(processorCount));
    }

    [Fact]
    public async Task ALeaseRoundTripsThroughTheBudget()
    {
        // Acquire and release one slot: a lease that never released would starve every
        // later child start in this process, so the round trip is the contract that keeps
        // the budget from becoming a one-way leak.
        var lease = await TestChildProcessGate.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.True(lease.HasSlot);
        lease.Dispose();

        var again = await TestChildProcessGate.AcquireAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        again.Dispose();
    }
}
