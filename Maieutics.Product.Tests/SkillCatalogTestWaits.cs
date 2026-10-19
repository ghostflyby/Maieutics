using Maieutics.Skills;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

/// <summary>
///     Signal-driven waits for the skill catalog's debounced watcher pump. The catalog
///     registers its rescan debounce with the injected <see cref="TimeProvider"/>, so a test
///     supplying a <see cref="FakeTimeProvider"/> advances past the window instead of waiting
///     it out in real time.
/// </summary>
internal static class SkillCatalogTestWaits
{
    /// <summary>Awaits a snapshot accepted by <paramref name="predicate"/> by advancing the
    /// fake clock past the pump's debounce. Each pass waits for one of two facts — the next
    /// snapshot publication, or the pump registering its debounce timer with the provider —
    /// and advances only once a timer provably exists. The registration signal hands back a
    /// completed edge, so a pump that wins the race against this read still gets its clock
    /// advanced. A write burst may publish several snapshots, so passes converge on the
    /// snapshot the predicate accepts; cancellation surfaces at the wait itself rather than
    /// after a timed-out poll.</summary>
    internal static async Task AwaitAsync(
        SkillCatalog catalog,
        FakeTimeProvider clock,
        Func<SkillCatalogSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!predicate(catalog.Current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var published = catalog.WaitForNextSnapshotAsync();
            var registered = catalog.WatcherDebounceRegistered;
            var settled = await Task.WhenAny(
                published,
                registered.WaitAsync(cancellationToken)).ConfigureAwait(false);
            if (settled == published)
            {
                await published.ConfigureAwait(false);
                continue;
            }

            // Surface cancellation at the wait instead of advancing a canceled edge: a
            // canceled wait is not a registered debounce.
            await settled.ConfigureAwait(false);
            clock.Advance(SkillCatalog.RescanDebounce);
        }
    }
}
