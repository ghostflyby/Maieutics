using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins.Contributions;

/// <summary>The MCP server discovery kind's delivery endpoint: RegistryWide shape —
/// every event delivers the full registration snapshot frame-level, the adapter filters
/// its <c>McpDiscover</c> registrations and hands them to the MCP coordinator with the
/// frame's forced set. The registration-level differential skip stays inside
/// <see cref="PluginMcpCoordinator"/>; the disposed-coordinator typed skip and the
/// historical plain/forced guard asymmetry move here verbatim from the former
/// <c>RepublishRegistry</c> / frame handler.</summary>
internal sealed class McpContributionDelivery(
    string kindName,
    string extensionPointName,
    Func<PluginMcpCoordinator?> coordinator,
    ILogger logger) : IContributionDelivery
{
    private readonly Func<PluginMcpCoordinator?> coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    private readonly ILogger logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>The plugin ids of the last delivered MCP snapshot — the face view of
    /// the sticky bag the coordinator derives from the same value. Not consulted by any
    /// A 期 path (approval transitions republish RegistryWide unconditionally); kept so
    /// the delivery surface is honest for the face question. Unlike the coordinator's
    /// gate-bound state, the dispatch members run outside the host gate and are
    /// reachable from concurrent contexts (the host receive loop, the trigger, the
    /// approval/reload reconcile tasks), so this field carries its own lock — the only
    /// delivery state that does.</summary>
    private readonly Lock snapshotGate = new();

    private IReadOnlyList<string> lastSnapshotPlugins = [];

    public string KindName { get; } = kindName;

    public ContributionDeliveryShape Shape => ContributionDeliveryShape.RegistryWide;

    public string ExtensionPointName { get; } = extensionPointName;

    public bool HoldsFace(string pluginId)
    {
        lock (snapshotGate) return lastSnapshotPlugins.Contains(pluginId);
    }

    public void PublishFrame(ContributionFrameInput frame, bool guardDisposed)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var mcpSnapshot = frame.Registrations
            .Where(registration => registration.ExtensionPoint == ExtensionPointName)
            .ToArray();
        var snapshotPlugins = mcpSnapshot
            .Select(static registration => registration.PluginId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        lock (snapshotGate) lastSnapshotPlugins = snapshotPlugins;

        if (frame.ForcedPlugins.Count == 0)
        {
            if (!guardDisposed)
            {
                // The former registry-frame plain branch: a direct publish — a
                // coordinator disposed by a concurrent generation switch surfaces here
                // exactly as it did.
                if (coordinator() is { } direct)
                    direct.PublishRegistry(mcpSnapshot);
                return;
            }

            PublishGuarded(mcpSnapshot, null);
            return;
        }

        PublishGuarded(mcpSnapshot, frame.ForcedPlugins);
    }

    public Task ReconcileAsync(string pluginId, CancellationToken cancellationToken)
    {
        // RegistryWide kinds take no targeted passes; the coordinator never schedules
        // this shape. A stray call is a programming error — say so instead of silently
        // dropping it.
        throw new InvalidOperationException(
            "The MCP contribution kind has no per-plugin reconcile pass.");
    }

    /// <summary>The former <c>RepublishRegistry</c>: a disposed coordinator (torn down
    /// by a restart racing this publish against the OLD generation's descriptors) is a
    /// typed skip, not a crash — the fresh generation republishes at start.</summary>
    private void PublishGuarded(
        PluginRegistration[] mcpSnapshot,
        IReadOnlySet<string>? forcedPlugins)
    {
        if (coordinator() is not { } target) return;
        try
        {
            if (forcedPlugins is null || forcedPlugins.Count == 0)
                target.PublishRegistry(mcpSnapshot);
            else
                target.PublishRegistry(mcpSnapshot, forcedPlugins);
        }
        catch (ObjectDisposedException)
        {
            logger.LogDebug(
                "RepublishRegistry skipped: the plugin MCP coordinator was disposed by a concurrent generation switch.");
        }
    }
}
