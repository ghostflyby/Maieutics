using FluentAssertions;
using Maieutics.Mcp;
using Maieutics.Plugins;
using Maieutics.Plugins.Contributions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maieutics.Product.Tests;

/// <summary>Framework-level tests for the shared contribution differential engine
/// (plugin-contribution framework §4.1, A 期): the coordinator's frame dispatch,
/// single-flight coalescing and re-arm, reload-epoch forced set, kernel retry set, the
/// RegistryWide/PerPlugin shape contract, and the slot table's insertion-order
/// composition. The manager-level semantics these mirror stay covered by
/// PluginSkillsContributionTests / PluginDeclarativeExtensionsTests.</summary>
public sealed class PluginContributionCoordinatorTests
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task AnUnchangedFrameRepublishesRegistryWideButSchedulesNothing()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);
        var wide = new FakeDelivery("McpDiscover", ContributionDeliveryShape.RegistryWide);
        var perPlugin = new FakeDelivery("Skills", ContributionDeliveryShape.PerPlugin);
        coordinator.Attach([wide, perPlugin]);

        coordinator.OnRegistryFrame(new ContributionFrameInput(
            [new PluginRegistration("p1", "e1", "McpDiscover")],
            ContributionFrameInput.NoForcedPlugins,
            []));
        await Task.Delay(SettleDelay, TestContext.Current.CancellationToken);

        wide.Frames.Should().HaveCount(1, "the RegistryWide kind receives every frame");
        wide.Frames[0].Registrations.Should().ContainSingle("the snapshot is delivered unfiltered");
        wide.Guarded[0].Should().BeFalse("the registry frame's plain branch publishes unguarded");
        perPlugin.Invocations.Should().BeEmpty("an unchanged frame takes zero action");
    }

    [Fact]
    public async Task FrameTargetsSchedulePerPluginPassesOnlyForPerPluginKinds()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);
        var wide = new FakeDelivery("McpDiscover", ContributionDeliveryShape.RegistryWide);
        var skills = new FakeDelivery("Skills", ContributionDeliveryShape.PerPlugin);
        coordinator.Attach([wide, skills]);

        skills.HoldPasses();
        coordinator.OnRegistryFrame(new ContributionFrameInput(
            [],
            new HashSet<string>(["p1"], StringComparer.Ordinal),
            ["p1", "p2"]));

        await skills.WaitUntilAsync(() => skills.Invocations.Contains("p1") && skills.Invocations.Contains("p2"));
        wide.Frames.Should().ContainSingle("the RegistryWide kind publishes before scheduling");
        skills.ReleasePass();
        await skills.WaitUntilAsync(() => skills.ActivePasses == 0);
    }

    [Fact]
    public async Task EventsLandingDuringAPassCoalesceIntoOneFollowUp()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);
        var skills = new FakeDelivery("Skills", ContributionDeliveryShape.PerPlugin);
        coordinator.Attach([skills]);

        skills.HoldPasses();
        coordinator.ReconcilePlugins("Skills", ["p"]);
        await skills.WaitUntilAsync(() => skills.ActivePasses == 1);
        // Two more events land while the pass runs: both coalesce into the pending
        // flag, and the running pass drains them into exactly one follow-up (which
        // blocks again on the fresh gate until the next release).
        coordinator.ReconcilePlugins("Skills", ["p"]);
        coordinator.ReconcilePlugins("Skills", ["p"]);
        skills.ReleasePass();

        await skills.WaitUntilAsync(() => skills.CompletedPasses >= 1);
        await skills.WaitUntilAsync(() => skills.ActivePasses == 1);
        skills.ReleasePass();
        await skills.WaitUntilAsync(() => skills.ActivePasses == 0);
        skills.Invocations.Count(request => request == "p")
            .Should().Be(2, "two coalesced events produce exactly one follow-up pass");
        await Task.Delay(SettleDelay, TestContext.Current.CancellationToken);
        skills.Invocations.Count(request => request == "p").Should().Be(2, "no further pass may start");
    }

    [Fact]
    public async Task AFaultingPassReleasesTheFlightAndObservesTheException()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);
        var skills = new FakeDelivery("Skills", ContributionDeliveryShape.PerPlugin);
        coordinator.Attach([skills]);

        skills.FailNextPass = true;
        coordinator.ReconcilePlugins("Skills", ["p"]);
        await skills.WaitUntilAsync(() => skills.CompletedPasses >= 1);
        await skills.WaitUntilAsync(() => skills.ActivePasses == 0);

        // The flight is released: a later event starts a fresh pass.
        coordinator.ReconcilePlugins("Skills", ["p"]);
        await skills.WaitUntilAsync(() => skills.Invocations.Count(request => request == "p") == 2);
        await skills.WaitUntilAsync(() => skills.ActivePasses == 0);
    }

    [Fact]
    public void ReloadEpochForcesDrainOnlyOnProofOrRegression()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);
        coordinator.Reset();

        // A mark requires the frame's epoch to reach baseline+1.
        coordinator.MarkReloadForce("p");
        coordinator.DrainReloadForces(new Dictionary<string, int>())
            .Should().BeEmpty("a frame without the plugin's epoch must not drain the mark");
        coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 1 })
            .Should().ContainSingle("the epoch proves the worker was replaced");

        // Back-to-back marks raise the requirement past each epoch bump.
        coordinator.MarkReloadForce("p");
        coordinator.MarkReloadForce("p");
        coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 1 })
            .Should().BeEmpty("the raised requirement outlives the first epoch bump");
        coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 2 })
            .Should().BeEmpty("the second mark raised the requirement again");
        coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 3 })
            .Should().ContainSingle("the newest requirement is eventually proven");

        // An epoch that went backwards means a fresh host process: the mark drains.
        coordinator.MarkReloadForce("p");
        coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 0 })
            .Should().ContainSingle("a regressed epoch proves an all-fresh host");

        // A host that predates epochs offers its frame as the best completion signal.
        coordinator.MarkReloadForce("p");
        coordinator.DrainReloadForces(null).Should().ContainSingle("null epochs drain every mark");

        // An unrelated plugin's epoch bump drains nothing.
        coordinator.MarkReloadForce("p");
        coordinator.DrainReloadForces(new Dictionary<string, int> { ["other"] = 5 })
            .Should().BeEmpty();
    }

    [Fact]
    public void TheKernelRetrySetIsPerKindAndClearedByTheGenerationReset()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);

        coordinator.SetRetryPending("Skills", "p1", pending: true);
        coordinator.SetRetryPending("Skills", "p2", pending: true);
        coordinator.SetRetryPending("Other", "p1", pending: true);
        coordinator.RetrySnapshot("Skills").Should().BeEquivalentTo(["p1", "p2"]);
        coordinator.RetrySnapshot("Other").Should().ContainSingle("retry sets are per kind");
        coordinator.SetRetryPending("Skills", "p1", pending: false);
        coordinator.RetrySnapshot("Skills").Should().ContainSingle("the first successful pass clears the flag");

        coordinator.MarkReloadForce("p1");
        coordinator.Reset();
        coordinator.RetrySnapshot("Skills").Should().BeEmpty("no retry survives a generation swap");
        coordinator.DrainReloadForces(new Dictionary<string, int>())
            .Should().BeEmpty("no deferred force survives a generation swap");
    }

    [Fact]
    public async Task ApprovalTransitionsRepublishRegistryWideUnconditionallyAndRoutePerPluginTargetsByKind()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);
        var wide = new FakeDelivery("McpDiscover", ContributionDeliveryShape.RegistryWide);
        var skills = new FakeDelivery("Skills", ContributionDeliveryShape.PerPlugin);
        var other = new FakeDelivery("Notices", ContributionDeliveryShape.PerPlugin);
        coordinator.Attach([wide, skills, other]);

        // The RegistryWide input is the FULL snapshot: filtering it to a subset would
        // erase every other plugin's sticky contribution inside the consumer engine.
        var snapshot = new List<PluginRegistration>
        {
            new("p1", "e1", "McpDiscover"),
            new("p2", "e2", "Skills"),
        };
        coordinator.PublishRegistryWide(new ContributionFrameInput(
            snapshot,
            ContributionFrameInput.NoForcedPlugins,
            []));
        wide.Frames.Should().ContainSingle();
        wide.Frames[0].Registrations.Should().HaveCount(2, "the full snapshot is delivered verbatim");
        wide.Guarded[0].Should().BeTrue("approval publishes take the disposed-coordinator guard");
        await Task.Delay(SettleDelay, TestContext.Current.CancellationToken);
        skills.Invocations.Should().BeEmpty("the approval's RegistryWide half schedules no passes");
        other.Invocations.Should().BeEmpty();

        skills.HoldPasses();
        coordinator.ReconcilePlugins("Skills", ["p2"]);
        await skills.WaitUntilAsync(() => skills.ActivePasses == 1);
        other.Invocations.Should().BeEmpty("only the named kind schedules");
        skills.ReleasePass();
        await skills.WaitUntilAsync(() => skills.ActivePasses == 0);
    }

    [Fact]
    public void SeedPublishesTheSnapshotBeforeSchedulingTheSeedSet()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);
        var wide = new FakeDelivery("McpDiscover", ContributionDeliveryShape.RegistryWide);
        var skills = new FakeDelivery("Skills", ContributionDeliveryShape.PerPlugin);
        coordinator.Attach([wide, skills]);

        coordinator.Seed(new ContributionFrameInput(
            [new PluginRegistration("p1", "e1", "McpDiscover")],
            ContributionFrameInput.NoForcedPlugins,
            ["p1"]));
        wide.Frames.Should().ContainSingle("the seed publishes the initial snapshot");
        wide.Guarded[0].Should().BeTrue("the start seed takes the disposed-coordinator guard");
    }

    [Fact]
    public void ExportSetDiffReportsJoinedLeftAndChangedPlugins()
    {
        var before = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["kept"] = ["e1"],
            ["changed"] = ["e1"],
            ["gone"] = ["e1"],
        };
        var after = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["kept"] = ["e1"],
            ["changed"] = ["e1", "e2"],
            ["joined"] = ["e1"],
        };

        ContributionCoordinator.DiffExportSets(before, after)
            .Should().BeEquivalentTo(["changed", "joined", "gone"]);

        // An identical rebuild reports nothing — the unchanged-frame zero action.
        ContributionCoordinator.DiffExportSets(before, before).Should().BeEmpty();
    }

    [Fact]
    public void ExportSetsGroupOneExtensionPointsExportsPerPlugin()
    {
        var sets = ContributionCoordinator.ExportSets(
            new List<PluginRegistration>
            {
                new("p1", "e1", "Skills"),
                new("p1", "e2", "Skills"),
                new("p1", "e9", "McpDiscover"),
                new("p2", "e1", "Skills"),
            },
            "Skills");

        sets.Should().HaveCount(2);
        sets["p1"].Should().BeEquivalentTo(["e1", "e2"], "other extension points do not join the kind's sets");
        sets["p2"].Should().BeEquivalentTo(["e1"]);
    }

    // —— Slot table ——

    [Fact]
    public void SlotCompositionOrderIsDeclaredThenBagInsertionOrderThenPublished()
    {
        var table = new ContributionSlotTable("Skills");
        table.SetGenerated("p", Key("p", "e2"), Part("e2"));
        table.SetGenerated("p", Key("p", "e1"), Part("e1"));
        table.SetPublished("p", Part("pub"));
        table.SetDeclared("p", Part("decl"));

        Names(table.Compose("p")).Should().ContainInOrder("decl", "e2", "e1", "pub");
    }

    [Fact]
    public void SlotBagOrderMatchesTheFormerPlainDictionarySemantics()
    {
        var table = new ContributionSlotTable("Skills");
        // The composition order contract is the former bag's enumeration order: a plain
        // Dictionary undergoing the same operation sequence (append, in-place upsert,
        // removal, re-add with free-slot reuse). Not key-sorted, and not idealized
        // insertion order after removals — the catalog's first-writer-wins shadow rule
        // reads the caller's order as-is.
        var reference = new Dictionary<SlotSourceKey, string>(SlotSourceKey.Ordinal);

        table.SetGenerated("p", Key("p", "e1"), Part("e1"));
        reference[Key("p", "e1")] = "e1";
        table.SetGenerated("p", Key("p", "e2"), Part("e2"));
        reference[Key("p", "e2")] = "e2";
        table.SetGenerated("p", Key("p", "e1"), Part("e1-v2"));
        reference[Key("p", "e1")] = "e1-v2";
        Names(table.Compose("p")).Should().ContainInOrder(ReferenceOrder(reference));
        // An upsert keeps the key's position and takes the new value.
        Names(table.Compose("p")).Should().ContainInOrder("e1-v2", "e2");

        table.MarkSlotHeld("p");
        table.Remove("p").Should().BeTrue();
        reference.Remove(Key("p", "e1"));
        reference.Remove(Key("p", "e2"));
        table.SetGenerated("p", Key("p", "e2"), Part("e2"));
        reference[Key("p", "e2")] = "e2";
        table.SetGenerated("p", Key("p", "e1"), Part("e1"));
        reference[Key("p", "e1")] = "e1";
        Names(table.Compose("p")).Should().ContainInOrder(ReferenceOrder(reference));
        Names(table.Compose("p")).Should().NotContain("e1-v2", "the removed plugin's parts went with it");
    }

    private static IReadOnlyList<string> ReferenceOrder(Dictionary<SlotSourceKey, string> reference) =>
        reference.Values.ToArray();

    [Fact]
    public void PublishOnlyCleanupClearsTheHeldSlotAndItsParts()
    {
        var table = new ContributionSlotTable("Skills");
        table.SetPublished("p", Part("pub"));
        table.MarkSlotHeld("p");
        table.HoldsFace("p").Should().BeTrue("a held slot is a face even with only a published part");

        table.Remove("p").Should().BeTrue();
        table.HoldsFace("p").Should().BeFalse();
        table.Compose("p").Should().BeEmpty("the revoked publish-only plugin loses its published part");

        table.Remove("p").Should().BeFalse("removing an absent slot is a no-op");
    }

    [Fact]
    public void AGeneratedOrphanOutlivesADeclinedCommitWithoutPresence()
    {
        var table = new ContributionSlotTable("Skills");

        // A pass wrote a sticky part but its commit was declined: no slot presence.
        table.SetGenerated("p", Key("p", "e1"), Part("e1"));
        table.HoldsFace("p").Should().BeFalse();

        // Slot removal keyed on presence does nothing — the orphan stays for a later
        // successful pass to compose (the former early return before any mutation).
        table.Remove("p").Should().BeFalse();
        Names(table.Compose("p")).Should().ContainSingle("the sticky orphan survives");

        // A later successful pass commits and composes the orphan.
        table.SetDeclared("p", Part("decl"));
        table.MarkSlotHeld("p");
        Names(table.Compose("p")).Should().ContainInOrder("decl", "e1");
    }

    [Fact]
    public void ClearAllReturnsTheHeldSlotsAndClearsEverything()
    {
        var table = new ContributionSlotTable("Skills");
        table.SetPublished("p1", Part("pub"));
        table.MarkSlotHeld("p1");
        table.SetGenerated("p2", Key("p2", "e1"), Part("e1"));

        table.ClearAll().Should().ContainSingle().Which.Should().Be("p1");
        table.HoldsFace("p1").Should().BeFalse();
        table.Compose("p2").Should().BeEmpty("the generated bag dies with the manager");
    }

    // —— MCP delivery adapter ——

    [Fact]
    public async Task TheMcpAdapterFiltersTheSnapshotAndForwardsTheForcedSet()
    {
        var discovered = new List<PluginRegistration>();
        await using var coordinator = new PluginMcpCoordinator(
            (registration, _) =>
            {
                lock (discovered) discovered.Add(registration);
                return Task.FromResult(PluginMcpDiscoveryResult.Success([]));
            },
            (_, _) => throw new InvalidOperationException("no definitions reach generation construction"),
            NullLogger<PluginHostManager>.Instance);
        coordinator.Start();
        var adapter = new McpContributionDelivery(
            "McpDiscover",
            "McpDiscover",
            () => coordinator,
            NullLogger<McpContributionDelivery>.Instance);

        var frame = new ContributionFrameInput(
            new List<PluginRegistration>
            {
                new("p1", "e1", "McpDiscover"),
                new("p2", "e2", "Skills"),
            },
            ContributionFrameInput.NoForcedPlugins,
            []);
        adapter.PublishFrame(frame, guardDisposed: true);
        await WaitForAsync(() =>
        {
            lock (discovered) return discovered.Count == 1;
        });
        lock (discovered) discovered[0].Should().Be(
            new PluginRegistration("p1", "e1", "McpDiscover"),
            "the adapter filters the kind's own subset from the full snapshot");

        // The registration-level diff stays inside the consumer engine: an unchanged
        // frame re-publishes but invokes no discovery.
        adapter.PublishFrame(frame, guardDisposed: true);
        await Task.Delay(SettleDelay, TestContext.Current.CancellationToken);
        lock (discovered) discovered.Count.Should().Be(1, "an unchanged registration is differentially skipped");

        // The forced form re-runs the plugin's discovery over unchanged records.
        adapter.PublishFrame(
            frame with { ForcedPlugins = new HashSet<string>(["p1"], StringComparer.Ordinal) },
            guardDisposed: true);
        await WaitForAsync(() =>
        {
            lock (discovered) return discovered.Count == 2;
        });
    }

    [Fact]
    public async Task TheDisposedCoordinatorGuardFollowsTheHistoricalAsymmetry()
    {
        var coordinator = new PluginMcpCoordinator(
            (_, _) => Task.FromResult(PluginMcpDiscoveryResult.Success([])),
            (_, _) => throw new InvalidOperationException("unreachable"),
            NullLogger<PluginHostManager>.Instance);
        coordinator.Start();
        await coordinator.DisposeAsync();
        var adapter = new McpContributionDelivery(
            "McpDiscover",
            "McpDiscover",
            () => coordinator,
            NullLogger<McpContributionDelivery>.Instance);
        var frame = new ContributionFrameInput(
            [new PluginRegistration("p1", "e1", "McpDiscover")],
            ContributionFrameInput.NoForcedPlugins,
            []);

        // Guarded publishes (seed, approval, trigger, forced frames) skip a disposed
        // coordinator as a typed non-event — the fresh generation republishes at start.
        var publish = () => adapter.PublishFrame(frame, guardDisposed: true);
        publish.Should().NotThrow();

        // The registry frame's plain branch publishes unguarded: the disposal surfaces.
        var unguarded = () => adapter.PublishFrame(frame, guardDisposed: false);
        unguarded.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task TheMcpAdapterSnapshotStateSurvivesConcurrentPublishes()
    {
        // Dispatch runs outside the host gate and is reachable from concurrent
        // contexts (the host receive loop, the trigger, the approval/reload reconcile
        // tasks), so the face-view snapshot carries its own lock: publishing frames
        // from many threads while reading HoldsFace stays coherent — no torn state,
        // no lost update, no exception.
        var adapter = new McpContributionDelivery(
            "McpDiscover",
            "McpDiscover",
            () => null,
            NullLogger<McpContributionDelivery>.Instance);
        var frame = new ContributionFrameInput(
            [
                new PluginRegistration("p1", "e1", "McpDiscover"),
                new PluginRegistration("p2", "e2", "McpDiscover"),
            ],
            ContributionFrameInput.NoForcedPlugins,
            []);

        var readers = Enumerable.Range(0, 4).Select(channel => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
                _ = adapter.HoldsFace(i % 2 == 0 ? "p1" : "p3");
        }, TestContext.Current.CancellationToken));
        var writers = Enumerable.Range(0, 4).Select(channel => Task.Run(() =>
        {
            for (var i = 0; i < 5_000; i++)
                adapter.PublishFrame(frame, guardDisposed: true);
        }, TestContext.Current.CancellationToken));

        await Task.WhenAll(readers.Concat(writers));
        adapter.HoldsFace("p1").Should().BeTrue("the last frame's snapshot is the face view");
        adapter.HoldsFace("p2").Should().BeTrue();
        adapter.HoldsFace("p3").Should().BeFalse();
    }

    private static SlotSourceKey Key(string pluginId, string export) =>
        new(pluginId, export, "Skills");

    private static IReadOnlyList<ContributionDescriptor> Part(string label) =>
        [new SkillEntryContribution(CreateSkill(label))];

    private static IReadOnlyList<string> Names(IReadOnlyList<ContributionDescriptor> entries) =>
        entries.Select(static entry => entry.IdentityKey).ToArray();

    private static Skills.SkillDescriptor CreateSkill(string name) =>
        new(name, $"description of {name}", Skills.SkillSource.PluginGenerated, RootDirectory: "/");

    private static async Task WaitForAsync(Func<bool> predicate, int seconds = 5)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }

        Assert.Fail("The expected engine state was not reached within the deadline.");
    }

    private sealed class FakeDelivery(string kindName, ContributionDeliveryShape shape) : IContributionDelivery
    {
        private readonly object sync = new();
        private TaskCompletionSource release = NewTcs();
        private bool holdPasses;
        private int activePasses;
        private int completedPasses;

        public string KindName { get; } = kindName;

        public ContributionDeliveryShape Shape { get; } = shape;

        public string ExtensionPointName { get; } = kindName;

        public bool FailNextPass { get; set; }

        public List<string> Invocations { get; } = [];

        public List<ContributionFrameInput> Frames { get; } = [];

        public List<bool> Guarded { get; } = [];

        public int ActivePasses => Volatile.Read(ref activePasses);

        public int CompletedPasses => Volatile.Read(ref completedPasses);

        public bool HoldsFace(string pluginId) => false;

        public void PublishFrame(ContributionFrameInput frame, bool guardDisposed)
        {
            lock (sync)
            {
                Frames.Add(frame);
                Guarded.Add(guardDisposed);
            }
        }

        public Task ReconcileAsync(string pluginId, CancellationToken cancellationToken)
        {
            TaskCompletionSource? gate;
            lock (sync)
            {
                Invocations.Add(pluginId);
                if (FailNextPass)
                {
                    FailNextPass = false;
                    Interlocked.Increment(ref completedPasses);
                    throw new InvalidOperationException("simulated pass failure");
                }

                gate = holdPasses ? release : null;
                if (gate is null)
                {
                    Interlocked.Increment(ref completedPasses);
                    return Task.CompletedTask;
                }

                Interlocked.Increment(ref activePasses);
            }

            return gate.Task.ContinueWith(_ =>
            {
                Interlocked.Decrement(ref activePasses);
                Interlocked.Increment(ref completedPasses);
            });
        }

        /// <summary>Makes every pass entry block on the current release gate until
        /// <see cref="ReleasePass"/> completes it (the gate is shared: one release
        /// unblocks every pass awaiting it, and the follow-up pass blocks on the fresh
        /// gate until the next release).</summary>
        public void HoldPasses()
        {
            lock (sync)
            {
                holdPasses = true;
                release = NewTcs();
            }
        }

        public void ReleasePass()
        {
            TaskCompletionSource gate;
            lock (sync)
            {
                gate = release;
                release = NewTcs();
            }

            gate.TrySetResult();
        }

        public Task WaitUntilAsync(Func<bool> predicate, int seconds = 5)
        {
            return WaitForAsync(() =>
            {
                lock (sync) return predicate();
            }, seconds);
        }

        private static TaskCompletionSource NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
