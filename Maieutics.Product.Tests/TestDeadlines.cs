namespace Maieutics.Product.Tests;

/// <summary>One deadline helper for the suite: a linked source that cancels after the
/// test's own cancellation or the stage budget, whichever comes first. Disposing it at the
/// end of the stage stops the timer, so a short-lived budget never keeps a callback alive
/// past the test.</summary>
internal static class TestDeadlines
{
    internal static CancellationTokenSource Create(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    /// <summary>The shared 20 s stage budget used by unit-style tests whose operations are
    /// expected to settle far sooner.</summary>
    internal static CancellationTokenSource Create(
        CancellationToken cancellationToken)
    {
        return Create(cancellationToken, TimeSpan.FromSeconds(20));
    }
}
