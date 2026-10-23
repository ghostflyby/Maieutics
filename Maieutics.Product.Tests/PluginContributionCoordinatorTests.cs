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
    [Fact(Timeout = 30_000)]
    public async Task AnUnchangedFrameRepublishesRegistryWideButSchedulesNothing()
    {
        _ = TestContext.Current.CancellationToken;
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);
        var wide = new FakeDelivery("McpDiscover", ContributionDeliveryShape.RegistryWide);
        var perPlugin = new FakeDelivery("Skills", ContributionDeliveryShape.PerPlugin);
        coordinator.Attach([wide, perPlugin]);

        coordinator.OnRegistryFrame(new ContributionFrameInput(
            [new PluginRegistration("p1", "e1", "McpDiscover")],
            ContributionFrameInput.NoForcedPlugins,
            []));
        // The frame carries no per-plugin targets, so the coordinator has no flight to
        // wait on and the registry-wide publish is synchronous — no settle window.
        await coordinator.FlightIdle;

        Assert.Single(wide.Frames);
        Assert.Single(wide.Frames[0].Registrations);
        Assert.False(wide.Guarded[0], "the registry frame's plain branch publishes unguarded");
        Assert.Empty(perPlugin.Invocations);
    }

    [Fact(Timeout = 30_000)]
    public async Task FrameTargetsSchedulePerPluginPassesOnlyForPerPluginKinds()
    {
        _ = TestContext.Current.CancellationToken;
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
        Assert.Single(wide.Frames);
        skills.ReleasePass();
        await skills.WaitUntilAsync(() => skills.ActivePasses == 0);
    }

    [Fact(Timeout = 30_000)]
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
        // Await the coordinator's own quiescence edge: after it, any extra follow-up pass
        // would already have been scheduled and entered, so the count can be asserted
        // without a settle window.
        await coordinator.FlightIdle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, skills.Invocations.Count(request => request == "p"));
    }

    [Fact(Timeout = 30_000)]
    public async Task AFaultingPassReleasesTheFlightAndObservesTheException()
    {
        _ = TestContext.Current.CancellationToken;
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
        Assert.Empty(coordinator.DrainReloadForces(new Dictionary<string, int>()));
        Assert.Single(coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 1 }));

        // Back-to-back marks raise the requirement past each epoch bump.
        coordinator.MarkReloadForce("p");
        coordinator.MarkReloadForce("p");
        Assert.Empty(coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 1 }));
        Assert.Empty(coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 2 }));
        Assert.Single(coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 3 }));

        // An epoch that went backwards means a fresh host process: the mark drains.
        coordinator.MarkReloadForce("p");
        Assert.Single(coordinator.DrainReloadForces(new Dictionary<string, int> { ["p"] = 0 }));

        // A host that predates epochs offers its frame as the best completion signal.
        coordinator.MarkReloadForce("p");
        Assert.Single(coordinator.DrainReloadForces(null));

        // An unrelated plugin's epoch bump drains nothing.
        coordinator.MarkReloadForce("p");
        Assert.Empty(coordinator.DrainReloadForces(new Dictionary<string, int> { ["other"] = 5 }));
    }

    [Fact]
    public void TheKernelRetrySetIsPerKindAndClearedByTheGenerationReset()
    {
        var coordinator = new ContributionCoordinator(NullLogger<ContributionCoordinator>.Instance);

        coordinator.SetRetryPending("Skills", "p1", pending: true);
        coordinator.SetRetryPending("Skills", "p2", pending: true);
        coordinator.SetRetryPending("Other", "p1", pending: true);
        Assert.Equivalent(new[] { "p1", "p2" }, coordinator.RetrySnapshot("Skills"));
        Assert.Single(coordinator.RetrySnapshot("Other"));
        coordinator.SetRetryPending("Skills", "p1", pending: false);
        Assert.Single(coordinator.RetrySnapshot("Skills"));

        coordinator.MarkReloadForce("p1");
        coordinator.Reset();
        Assert.Empty(coordinator.RetrySnapshot("Skills"));
        Assert.Empty(coordinator.DrainReloadForces(new Dictionary<string, int>()));
    }

    [Fact(Timeout = 30_000)]
    public async Task ApprovalTransitionsRepublishRegistryWideUnconditionallyAndRoutePerPluginTargetsByKind()
    {
        _ = TestContext.Current.CancellationToken;
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
        Assert.Single(wide.Frames);
        Assert.Equal(2, wide.Frames[0].Registrations.Count());
        Assert.True(wide.Guarded[0], "approval publishes take the disposed-coordinator guard");
        // The input carries no per-plugin targets, so nothing is scheduled: awaiting the
        // coordinator's idle edge confirms it without a settle window.
        await coordinator.FlightIdle;
        Assert.Empty(skills.Invocations);
        Assert.Empty(other.Invocations);

        skills.HoldPasses();
        coordinator.ReconcilePlugins("Skills", ["p2"]);
        await skills.WaitUntilAsync(() => skills.ActivePasses == 1);
        Assert.Empty(other.Invocations);
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
        Assert.Single(wide.Frames);
        Assert.True(wide.Guarded[0], "the start seed takes the disposed-coordinator guard");
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

        Assert.Equivalent(new[] { "changed", "joined", "gone" }, ContributionCoordinator.DiffExportSets(before, after));

        // An identical rebuild reports nothing — the unchanged-frame zero action.
        Assert.Empty(ContributionCoordinator.DiffExportSets(before, before));
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

        Assert.Equal(2, sets.Count());
        Assert.Equivalent(new[] { "e1", "e2" }, sets["p1"]);
        Assert.Equivalent(new[] { "e1" }, sets["p2"]);
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

        AssertContainsInOrder(Names(table.Compose("p")), "decl", "e2", "e1", "pub");
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
        AssertContainsInOrder(Names(table.Compose("p")), ReferenceOrder(reference));
        // An upsert keeps the key's position and takes the new value.
        AssertContainsInOrder(Names(table.Compose("p")), "e1-v2", "e2");

        table.MarkSlotHeld("p");
        Assert.True(table.Remove("p"));
        reference.Remove(Key("p", "e1"));
        reference.Remove(Key("p", "e2"));
        table.SetGenerated("p", Key("p", "e2"), Part("e2"));
        reference[Key("p", "e2")] = "e2";
        table.SetGenerated("p", Key("p", "e1"), Part("e1"));
        reference[Key("p", "e1")] = "e1";
        AssertContainsInOrder(Names(table.Compose("p")), ReferenceOrder(reference));
        Assert.DoesNotContain("e1-v2", Names(table.Compose("p")));
    }

    /// <summary>Subsequence assertion: the expected names appear in this order, but the
    /// actual composition may carry additional entries in between.</summary>
    private static void AssertContainsInOrder(IReadOnlyList<string> actual, params string[] expected) =>
        AssertContainsInOrder(actual, (IReadOnlyList<string>)expected);

    private static void AssertContainsInOrder(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        var position = 0;
        foreach (var item in actual)
            if (position < expected.Count && string.Equals(item, expected[position], StringComparison.Ordinal))
                position++;
        Assert.Equal(expected.Count, position);
    }

    private static IReadOnlyList<string> ReferenceOrder(Dictionary<SlotSourceKey, string> reference) =>
        reference.Values.ToArray();

    [Fact]
    public void PublishOnlyCleanupClearsTheHeldSlotAndItsParts()
    {
        var table = new ContributionSlotTable("Skills");
        table.SetPublished("p", Part("pub"));
        table.MarkSlotHeld("p");
        Assert.True(table.HoldsFace("p"), "a held slot is a face even with only a published part");

        Assert.True(table.Remove("p"));
        Assert.False(table.HoldsFace("p"));
        Assert.Empty(table.Compose("p"));

        Assert.False(table.Remove("p"), "removing an absent slot is a no-op");
    }

    [Fact]
    public void AGeneratedOrphanOutlivesADeclinedCommitWithoutPresence()
    {
        var table = new ContributionSlotTable("Skills");

        // A pass wrote a sticky part but its commit was declined: no slot presence.
        table.SetGenerated("p", Key("p", "e1"), Part("e1"));
        Assert.False(table.HoldsFace("p"));

        // Slot removal keyed on presence does nothing — the orphan stays for a later
        // successful pass to compose (the former early return before any mutation).
        Assert.False(table.Remove("p"));
        Assert.Single(Names(table.Compose("p")));

        // A later successful pass commits and composes the orphan.
        table.SetDeclared("p", Part("decl"));
        table.MarkSlotHeld("p");
        AssertContainsInOrder(Names(table.Compose("p")), "decl", "e1");
    }

    [Fact]
    public void ClearAllReturnsTheHeldSlotsAndClearsEverything()
    {
        var table = new ContributionSlotTable("Skills");
        table.SetPublished("p1", Part("pub"));
        table.MarkSlotHeld("p1");
        table.SetGenerated("p2", Key("p2", "e1"), Part("e1"));

        Assert.Equal("p1", Assert.Single(table.ClearAll()));
        Assert.False(table.HoldsFace("p1"));
        Assert.Empty(table.Compose("p2"));
    }

    // —— MCP delivery adapter ——

    [Fact(Timeout = 30_000)]
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
        await coordinator.RegistryReconciled.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lock (discovered) Assert.Equal(new PluginRegistration("p1", "e1", "McpDiscover"), discovered[0]);

        // The registration-level diff stays inside the consumer engine: an unchanged
        // frame re-publishes but invokes no discovery. The revision's completion settles
        // only after the diff ran, so the assertion needs no settle window.
        adapter.PublishFrame(frame, guardDisposed: true);
        await coordinator.RegistryReconciled.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lock (discovered) Assert.Single(discovered);

        // The forced form re-runs the plugin's discovery over unchanged records.
        adapter.PublishFrame(
            frame with { ForcedPlugins = new HashSet<string>(["p1"], StringComparer.Ordinal) },
            guardDisposed: true);
        await coordinator.RegistryReconciled.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lock (discovered) Assert.Equal(2, discovered.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheDisposedCoordinatorGuardFollowsTheHistoricalAsymmetry()
    {
        _ = TestContext.Current.CancellationToken;
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
        adapter.PublishFrame(frame, guardDisposed: true);

        // The registry frame's plain branch publishes unguarded: the disposal surfaces.
        var unguarded = () => adapter.PublishFrame(frame, guardDisposed: false);
        Assert.ThrowsAny<ObjectDisposedException>(() => unguarded.Invoke());
    }

    [Fact(Timeout = 30_000)]
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
        Assert.True(adapter.HoldsFace("p1"), "the last frame's snapshot is the face view");
        Assert.True(adapter.HoldsFace("p2"));
        Assert.False(adapter.HoldsFace("p3"));
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
        using var deadline = TestDeadlines.Create(TestContext.Current.CancellationToken, TimeSpan.FromSeconds(seconds));
        while (true)
        {
            if (predicate()) return;
            await Task.Delay(20, deadline.Token);
            if (deadline.IsCancellationRequested)
                Assert.Fail(
                    "The contribution engine did not reach the expected state within the stage budget; "
                    + "the budget is the failure, not the observation.");
        }
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
