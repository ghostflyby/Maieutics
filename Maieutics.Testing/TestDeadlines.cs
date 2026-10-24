namespace Maieutics.Testing;

/// <summary>One deadline helper for the whole suite: a linked source that cancels after the
/// test's own cancellation or the stage budget, whichever comes first. Disposing it at the
/// end of the stage stops the timer, so a short-lived budget never keeps a callback alive
/// past the test. Every wait takes the resulting token — the deadline is the outer budget
/// of a stage, never the detector of anything.</summary>
public static class TestDeadlines
{
    /// <summary>Creates a stage budget linked to the test's cancellation. The stage fails
    /// (its token fires) if the budget expires before the awaited completion events
    /// arrive.</summary>
    public static CancellationTokenSource Create(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    /// <summary>The shared 20 s stage budget used by unit-style tests whose operations are
    /// expected to settle far sooner.</summary>
    public static CancellationTokenSource Create(
        CancellationToken cancellationToken)
    {
        return Create(cancellationToken, TimeSpan.FromSeconds(20));
    }
}
