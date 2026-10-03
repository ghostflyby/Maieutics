using Maieutics.Plugins;
using Microsoft.Extensions.Time.Testing;

namespace Maieutics.Product.Tests;

/// <summary>
///     Signal-driven virtual-time waits for the plugin watcher's debounced reload: the
///     manager registers its debounce with the injected <see cref="TimeProvider"/>, so a test
///     supplies a <c>FakeTimeProvider</c> and advances past the window instead of waiting it
///     out in real time.
/// </summary>
internal static class PluginWatcherTestWaits
{
    /// <summary>Awaits <paramref name="applied"/> by advancing the fake clock past the
    /// watcher debounce. Each pass awaits one of two facts — the reload applied, or another
    /// debounce registered with the provider (the advance fires it) — and never a sleep: a
    /// file change bursts into several watcher events, each re-arming the debounce, and the
    /// pass loop converges on the event whose debounce wins. The completed signal source is
    /// replaced on the next read, so a pass never re-awaits a stale registration.</summary>
    internal static async Task AwaitReloadAppliedByAdvancingAsync(
        PluginHostManager manager,
        FakeTimeProvider clock,
        Task applied,
        CancellationToken cancellationToken)
    {
        while (!applied.IsCompleted)
        {
            Task registered = manager.WatcherDebounceRegistered;
            Task settled = await Task.WhenAny(
                applied,
                registered.WaitAsync(cancellationToken)).ConfigureAwait(false);
            if (settled == applied) return;

            clock.Advance(PluginHostManager.PluginReloadDebounce);
        }
    }
}
