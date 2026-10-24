using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Maieutics.Control;
using Maieutics.DenoExecution;
using Maieutics.DenoRepl;
using Maieutics.Execution;
using Maieutics.Mcp;
using Maieutics.Permissions;
using Maieutics.Plugins.Contributions;
using Maieutics.Processes;
using Maieutics.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Maieutics.Plugins;

internal sealed record PluginRegistration(string PluginId, string ExportName, string ExtensionPoint);

internal sealed record PluginHostStatus(
    PluginHostState State,
    int PluginCount,
    int RegistrationCount,
    bool HostProcessRequired,
    bool ControlConnected,
    int PendingApprovals = 0);

/// <summary>One plugin's approval classification (ADR 0037) for status and command
/// rendering. <see cref="Exempt"/> declares nothing privileged; <see cref="Approved"/>
/// matches a persisted record; <see cref="PendingApproval"/> has no matching record;
/// <see cref="BlockedByDependency"/> matches its own record but a transitive dependency
/// does not.</summary>
internal enum PluginApprovalState
{
    Exempt,
    Approved,
    PendingApproval,
    BlockedByDependency
}

/// <summary>The approval view of one discovered plugin: current classification, the
/// requested versus approved declaration fingerprints, and both grant summaries so a
/// renderer can show what changed since the last approval. The approved fingerprint is
/// carried for that renderer even though the current list view renders only the state.</summary>
// ReSharper disable NotAccessedPositionalProperty.Global
internal sealed record PluginApprovalInfo(
    string PluginId,
    string Name,
    PluginApprovalState State,
    string RequestedFingerprint,
    string? ApprovedFingerprint,
    PluginPermissionGrants RequestedGrants,
    PluginPermissionGrants? ApprovedGrants,
    int WorkerCount);
// ReSharper restore NotAccessedPositionalProperty.Global

internal enum PluginHostState
{
    NotStarted,
    Starting,
    Ready,
    Stopping,
    Stopped,
    Canceled,
    Failed,
    Exited
}

/// <summary>Executes one catalogued kernel capability on behalf of a plugin worker
/// (ADR 0020 §7.2). The composition root binds this to the script tool registry;
/// the plugin host itself never implements capabilities.</summary>
internal delegate Task<JsonElement> PluginCapabilityExecutor(
    string tool,
    JsonElement arguments,
    CancellationToken cancellationToken);

/// <summary>Receives one plugin UI frame for publication into the session's comm plane
/// (ADR 0038 stage 3); bound by the composition root.</summary>
internal delegate ValueTask PluginUiFrameSink(
    string pluginId,
    PluginUiFramePayload frame,
    CancellationToken cancellationToken);

internal readonly record struct ExtensionCallOutcome(
    bool IsError,
    JsonElement? Value,
    string Code,
    string Message)
{
    public static ExtensionCallOutcome Result(JsonElement? value)
    {
        return new ExtensionCallOutcome(false, value, string.Empty, string.Empty);
    }

    public static ExtensionCallOutcome Error(string code, string message)
    {
        return new ExtensionCallOutcome(true, null, code, message);
    }
}

/// <summary>Outcome of a <c>host.repl.derive</c> instruction. <see cref="Spawned"/> means the host
/// reported <c>host.repl.spawned</c> (pid + broker policy registered); <see cref="Failed"/> means
/// the host reported <c>host.repl.deriveFailed</c> before any pid existed.</summary>
internal readonly record struct ReplDeriveOutcome(bool Failed, string Message)
{
    public static ReplDeriveOutcome Spawned()
    {
        return new ReplDeriveOutcome(false, string.Empty);
    }

    public static ReplDeriveOutcome DeriveFailed(string message)
    {
        return new ReplDeriveOutcome(true, message);
    }
}

/// <summary>
///     Owns plugin discovery, the plugin host process, its control-channel WebSocket connection, and
///     extension point invocation routing. REPL connections stay in <see cref="ReplControlHost" />;
///     host connections are attached here so the kernel can call into plugin workers without a
///     reverse dependency. The generic host starts and stops this same instance.
/// </summary>
internal sealed class PluginHostManager(
    string pluginsRoot,
    string pluginDataRoot,
    string socketPath,
    DenoReplOptions denoOptions,
    PluginHostModule modules,
    ReplControlSessionRegistry sessionRegistry,
    ILogger<PluginHostManager> logger,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider,
    IMcpWorkspaceRootsSource? workspaceRootsSource = null,
    IMcpElicitationPresenter? elicitationPresenter = null,
    DenoPermissionBroker? broker = null,
    string? pluginApprovalsPath = null,
    SkillCatalog? skillCatalog = null,
    VariableTable? manifestVariables = null,
    Func<string?>? workspaceRootAccessor = null)
    : IHostedService, IAsyncDisposable, IReplPolicyRegistrar
{
    /// <summary>The read-only IDE tools `ide.query` may invoke (ADR 0042 decision 8's
    /// read group, as tool names): hover, navigation, symbols, the aggregate and
    /// per-document diagnostics, and the read-only request escape hatch. This copy
    /// exists because Plugins sits below Frontend in the dependency direction; a
    /// Product.Tests sync test pins it against the kernel's IdeFunction names —
    /// keep both lists aligned when the read group changes.</summary>
    internal static readonly IReadOnlySet<string> IdeQueryToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "ide_diagnostics", "ide_hover", "ide_navigate", "ide_document_symbols", "ide_workspace_symbols", "ide_request"
    };

    private const int EnvelopeVersion = 1;

    // 30s: the extension-point channel also covers on-demand worker starts
    // (ADR 0035) — a cold spawn's handshake rides the same budget.
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The allowance added to a *declared* per-call budget so the caller's wait covers
    /// an on-demand worker start (ADR 0035) rather than charging that startup to the call. A
    /// tool declaration's timeout is the handler's budget — the worker enforces it from the
    /// moment the handler begins — so the kernel needs a bound that cannot fire first.
    ///
    /// It applies only when a caller supplied its own budget. Every other extension-point call
    /// (discovery, skills, triggers, hooks) has no declared handler budget to protect, and its
    /// existing ceiling already includes a cold spawn by design; adding the allowance there
    /// would silently double how long a wedged call blocks.</summary>
    private static readonly TimeSpan WorkerStartAllowance = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait the kernel applies to one extension-point call.</summary>
    private static TimeSpan CallWaitBudget(TimeSpan? timeout)
    {
        // No caller budget: the default already covers a cold start, so it is the ceiling.
        if (timeout is not { } handlerBudget) return InvokeTimeout;
        return handlerBudget + WorkerStartAllowance;
    }

    /// <summary>The best-effort budget for delivering one cancel instruction. It must never
    /// extend the caller's own deadline or host shutdown, so it is deliberately short.</summary>
    private static readonly TimeSpan CallCancellationBudget = TimeSpan.FromSeconds(2);

    /// <summary>Idle grace before an unreferenced worker is reclaimed
    /// (ADR 0035); MAIEUTICS_PLUGIN_IDLE_GRACE_MS overrides (milliseconds).</summary>
    private static int ReadIdleGraceMs()
    {
        var raw = Environment.GetEnvironmentVariable("MAIEUTICS_PLUGIN_IDLE_GRACE_MS");
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 30_000;
    }

    /// <summary>Default approvals-file location: beside the plugin data root (the data
    /// root's parent), so a test that relocates only the data root stays isolated.</summary>
    private static string DeriveApprovalsPath(string dataRoot)
    {
        var trimmed = dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        return Path.Combine(string.IsNullOrEmpty(parent) ? trimmed : parent, "plugin-approvals.json");
    }

    /// <summary>The synthetic export name given to manifest-declared extension entries.
    /// They have no worker export; discovery reads the kernel-parsed manifest snapshot
    /// directly instead of performing a host invoke.</summary>
    internal const string ManifestExportName = "maieutics.json";

    // Capability calls wrap kernel tool invokes, which legitimately run longer than
    // extension-point hooks; the budget bounds one call, it is not a stage reset.
    // Note the effective ceiling for hook-initiated calls is InvokeTimeout (15 s):
    // the outer hook await gives up first, after which the capability result no
    // longer has an observer and is simply dropped.
    private static readonly TimeSpan CapabilityCallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Debounce window a watched plugin-file change waits before its reload starts.
    /// Internal so tests advancing a fake time provider step past exactly this window.</summary>
    internal static readonly TimeSpan PluginReloadDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a <c>host.repl.derive</c> instruction waits for the host's spawned /
    /// deriveFailed report before the derive is treated as failed (bounded by the session
    /// factory's startup timeout, which is the overall start budget).</summary>
    private static readonly TimeSpan ReplDeriveTimeout = TimeSpan.FromSeconds(15);

    private readonly DenoReplOptions _denoOptions = denoOptions ?? throw new ArgumentNullException(nameof(denoOptions));
    private readonly List<PluginDescriptor> _descriptors = [];
    private PluginHttpGatewayPayload? _httpGateway;
    private readonly Lock _gate = new();
    private readonly Lock _lifecycleGate = new();

    // Restartable lifecycle state: a plugin-set change tears the host process down and starts a
    // fresh one, so these are replaced per generation instead of being readonly-once.
    private CancellationTokenSource _lifetime = new();

    private TaskCompletionSource _readiness =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The in-flight plugin-set restart, coalescing watcher events that arrive while
    /// one restart is already running. Null when the manager is in its normal running state.</summary>
    private Task? _restart;

    /// <summary>Set only by an EXTERNAL stop (DisposeAsync / StopAsync), never by the restart
    /// machinery's own teardown. A restart's lifecycle reset checks this under the same lock:
    /// when an external stop raced the restart, the reset stays stopped instead of
    /// resurrecting the manager with a fresh generation. (The restart's own
    /// EnsureStoppedAsync call must not set this — that would make every restart abort.)</summary>
    private bool _externallyStopped;

    /// <summary>Internal test observability: completed by the detach finally of the live host
    /// connection, in the same locked section that clears the attach slot. Replaced with a fresh
    /// source when a connection is accepted, so the property always reflects the current
    /// connection (see <see cref="HostConnectionReleased"/>).</summary>
    private TaskCompletionSource _hostConnectionReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Internal test observability: completed when a host connection is accepted, in
    /// the same locked section that installs the socket. Swapped for a fresh source at the
    /// accept and again when the live connection detaches, so the property always reflects the
    /// next accept (see <see cref="HostConnectionAttached"/>).</summary>
    private TaskCompletionSource _hostConnectionAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ILogger<PluginHostManager> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ILoggerFactory _loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    private readonly PluginHostModule _modules = modules ?? throw new ArgumentNullException(nameof(modules));

    /// <summary>Completions for in-flight <c>host.invoke</c> extension point calls, keyed by
    /// correlation id. A <c>host.invokeResult</c> / <c>host.invokeError</c> response for the same
    /// correlation id completes the matching call (the retired <c>extension.invoke</c> protocol
    /// used the same pending/complete shape; only the message family changed).</summary>
    /// <summary>Resolves whether one loaded plugin declared content observation in its
    /// inspections section (ADR 0032): a post-invoke hook on a plugin without the declaration
    /// fires without the result payload. Unknown plugin ids report false.</summary>
    internal bool DeliversContent(string pluginId)
    {
        lock (_gate)
        {
            return _descriptors.Any(descriptor =>
                descriptor.Id == pluginId && descriptor.InspectionsContentReadAll &&
                !IsApprovalBlockedLock(descriptor.Id));
        }
    }

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExtensionCallOutcome>> _pendingInvokes =
        new(StringComparer.Ordinal);

    /// <summary>Completions for in-flight <c>host.repl.derive</c> instructions, keyed by
    /// <c>sessionId\0generation</c>. A <c>host.repl.spawned</c> report for the same session and
    /// generation completes the <see cref="ReplDeriveOutcome.Spawned"/> branch (the host has
    /// registered the pid + broker policy and may have exited already); a
    /// <c>host.repl.deriveFailed</c> report completes it with the failure so the session factory
    /// can surface or downgrade. The host reports no correlation id (B5a), so matching is by
    /// session and generation — a report for a session/generation no derivation is waiting on is
    /// ignored (a spontaneous host-derived REPL still registers its pid through
    /// <see cref="RegisterHostRepl"/>).</summary>
    private const int OutboundCapacity = 512;

    private Channel<byte[]>? _outbound;
    private Task? _outboundWriter;
    private readonly List<Task> _capabilityCalls = [];

    private readonly Dictionary<string, IReadOnlyList<string>> _capabilityGrants =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, IReadOnlyList<PluginExtensionEntry>> _descriptorExtensions =
        new(StringComparer.Ordinal);

    /// <summary>Per-(kind, plugin) recorded declaration interpretations (the kind
    /// contract's <c>ParseDeclared</c> products, re-derived from the loaded descriptor):
    /// a non-empty result is the kind's declarative registration face, refreshed with
    /// the descriptor at start and reload. Pure kernel-side data — the host process
    /// never sees it.</summary>
    private readonly Dictionary<(string KindName, string PluginId), ContributionDeclarationResult> _recordedDeclarations =
        new(DeclarationRecordKeyComparer.Ordinal);

    /// <summary>Dependency-topological plugin id order from the last discovery
    /// (ADR 0034): the chain order for MCP adjusters.</summary>
    private IReadOnlyList<string> _pluginOrder = [];

    // —— Plugin contribution coordination (plugin-contribution framework A 期) ——

    /// <summary>The shared event differential engine (plugin-contribution framework
    /// §4.1): per-kind delivery endpoints, the per-plugin single-flight drain, the
    /// kernel retry set, and the reload-epoch forced set. Created at start; per-host-
    /// generation state clears through <see cref="Contributions.ContributionCoordinator.Reset"/>.</summary>
    private ContributionCoordinator? _contributions;

    /// <summary>The MCP kind's RegistryWide delivery: it filters the frame snapshot to
    /// McpDiscover and republishes through <see cref="_dynamicMcpCoordinator"/>.</summary>
    private McpContributionDelivery? _mcpDelivery;

    /// <summary>The skills kind's PerPlugin delivery: the former reconcile pass, owning
    /// the contribution slot table that replaced the four skill dictionaries.</summary>
    private SkillsContributionDelivery? _skillsDelivery;

    /// <summary>The model-callable plugin tool surface: describes <c>Tool</c> exports and
    /// republishes immutable declarations for run-profile acquisition. Created per host
    /// generation (describe passes die with the host they address).</summary>
    private PluginToolCoordinator? _toolCoordinator;

    // —— Plugin declaration approval (ADR 0037) ——

    /// <summary>The persisted approval registry, loaded once from
    /// <c>pluginApprovalsPath</c> (derived from the data root when null) on first use.</summary>
    private readonly string _approvalsPath =
        pluginApprovalsPath ?? DeriveApprovalsPath(pluginDataRoot);

    private PluginApprovalStore _approvalStore = PluginApprovalStore.Empty();
    private volatile bool _approvalStoreLoaded;

    /// <summary>Single-flight for approval reconciles (approve, revoke, and the approval
    /// branch of watcher reloads): each pass spans state mutation, frame construction,
    /// and frame delivery, so two passes can never interleave their sends and leave the
    /// host's worker set contradicting the kernel's approval state. Approval states are
    /// mutated only while this is held.</summary>
    private readonly SemaphoreSlim _reconcile = new(1, 1);

    /// <summary>Current declaration fingerprint per plugin id, recomputed with the
    /// descriptor set at start and reload.</summary>
    private readonly Dictionary<string, string> _declarationFingerprints =
        new(StringComparer.Ordinal);

    /// <summary>Current approval classification per plugin id. Pending and blocked entries
    /// are excluded from the host config, declaration recording, and extension invokes.</summary>
    private readonly Dictionary<string, PluginApprovalState> _approvalStates =
        new(StringComparer.Ordinal);

    /// <summary>Internal test observability: completes when approval classifications were
    /// recomputed (start, watched reload, approve, revoke). A completed source is handed
    /// back once and a fresh one installed in the same gate section, so a reader that races
    /// the transition still observes that edge.</summary>
    private TaskCompletionSource _approvalsChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes on the next approval-classification recomputation. Capture it
    /// before the triggering action and await it under the caller's deadline.</summary>
    internal Task ApprovalsChanged
    {
        get
        {
            lock (_gate)
            {
                var changed = _approvalsChanged.Task;
                if (changed.IsCompleted)
                    _approvalsChanged = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                return changed;
            }
        }
    }

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ReplDeriveOutcome>> _pendingDerives =
        new(StringComparer.Ordinal);

    private readonly List<PluginRegistration> _registrations = [];
    private readonly List<PluginRegistration> _hostRegistrations = [];

    /// <summary>Pids of the REPL processes the attached plugin host derived, keyed by pid to the
    /// session id the host reported. Used to release the pid-scoped registrations (session identity
    /// + broker policy) when the host disconnects without reporting <c>host.repl.exited</c> (a
    /// crashed or killed host never drains its children's registrations; ADR 0020 follow-up).</summary>
    private readonly ConcurrentDictionary<int, string> _hostReplSessions = new();

    private FileSystemWatcher? _pluginWatcher;
    private CancellationTokenSource? _watcherDebounce;

    /// <summary>Tracked debounce/reload tasks spawned by the plugin watcher, pruned the same way
    /// as <see cref="_capabilityCalls"/> so stop can observe them instead of leaving unobserved
    /// task exceptions behind.</summary>
    private readonly List<Task> _watcherReloads = [];

    /// <summary>Internal test observability: completes when a debounced watcher reload has been
    /// applied — the same transition that finishes the reload task — so tests can await the
    /// application deterministically instead of sleeping past the debounce. A completed task is
    /// replaced on the next read, so the property is safe to await repeatedly (one await observes
    /// one applied reload). Capture it before the watched change and await it after. Await it
    /// under the caller's deadline.</summary>
    private TaskCompletionSource _reloadApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task ReloadApplied
    {
        get
        {
            lock (_gate)
            {
                if (_reloadApplied.Task.IsCompleted)
                    _reloadApplied = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                return _reloadApplied.Task;
            }
        }
    }

    /// <summary>The per-event debounced wait, completed (under the gate) only after its
    /// timer is registered with the injected time provider; the completion and the
    /// property's per-read swap share the same gate, so a reader either observes the
    /// completed task or hands the completer the fresh source. Internal test observability
    /// for virtual time: advancing a fake clock past <see cref="PluginReloadDebounce"/> once
    /// this completes is guaranteed to fire the wait, so tests never sleep out the debounce
    /// in real time.</summary>
    private TaskCompletionSource _watcherDebounceRegistered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Internal test observability: returns a task that completes when a watcher
    /// event's debounced wait has been registered with the time provider (see the field
    /// above). An already-completed registration is handed back once and a fresh source is
    /// installed in the same locked section, so a read that races the watcher chain never
    /// drops the observation: the caller sees the completed task and can advance the clock
    /// instead of waiting for a second registration that a single change never produces.</summary>
    internal Task WatcherDebounceRegistered
    {
        get
        {
            lock (_gate)
            {
                Task registered = _watcherDebounceRegistered.Task;
                if (registered.IsCompleted)
                    _watcherDebounceRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return registered;
            }
        }
    }

    /// <summary>Internal test observability: completes when the live host connection's receive
    /// loop releases the attach slot — the same locked transition that clears the slot, so an
    /// awaited task guarantees a subsequent attach is accepted. A fresh source is installed when
    /// a connection is accepted, so the property always reflects the current connection: capture
    /// the task before detaching and await it after, instead of racing re-attach attempts against
    /// the asynchronous unwind (until the old loop's finally runs, refusals complete instantly and
    /// an attempt-count retry loop spins without ever waiting). Await it under the caller's
    /// deadline.</summary>
    internal Task HostConnectionReleased
    {
        get
        {
            lock (_gate) return _hostConnectionReleased.Task;
        }
    }

    /// <summary>Internal test observability: completes when a live host connection is accepted —
    /// the same locked transition that installs the socket — so an awaited task proves the
    /// attach (and with it the connected status) without polling <c>GetStatus()</c>. The
    /// completed source is replaced in the same lock section, and a fresh source is installed
    /// again when the current connection detaches, so the property always reflects the next
    /// accept: capture the task before triggering the attach and await it under the caller's
    /// deadline.</summary>
    internal Task HostConnectionAttached
    {
        get
        {
            lock (_gate) return _hostConnectionAttached.Task;
        }
    }

    /// <summary>
    ///     Publishes the latest registry snapshot produced by the plugin host so tests can wait for a
    ///     registration without polling. Completed when the manager is disposed. Bounded and
    ///     drop-oldest so an unconsumed production stream retains only the newest snapshot.
    /// </summary>
    internal Channel<PluginRegistration[]> RegistryChanges = Channel.CreateBounded<PluginRegistration[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Internal test observability: completed when a host restart replaced
    /// <see cref="RegistryChanges"/> with the fresh generation's channel. Completion and
    /// <see cref="RegistryChangesReplaced"/>'s per-read swap share the gate, so a reader
    /// either observes the completed task or hands the completer the fresh source; a
    /// completed task is replaced on the next read. A waiter parked on the superseded
    /// channel's frames must re-capture on this signal — the replaced channel never
    /// produces another frame.</summary>
    private TaskCompletionSource _registryChangesReplaced = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Internal test observability: completes when the registry channel was replaced
    /// by a host restart (see the field above). A completed replacement is handed back once
    /// and a fresh source installed in the same locked section, because a waiter can capture
    /// the superseded channel just before the swap and read this signal just after the
    /// completion: dropping that edge would park it on a channel that never produces frames
    /// again.</summary>
    internal Task RegistryChangesReplaced
    {
        get
        {
            lock (_gate)
            {
                Task replaced = _registryChangesReplaced.Task;
                if (replaced.IsCompleted)
                    _registryChangesReplaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return replaced;
            }
        }
    }

    private readonly ReplControlSessionRegistry _sessionRegistry =
        sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));

    private readonly DenoPermissionBroker? _broker = broker;

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private string? _configPath;

    /// <summary>
    ///     Effective REPL policies keyed by session id, registered by the kernel before the host
    ///     derives a REPL for that session (ADR 0020 decision 1). The permission broker resolves
    ///     every Deno permission check the REPL child makes against the policy registered for its
    ///     pid; the host is only the enforcement point, never the authority. The kernel pre-caches
    ///     the policy at session start (<see cref="DenoReplPolicyCache.PrepareAsync"/>), so a
    ///     spawned report registers the real policy; a session with no cached policy degrades to
    ///     <see cref="EffectivePolicy.Default"/> explicitly with a warning — the fallback is a
    ///     deliberate choice, never a silent one.
    /// </summary>
    private readonly ConcurrentDictionary<string, EffectivePolicy> _replPolicies = new(StringComparer.Ordinal);

    /// <summary>The launch policy of each stdio MCP server child, keyed by server id: the
    /// declaring plugin's process-launch scope, captured when its servers are discovered so
    /// the generation factory renders it before the child is spawned (AGENTS.md invariant 19).
    /// Discovery always precedes generation creation for a definition, so the entry exists by
    /// launch time; a definition without one launches unrestricted, the same default the run
    /// gate applies everywhere.</summary>
    private readonly ConcurrentDictionary<string, EffectivePolicy> _mcpServerPolicies = new(StringComparer.Ordinal);

    private PluginMcpCoordinator? _dynamicMcpCoordinator;
    private McpAdjustmentChain? _adjustmentChain;
    private PluginHostProcess? _process;
    private Task _processExitObservation = Task.CompletedTask;
    private IReadOnlySet<string> _reservedToolNames = new HashSet<string>(StringComparer.Ordinal);
    private Task? _starting;
    private Task? _stopping;
    private WebSocket? Socket { get; set; }

    private string HostId { get; } = $"host-{Guid.NewGuid():N}"[..12];

    public async ValueTask DisposeAsync()
    {
        Task stop;
        Task? inFlightRestart;
        Task? inFlightStartup;
        lock (_lifecycleGate)
        {
            _externallyStopped = true;
            // A restart clears `restart` in the same locked transition that swaps in the
            // successor generation's startup, so past that point the in-flight work is the
            // startup task itself; capture both under one gate read.
            inFlightRestart = _restart;
            inFlightStartup = _starting;
            stop = EnsureStoppedAsync();
        }

        await stop.ConfigureAwait(false);
        // A restart in flight is part of this stop: observing it here keeps the stop from
        // returning while lifecycle state could still be reset or a fresh host generation
        // started behind the caller's back.
        await ObserveInFlightRestartAsync(inFlightRestart, inFlightStartup).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_stopping is not null, this);
            return _starting ??= StartCoreAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task stop;
        Task? inFlightRestart;
        Task? inFlightStartup;
        lock (_lifecycleGate)
        {
            _externallyStopped = true;
            // Same capture as DisposeAsync: the restart's swap moves the in-flight work from
            // `restart` to the successor generation's `starting`, so both are read together.
            inFlightRestart = _restart;
            inFlightStartup = _starting;
            stop = EnsureStoppedAsync();
        }

        // Stop must run to completion even if the caller's token cancels: an
        // aborted stop would leave the host process and watcher behind, and
        // Host.StopAsync treats a thrown TaskCanceledException as a fatal
        // hosted-service failure. The cancellation only bounds the wait here;
        // EnsureStoppedAsync itself is cancellation-free.
        try
        {
            await stop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stop.ConfigureAwait(false);
        }

        await ObserveInFlightRestartAsync(inFlightRestart, inFlightStartup).ConfigureAwait(false);
    }

    internal Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        return _readiness.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Gets or sets the kernel capability executor. The composition root binds
    /// this to the control host's script tool registry; capability requests arriving
    /// before it is bound are refused with a typed <c>capability_unavailable</c> error.</summary>
    internal PluginCapabilityExecutor? CapabilityExecutor { get; set; }

    /// <summary>
    ///     Composition-root sink for plugin UI frames (ADR 0038 stage 3): the comm plane
    ///     lives in <c>Frontend</c>, which this namespace must not reference, so the
    ///     frame crosses the boundary through this delegate. Typed producer failures are
    ///     raised as <see cref="InvalidOperationException" /> and answered to the worker
    ///     as <c>ui_frame_rejected</c>.
    /// </summary>
    internal PluginUiFrameSink? UiFrameSink { get; set; }

    /// <summary>The export name serving <c>UiEvent</c> for a plugin (ADR 0038 stage 3):
    /// the kernel routes frontend uplink for the plugin's models there via
    /// <see cref="InvokeExtensionPointAsync" />. Null when the plugin registers none.</summary>
    internal string? FindUiEventExport(string pluginId)
    {
        lock (_gate)
        {
            return _hostRegistrations
                .FirstOrDefault(registration =>
                    registration.PluginId == pluginId &&
                    registration.ExtensionPoint == ReplExtensionPointName.UiEvent)
                ?.ExportName;
        }
    }

    /// <summary>The interpreted `ui` data-entry form of one plugin (ADR 0038 stage 3):
    /// the template a consumer (the `GET /v1/plugins` surface, stage 4) can publish on
    /// demand. Null when the plugin declares no ui entry. Approval does not gate the
    /// template itself — publishing does, through the same sink the live lane uses.</summary>
    /// <summary>One plugin's frontend-facing entry for the plugins surface (ADR 0038
    /// stage 4): identity, approval classification, the declarative form template, and
    /// the gateway page mount when the plugin serves HTTP UI.</summary>
    internal sealed record PluginSurfaceInfo(
        string Id,
        string Name,
        PluginApprovalState ApprovalState,
        PluginUiFormDefinition? Form,
        string? PageUrl);

    /// <summary>Snapshots every discovered plugin's surface entry. Gateway page URLs
    /// are composed only for approved plugins with a live mount; blocked plugins
    /// appear (with their state) but carry no usable links.</summary>
    internal IReadOnlyList<PluginSurfaceInfo> GetPluginSurfaces(string? gatewayHostname, int gatewayPort, string? gatewayToken)
    {
        PluginDescriptor[] snapshot;
        PluginApprovalState[] surfaceStates;
        lock (_gate)
        {
            snapshot = [.. _descriptors];
            surfaceStates = snapshot.Select(descriptor =>
                _approvalStates.GetValueOrDefault(descriptor.Id, PluginApprovalState.PendingApproval)).ToArray();
        }

        var surfaces = new List<PluginSurfaceInfo>(snapshot.Length);
        for (var index = 0; index < snapshot.Length; index++)
        {
            var descriptor = snapshot[index];
            var state = surfaceStates[index];
            string? pageUrl = null;
            if (state == PluginApprovalState.Approved &&
                gatewayHostname is not null && gatewayToken is not null &&
                descriptor.UiForm?.Error is null)
            {
                pageUrl = $"http://{gatewayHostname}:{gatewayPort}/{gatewayToken}/plugins/{descriptor.Id}/";
            }

            surfaces.Add(new PluginSurfaceInfo(
                descriptor.Id,
                descriptor.Name,
                state,
                descriptor.UiForm,
                pageUrl));
        }

        return surfaces;
    }

    /// <summary>The live gateway descriptor (address, entrance token, mounts), or null
    /// when the connected host reported none. Token handling is the caller's duty.</summary>
    internal PluginHttpGatewayPayload? GetHttpGateway()
    {
        lock (_gate)
        {
            return _httpGateway;
        }
    }

    internal PluginUiFormDefinition? GetUiFormTemplate(string pluginId)
    {
        lock (_gate)
        {
            return _descriptors.FirstOrDefault(candidate => candidate.Id == pluginId)?.UiForm;
        }
    }

    /// <summary>Publishes a plugin's declarative `ui` form into the foreground session's
    /// comm plane (ADR 0038 stage 3, data form): the same frame path the live lane uses,
    /// with the plugin as uplink owner. Submit actions declared as `submitTool` are the
    /// consumer's concern — the kernel capability gate covers any tool invocation the
    /// plugin attempts with the submitted values. Throws typed producer failures.</summary>
    internal async ValueTask PublishUiFormAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        var template = GetUiFormTemplate(pluginId);
        if (template is null)
            throw new InvalidOperationException($"Plugin '{pluginId}' declares no ui data entry.");
        if (template.Error is { } error)
            throw new InvalidOperationException($"Plugin '{pluginId}' ui entry is unusable: {error}");
        if (UiFrameSink is null)
            throw new InvalidOperationException("The kernel UI frame sink is not available.");

        var commId = Guid.NewGuid().ToString("N");
        var state = new Dictionary<string, object?>
        {
            ["fields"] = template.Fields,
            ["values"] = new Dictionary<string, object?>(),
        };
        if (template.Title is not null) state["title"] = template.Title;
        if (template.SubmitLabel is not null) state["submitLabel"] = template.SubmitLabel;
        if (template.CancelLabel is not null) state["cancelLabel"] = template.CancelLabel;

        var data = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["state"] = state },
            ReplControlJsonContext.Default.DictionaryStringJsonElement);
        await UiFrameSink(
            pluginId,
            new PluginUiFramePayload("open", commId, "maieutics.view/maieutics/form", data),
            cancellationToken).ConfigureAwait(false);
    }

    internal PluginHostStatus GetStatus()
    {
        Task? startup;
        Task? shutdown;
        PluginHostProcess? hostProcess;
        lock (_lifecycleGate)
        {
            startup = _starting;
            shutdown = _stopping;
            hostProcess = _process;
        }

        int pluginCount;
        int registrationCount;
        bool controlConnected;
        int pendingApprovals;
        lock (_gate)
        {
            pluginCount = _descriptors.Count;
            registrationCount = _registrations.Count;
            controlConnected = Socket?.State == WebSocketState.Open;
            pendingApprovals = _approvalStates.Values.Count(static state =>
                state is PluginApprovalState.PendingApproval or PluginApprovalState.BlockedByDependency);
        }

        var hostProcessRequired = hostProcess is not null || pluginCount > 0;
        var state = startup switch
        {
            _ when _readiness.Task.IsFaulted => PluginHostState.Failed,
            { IsFaulted: true } => PluginHostState.Failed,
            { IsCanceled: true } => PluginHostState.Canceled,
            _ when shutdown is { IsCompleted: true } => PluginHostState.Stopped,
            _ when shutdown is not null => PluginHostState.Stopping,
            null => PluginHostState.NotStarted,
            _ when !_readiness.Task.IsCompletedSuccessfully => PluginHostState.Starting,
            _ when hostProcess?.Completion.IsCompleted == true => PluginHostState.Exited,
            _ => PluginHostState.Ready
        };
        return new PluginHostStatus(
            state,
            pluginCount,
            registrationCount,
            hostProcessRequired,
            controlConnected,
            pendingApprovals);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The approval registry is a tiny local read; loading it before discovery keeps
            // every later gate decision off the filesystem.
            EnsureApprovalStoreLoaded();

            // Discovery waits on child processes and touches no gated state, so it runs
            // before the lifecycle gate. Holding that gate across it blocked GetStatus for
            // the whole scan (the gate is also what serializes startup against dispose).
            var plan = await DiscoverPluginsAsync(cancellationToken).ConfigureAwait(false);

            // The host generation's live-child slot (the test process gate): acquired
            // asynchronously out here — never inside the lifecycle lock, which must not
            // be held across an await — so parallel managers cannot oversubscribe a
            // shared runner with process startups. The slot leaves with the spawned
            // child (wired to its completion in DenoRunProcess); the catch below
            // unwinds it when the lifecycle check or the spawn itself fails first.
            var hostSlot = await InternalDenoProcessGate.AcquireAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                lock (_lifecycleGate)
                {
                    ObjectDisposedException.ThrowIf(_stopping is not null, this);
                    Start(plan, hostSlot);
                    _readiness.TrySetResult();
                }
            }
            catch
            {
                hostSlot.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _readiness.TrySetCanceled(cancellationToken);
            await CleanupFailedStartupAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            _readiness.TrySetException(exception);
            await CleanupFailedStartupAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CleanupFailedStartupAsync()
    {
        try
        {
            await EnsureStoppedAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Plugin host cleanup failed after startup did not complete.");
        }
    }

    public void SetReservedToolNames(IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (_gate)
        {
            _reservedToolNames = names.ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     Caches the effective REPL policy the kernel computed for a session, keyed by session
    ///     id. A host-derived REPL report (<c>host.repl.spawned</c>) then registers this policy
    ///     with the permission broker for the REPL's pid, preserving the kernel as the permission
    ///     authority (ADR 0020 decision 1). The kernel pre-caches the policy at session start via
    ///     <see cref="DenoReplPolicyCache"/>, which calls this method through
    ///     <see cref="IReplPolicyRegistrar"/>.
    /// </summary>
    internal void RegisterReplPolicy(string sessionId, EffectivePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(policy);
        _replPolicies[sessionId] = policy;
        _logger.LogDebug("Cached the effective REPL policy for session '{SessionId}'.", sessionId);
    }

    /// <summary>Removes a session's cached REPL policy, so a closed or restarted session's old
    /// policy cannot leak into the next generation (the next session start re-caches).</summary>
    internal void UnregisterReplPolicy(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_replPolicies.TryRemove(sessionId, out _))
            _logger.LogDebug("Removed the cached REPL policy for session '{SessionId}'.", sessionId);
    }

    void IReplPolicyRegistrar.RegisterReplPolicy(string sessionId, EffectivePolicy policy)
    {
        RegisterReplPolicy(sessionId, policy);
    }

    void IReplPolicyRegistrar.UnregisterReplPolicy(string sessionId)
    {
        UnregisterReplPolicy(sessionId);
    }

    public IReadOnlyList<PluginRegistration> GetRegistrations(string extensionPoint)
    {
        lock (_gate)
        {
            return _registrations
                .Where(registration => registration.ExtensionPoint == extensionPoint)
                .ToArray();
        }
    }

    /// <summary>The plugin set discovery resolved, ready to publish. Discovery shells out to
    /// the Deno toolchain and the npm/jsr registries, so it is deliberately separate from
    /// <see cref="Start"/>: it runs outside both the lifecycle gate and the state gate, and
    /// only its result is published under them.</summary>
    private readonly record struct PluginStartupPlan(
        IReadOnlyList<PluginDescriptor> Enabled,
        PluginImportMergeResult ImportMerge);

    /// <summary>Resolves the plugin set from the plugins root. Waits on child processes for up
    /// to a minute per registry entry, and touches none of the gated state, so callers must run
    /// it before taking <c>lifecycleGate</c> or <c>gate</c>: holding either across the scan
    /// stalls every control-plane reader — status, registrations, and each control-bus frame —
    /// and spends a thread-pool thread on blocking waits inside async startup.</summary>
    private async Task<PluginStartupPlan> DiscoverPluginsAsync(CancellationToken cancellationToken)
    {
        EnsurePluginsRoot();
        var scanned = await ScanPluginsAsync(cancellationToken).ConfigureAwait(false);
        var graph = PluginDependencyGraph.Validate(scanned);
        foreach (var exclusion in graph.Exclusions)
        {
            _logger.LogWarning(
                "Plugin '{PluginId}' is excluded: {Reason} — {Detail}.",
                exclusion.PluginId,
                exclusion.Reason,
                exclusion.Detail);
        }

        var importMerge = PluginImportMerger.Merge(graph.Enabled, PluginHostModule.ReservedImportKeys);
        foreach (var warning in importMerge.Warnings)
        {
            _logger.LogWarning("Plugin import merge: {Warning}.", warning);
        }

        foreach (var exclusion in importMerge.Exclusions)
        {
            _logger.LogWarning(
                "Plugin '{PluginId}' is excluded: {Reason} — {Detail}.",
                exclusion.PluginId,
                exclusion.Reason,
                exclusion.Detail);
        }

        var enabled = graph.Enabled
            .Where(plugin => !importMerge.ExcludedPluginIds.Contains(plugin.Id))
            .ToArray();
        // The enabled list preserves the dependency-topological wave order, which is
        // the responsibility-chain order for MCP adjusters (ADR 0034).
        _pluginOrder = enabled.Select(static plugin => plugin.Id).ToArray();
        return new PluginStartupPlan(enabled, importMerge);
    }

    private void Start(PluginStartupPlan plan, ChildSlotLease hostSlot)
    {
        // The contribution engine exists before any generation state is published, so
        // the generation reset and the start seed below both reach it.
        EnsureContributionEngine();
        EnsureToolCoordinator();

        // Publication stays under one lock: a concurrent GetStatus/PushAsync reader must
        // never observe a half-replaced registry.
        lock (_gate)
        {
            _descriptors.Clear();
            _descriptors.AddRange(plan.Enabled);
            RecomputeApprovalStatesLock(_descriptors);
            _capabilityGrants.Clear();
            _descriptorExtensions.Clear();
            _recordedDeclarations.Clear();
            foreach (var descriptor in _descriptors)
            {
                // Approval gate (ADR 0037): an unapproved plugin stays discovered but
                // completely inert — no declarations recorded, no grants, no workers.
                if (IsApprovalBlockedLock(descriptor.Id))
                {
                    _logger.LogWarning(
                        "Plugin '{PluginId}' declarations are not approved; its workers, MCP servers, triggers, and capabilities stay inactive until approved (%plugin approve {CommandTarget}).",
                        descriptor.Id,
                        descriptor.Id);
                    continue;
                }

                RecordDescriptorDeclarationsLock(descriptor);
                _capabilityGrants[descriptor.Id] = descriptor.Capabilities;
            }

            // Manifest-declared entries seed the registry before any host payload:
            // a declarative-only plugin contributes without ever spawning a worker.
            _hostRegistrations.Clear();
            _registrations.Clear();
            _registrations.AddRange(DeclarativeRegistrationsLock());
            // The reload-epoch bookkeeping belongs to one host generation: a fresh
            // process restarts its epochs at zero, and its start seed reconciles
            // everything anyway, so no deferred force may survive the swap.
            _contributions?.Reset();
        }

        // Outside the gate: Reset cancels any describe pass still awaiting the previous
        // host, and cancellation callbacks must not run while the manager gate is held.
        // Tool descriptors come from the workers this generation spawns, so a previous
        // generation's declarations cannot be served to a run.
        _toolCoordinator?.Reset();

        // The root deno.json must exist before the host process starts: it carries the
        // process import map (host machinery plus the merged plugin entries) that the
        // host and every plugin worker resolve against.
        _modules.WriteRootConfig(plan.ImportMerge.Imports);
        _configPath = WriteConfigFile(_descriptors);
        _process = PluginHostProcess.Start(
            new PluginHostProcessOptions(
                _denoOptions.Executable,
                _modules.HostUrl,
                socketPath,
                _configPath,
                HostId,
                _modules.SdkUrl,
                _modules.WorkerEntryUrl,
                _modules.ConfigFile,
                _broker),
            _logger,
            hostSlot);
        _sessionRegistry.RegisterPluginHost(_process.ProcessId, HostId);
        _processExitObservation = ObserveExitAsync(_process, _configPath);
        StartDynamicMcpCoordinator();
        StartPluginWatcher();
        // The MCP coordinator missed Start()'s seed (it starts later), so publish the
        // initial declarative snapshot now; the host's own registry payload will
        // union with it when it arrives. Progressive per-plugin seeding rides the same
        // frame: each plugin's declared roots reconcile and commit independently, so
        // one slow pass no longer delays every other plugin's contribution. The MCP
        // delivery filters its McpDiscover subset from the same snapshot; approval-
        // blocked plugins seed nothing.
        PluginRegistration[] initialSnapshot;
        string[] seedPlugins;
        lock (_gate)
        {
            initialSnapshot = _registrations.ToArray();
            seedPlugins = _descriptors
                .Where(descriptor => !IsApprovalBlockedLock(descriptor.Id) &&
                                     descriptor.Extensions.Any(static entry => SkillsContributionKind.Instance.OwnsExtensionKind(entry.Kind)))
                .Select(static descriptor => descriptor.Id)
                .ToArray();
        }

        _contributions?.Seed(new ContributionFrameInput(
            initialSnapshot,
            ContributionFrameInput.NoForcedPlugins,
            seedPlugins));
    }

    /// <summary>Creates the plugins root on first start as an empty deno project skeleton:
    /// a <c>deno.json</c> (package identity + SDK import mapping) and a <c>maieutics.json</c>
    /// with no entrypoints. The directory is idempotent — an existing user project is never
    /// overwritten.</summary>
    private void EnsurePluginsRoot()
    {
        if (Directory.Exists(pluginsRoot)) return;

        Directory.CreateDirectory(pluginsRoot);
        var denoJson = Path.Combine(pluginsRoot, "deno.json");
        if (!File.Exists(denoJson))
            File.WriteAllText(
                denoJson,
                """
                {
                  "name": "@maieutics/plugins",
                  "version": "0.1.0",
                  "imports": {
                    "@maieutics/plugin-sdk": "jsr:@maieutics/plugin-sdk@^0.1"
                  }
                }
                """);
        var manifestPath = Path.Combine(pluginsRoot, "maieutics.json");
        if (!File.Exists(manifestPath))
            File.WriteAllText(
                manifestPath,
                """
                {
                  "isolation": "auto",
                  "entrypoints": {}
                }
                """);
        _logger.LogInformation(
            "Created the plugin directory '{PluginsRoot}' with an empty deno project skeleton.",
            pluginsRoot);
    }

    private void StartDynamicMcpCoordinator()
    {
        _adjustmentChain = new McpAdjustmentChain(
            (pluginId, exportName, extensionPoint, request, cancellationToken) =>
                InvokeExtensionPointAsync(
                    pluginId,
                    exportName,
                    extensionPoint,
                    request,
                    cancellationToken),
            _logger);
        UpdateAdjustmentSnapshot();
        var coordinator = new PluginMcpCoordinator(
            DiscoverDynamicMcpAsync,
            CreateDynamicMcpGenerationAsync,
            _logger,
            _adjustmentChain);
        coordinator.Start();
        _dynamicMcpCoordinator = coordinator;
    }

    /// <summary>Recomputes the MCP adjustment topology (ADR 0034): dependency-
    /// topological plugin order, per-plugin declared dependencies, and the live
    /// McpAdjust registrations. Called on startup, on every host registry payload,
    /// and after declarative reloads.</summary>
    private void UpdateAdjustmentSnapshot()
    {
        var chain = _adjustmentChain;
        if (chain is null) return;

        McpAdjustmentSnapshot snapshot;
        lock (_gate)
        {
            var dependencies = _descriptors.ToDictionary(
                static descriptor => descriptor.Id,
                static descriptor => descriptor.Dependencies);
            var adjusters = _registrations
                .Where(registration => registration.ExtensionPoint == ReplExtensionPointName.McpAdjust)
                .Select(registration => new McpAdjusterRegistration(registration.PluginId, registration.ExportName))
                .Distinct()
                .ToList();
            snapshot = new McpAdjustmentSnapshot(_pluginOrder, dependencies, adjusters);
        }

        chain.UpdateSnapshot(snapshot);
    }

    /// <summary>Watches the plugins root for manifest/permission/source changes and reloads the
    /// owning plugin's worker in-process with its latest config (the config file is re-resolved
    /// per reload, so a permission change applies without restarting the host process). Changes
    /// are debounced; only the most recent change triggers one reload.</summary>
    private void StartPluginWatcher()
    {
        // The plugins root always exists (EnsurePluginsRoot ran in Start), so the
        // watcher always runs; a plugin added later is picked up by a reload.
        var watcher = new FileSystemWatcher(pluginsRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                           NotifyFilters.DirectoryName | NotifyFilters.Size
        };
        watcher.Changed += OnPluginFileChanged;
        watcher.Created += OnPluginFileChanged;
        watcher.Deleted += OnPluginFileChanged;
        watcher.Renamed += OnPluginFileChanged;
        watcher.EnableRaisingEvents = true;
        _pluginWatcher = watcher;
        _logger.LogInformation("Watching '{PluginsRoot}' for plugin changes.", pluginsRoot);
    }

    private void StopPluginWatcher()
    {
        lock (_gate)
        {
            _watcherDebounce?.Cancel();
            _watcherDebounce?.Dispose();
            _watcherDebounce = null;
            _pluginWatcher?.Dispose();
            _pluginWatcher = null;
        }
    }

    private void OnPluginFileChanged(object sender, FileSystemEventArgs args)
    {
        // Debounce: a burst of file writes (e.g. deno fmt, git checkout) collapses into one
        // reload. The most recent change wins; earlier pending reloads are superseded. The
        // generation's CTS is captured with the debounce so a handler that resumes after a
        // restart replaced `lifetime` still dies with its own generation instead of linking
        // against (or observing) the new one.
        CancellationTokenSource debounce;
        CancellationTokenSource generation;
        lock (_gate)
        {
            generation = _lifetime;
            if (generation.IsCancellationRequested) return;
            try
            {
                _watcherDebounce?.Cancel();
                _watcherDebounce = debounce = CancellationTokenSource.CreateLinkedTokenSource(generation.Token);
            }
            catch (ObjectDisposedException)
            {
                // The generation's CTS was disposed between the check and the link (stop path
                // runs outside this lock); the event belongs to a dead generation.
                return;
            }
        }

        // Tracked like the capability calls so stop can observe the reloads; the body catches
        // every exception (the known transient cases and anything unexpected) so the
        // fire-and-forget task never surfaces as an unobserved task exception.
        TrackWatcherReload(Task.Run(async () =>
        {
            try
            {
                if (generation.IsCancellationRequested) return;

                // The debounce rides the injected time provider (a fake clock in tests
                // advances past it) with the same linked-CTS cancellation as before: the
                // linked debounce token cancels the wait, an earlier event supersedes it,
                // and generation shutdown cancels the linked source. The registration is
                // reported (WatcherDebounceRegistered) only after Task.Delay returned, so
                // the wait's timer provably exists in the provider by then.
                Task debounceWait = Task.Delay(PluginReloadDebounce, _timeProvider, debounce.Token);
                lock (_gate)
                {
                    _watcherDebounceRegistered.TrySetResult();
                }

                try
                {
                    await debounceWait.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // Set recompute first: a watched change that adds or removes a local plugin
                // (a directory at/under the plugins root carrying a maieutics.json) cannot be
                // applied in-process — the new module entries belong to the process import
                // map, which is fixed at host start. The restart re-runs full discovery, so
                // removals cascade through the dependency graph's exclusion rules. Pure
                // content changes leave the set unchanged and fall through to the in-process
                // worker reload below.
                if (LocalPluginSetDiffers())
                {
                    _logger.LogInformation(
                        "Watched change at '{Path}' changes the local plugin set; restarting the plugin host.",
                        args.FullPath);
                    // A set change settles the reload through the restart: the new host
                    // re-runs full discovery, so the applied signal completes here — a
                    // waiter watching this edit would otherwise park forever on a signal
                    // the restart path never completed.
                    lock (_gate)
                    {
                        _reloadApplied.TrySetResult();
                    }

                    _ = RestartForPluginSetChangeAsync();
                    return;
                }

                await ReloadChangedPluginAsync(args.FullPath).ConfigureAwait(false);
                _logger.LogDebug(
                    "Watched change at '{Path}' applied.",
                    args.FullPath);
                lock (_gate)
                {
                    _reloadApplied.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                // A deleted plugin directory, an unreadable manifest, a host socket that closed
                // mid-send, or a shutdown-during-reload must not crash the watcher; the next
                // change (or the next startup) re-resolves. Unknown failures are observed here
                // too instead of escaping as unobserved task exceptions.
                _logger.LogDebug(
                    exception,
                    "Plugin reload for '{Path}' did not complete.",
                    args.FullPath);
            }
        }));
    }

    /// <summary>Tracks one watcher-spawned reload task so shutdown can observe it; completed
    /// entries are pruned as new tasks arrive to keep the list bounded (the body handles its own
    /// failures, so a fault here is only observed, never rethrown).</summary>
    private void TrackWatcherReload(Task reload)
    {
        lock (_gate)
        {
            _watcherReloads.RemoveAll(tracked =>
            {
                if (!tracked.IsCompleted) return false;
                if (tracked.IsFaulted) _ = tracked.Exception;
                return true;
            });
            _watcherReloads.Add(reload);
        }
    }

    /// <summary>Determines whether the local plugin set (the root project plus every local
    /// file-import target at/under the plugins root) differs from the currently loaded
    /// descriptors. The scan is file-only — no Deno toolchain resolution — so every debounced
    /// watcher event can afford it; registry-installed (jsr:/npm:) plugins are not part of this
    /// check and still require a manual host-process restart, consistent with the import-map
    /// change they imply.</summary>
    private bool LocalPluginSetDiffers()
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var scannedRoots = ScanLocalPluginRoots();
        HashSet<string> current;
        lock (_gate)
        {
            current = _descriptors
                .Where(descriptor => IsWithin(descriptor.RootDirectory, pluginsRoot, comparison))
                .Select(descriptor => NormalizeRoot(descriptor.RootDirectory))
                .ToHashSet(StringComparer.FromComparison(comparison));
        }

        return !current.SetEquals(scannedRoots);
    }

    /// <summary>Import-target directories outside the plugins root, warned about once per
    /// manager lifetime. They are excluded from the plugin-set difference — the loaded
    /// descriptor set is root-contained by definition, so counting them would make the
    /// difference permanently true and escalate every watched change (pure content edits
    /// included) into a generation restart that re-detects itself.</summary>
    private readonly HashSet<string> _warnedOutOfRootImportTargets = new(StringComparer.Ordinal);

    /// <summary>Scans the local plugin set without touching the Deno toolchain: the root
    /// project plus every local file-import target whose directory carries a loadable
    /// maieutics.json. Roots are normalized full paths; targets resolving outside the
    /// plugins root are skipped with a one-time warning — the watcher cannot see them and
    /// the loaded set never contains them, so they are inert for set management.</summary>
    private HashSet<string> ScanLocalPluginRoots()
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var roots = new HashSet<string>(StringComparer.FromComparison(comparison));
        if (PluginManifest.TryLoad(pluginsRoot, manifestVariables, out _, out _))
            roots.Add(NormalizeRoot(pluginsRoot));

        foreach (var importTarget in PluginManifest.ReadLocalImportTargets(pluginsRoot))
        {
            var packageDirectory = Path.GetDirectoryName(importTarget);
            if (packageDirectory is null ||
                !Directory.Exists(packageDirectory) ||
                !PluginManifest.TryLoad(packageDirectory, manifestVariables, out _, out _))
                continue;

            var normalized = NormalizeRoot(packageDirectory);
            if (!IsWithin(normalized, pluginsRoot, comparison))
            {
                WarnOutOfRootImportTargetOnce(normalized);
                continue;
            }

            roots.Add(normalized);
        }

        return roots;
    }

    private void WarnOutOfRootImportTargetOnce(string normalizedDirectory)
    {
        lock (_gate)
        {
            if (!_warnedOutOfRootImportTargets.Add(normalizedDirectory)) return;
        }

        _logger.LogWarning(
            "The local import target '{Directory}' resolves outside the plugins root '{PluginsRoot}'; " +
            "it is excluded from the plugin set (its changes cannot trigger reloads or restarts). " +
            "Move the dependency at/under the plugins root to manage it as a plugin.",
            normalizedDirectory,
            pluginsRoot);
    }

    private static string NormalizeRoot(string directory)
    {
        return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Restarts the whole plugin host so discovery recomputes the plugin set: a fresh
    /// scan rebuilds the dependency graph (removed plugins and their dependents become typed
    /// exclusions), the process import map, and the worker set. Storage pools reopen from disk
    /// (WAL recovery — crash-equivalent for SQLite); REPL children are separate processes and
    /// their broker registrations release through the host-detach path. Concurrent requests
    /// coalesce onto the running restart; a change arriving after the new watcher is armed
    /// fires a fresh event and re-evaluates.</summary>
    private Task RestartForPluginSetChangeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_stopping is not null) return Task.CompletedTask;
            return _restart ??= RestartCoreAsync();
        }
    }

    private async Task RestartCoreAsync()
    {
        await Task.Yield();
        try
        {
            Task stop;
            lock (_lifecycleGate)
            {
                stop = EnsureStoppedAsync();
            }

            await stop.ConfigureAwait(false);
            Task startupTask;
            lock (_lifecycleGate)
            {
                // A dispose that raced the restart wins: if a stop was requested while this
                // task tore the old generation down, stay stopped instead of resetting the
                // lifecycle and resurrecting a manager the caller just disposed.
                if (_externallyStopped)
                {
                    _restart = null;
                    return;
                }

                // Reset the terminal lifecycle state StopCoreAsync left behind so the manager
                // can start a fresh host generation: new lifetime, readiness, and registry
                // channel; per-generation state (descriptors, grants, registrations) is
                // repopulated by Start(plan). The new startup task is assigned under the same
                // lock, so GetStatus never observes the NotStarted blip between reset and
                // start.
                _starting = null;
                _stopping = null;
                _restart = null;
                _readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _lifetime = new CancellationTokenSource();
                RegistryChanges = Channel.CreateBounded<PluginRegistration[]>(
                    new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
                var startupToken = _lifetime.Token;
                _logger.LogInformation("Starting a fresh plugin host generation with the recomputed plugin set.");
                // Bound the new generation's discovery to its own lifetime: a dispose racing
                // the restart cancels the scan instead of letting it complete against a stopped
                // manager. StartCoreAsync yields immediately, so assigning under the lock is
                // safe and closes the NotStarted observation window.
                startupTask = StartCoreAsync(startupToken);
                _starting = startupTask;
            }

            // Reported after the swap, under the state gate so a reader's per-read swap cannot
            // strand the fresh source: a waiter parked on the superseded channel's frames
            // re-captures the live one (RegistryChangesReplaced).
            lock (_gate)
            {
                _registryChangesReplaced.TrySetResult();
            }

            // Wait for the new generation to be ready before the re-check below: descriptors
            // are only populated by Start(plan), so an earlier diff would see an empty set and
            // loop restarts forever.
            await startupTask.ConfigureAwait(false);

            // Watcher events fired between the old watcher's disposal and the new one arming
            // are lost (FileSystemWatcher has no replay). One post-start re-check closes the
            // gap: if the set differs again — from a change in that window — the coalesced
            // restart machinery runs another generation. The diff is deterministic, so this
            // converges instead of looping.
            if (LocalPluginSetDiffers())
            {
                _logger.LogWarning(
                    "The local plugin set changed while the host was restarting; starting another generation.");
                _ = RestartForPluginSetChangeAsync();
            }
        }
        catch (Exception exception)
        {
            // A failed restart leaves the manager stopped: StartCoreAsync has already faulted
            // readiness and cleaned up. The next watched change re-evaluates and retries.
            _logger.LogError(exception, "The plugin host restart did not complete.");
            lock (_lifecycleGate)
            {
                _restart = null;
            }
        }
    }

    /// <summary>Locates the plugin owning a changed path and ships its fresh config to the host
    /// via <c>plugin.reload</c>, so the host rebuilds that worker (and its dependents) with the
    /// latest permissions. Config/dependency changes re-resolve the descriptor; pure source edits
    /// reload with the same config so new module text is picked up. A re-read whose declaration
    /// fingerprint no longer matches the persisted approval instead stops the plugin's workers
    /// and clears its declarations — the change-revokes rule of ADR 0037.</summary>
    private async Task ReloadChangedPluginAsync(string changedPath)
    {
        // Find the most specific owning plugin: the longest root directory that
        // contains the changed path, with a path-segment boundary so a plugin
        // named "foo" never claims "foo-bar/...". Without the longest-match
        // rule the plugins-root descriptor (whose RootDirectory equals the
        // plugins root itself) would swallow every nested-plugin event.
        PluginDescriptor? owner = null;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        lock (_gate)
        {
            foreach (var descriptor in _descriptors)
            {
                if (!IsWithin(changedPath, descriptor.RootDirectory, comparison)) continue;
                if (owner is null ||
                    descriptor.RootDirectory.Length > owner.RootDirectory.Length)
                {
                    owner = descriptor;
                }
            }
        }

        if (owner is null) return;
        _logger.LogDebug(
            "Watched change at '{Path}' reloads plugin '{PluginId}'.",
            changedPath,
            owner.Id);
        EnsureApprovalStoreLoaded();

        // Re-resolve the descriptor from disk so manifest, permission, and mcp.json
        // changes apply. A failed re-read keeps the previous snapshot active — the same
        // last-known-good rule the kernel applies to its own configuration reloads —
        // and must degrade visibly, not silently.
        PluginHostConfigPlugin? replacement = null;
        var shipOwnerReload = true;
        if (PluginManifest.TryLoad(owner.RootDirectory, manifestVariables, out var reloaded, out var loadFailure))
        {
            if (!PluginImportMerger.SameMapping(owner.Imports, reloaded.Imports))
            {
                // The process import map is baked into the root deno.json at host
                // start; an in-process worker reload cannot add or change entries.
                _logger.LogWarning(
                    "Plugin '{PluginId}' import map changed; the host process import map is fixed at startup. " +
                    "Restart the host process to apply it — the worker still reloads with the previous map.",
                    owner.Id);
            }
            if (RequiresProcessIsolation(reloaded))
            {
                // A plugin that newly declares run/ffi (or switches to process
                // isolation) cannot take effect through an in-process worker
                // reload — that would require a host-process restart. Keep the
                // previous grants and surface the gap instead of silently
                // reloading with stale permissions.
                _logger.LogWarning(
                    "Plugin '{PluginId}' now requires process isolation (run/ffi grants or isolation=process); " +
                    "an in-process reload cannot apply it. Restart the host process to pick up the change.",
                    owner.Id);
            }
            else
            {
                // Approval gate first (ADR 0037): fingerprint the re-read declarations
                // before anything ships. A mismatch (or first sighting) revokes —
                // stops the workers, clears the declarations — and the plugin returns
                // to pending until re-approved; a revert to the approved shape re-matches
                // the still-stored record and reactivates through the upsert path. The
                // whole pass runs under the reconcile serialization so a concurrent
                // approve/revoke cannot interleave its frames with these.
                await _reconcile.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogDebug(
                    "Plugin '{PluginId}' reload holds the reconcile pass; approval blocked: {Blocked}.",
                    owner.Id,
                    IsApprovalBlocked(reloaded.Id));
                try
                {
                    HashSet<string> previouslyBlocked;
                    lock (_gate)
                    {
                        previouslyBlocked = BlockedIdsLock();
                        ReplaceDescriptorLock(reloaded);
                        RecomputeApprovalStatesLock(_descriptors);
                    }

                    if (IsApprovalBlocked(reloaded.Id))
                    {
                        if (!previouslyBlocked.Contains(reloaded.Id))
                            _logger.LogWarning(
                                "Plugin '{PluginId}' declarations changed since approval; its approval is revoked " +
                                "and the plugin stays inactive until re-approved (%plugin approve {CommandTarget}).",
                                reloaded.Id,
                                reloaded.Id);
                        await ApplyApprovalSnapshotAsync(previouslyBlocked).ConfigureAwait(false);
                        shipOwnerReload = false;
                    }
                    else
                    {
                        var config = BuildConfig([reloaded]);
                        replacement = config.Plugins.FirstOrDefault();
                        // The re-read worker's module text and declarations can change
                        // what its exports return while its registration records stay
                        // identical, so the reloaded plugin's MCP discovery and skill
                        // generators re-run in the forced form — but only AFTER the
                        // reload completes. The mark defers the force to the registry
                        // frame whose epoch proves the worker was replaced: forcing now
                        // would invoke the outgoing worker, and the unchanged export-set
                        // frame that follows a reload would never re-run it. The mark
                        // precedes the snapshot pass because a blocked→active owner's
                        // upsert reload ships from inside it.
                        lock (_gate)
                        {
                            _contributions?.MarkReloadForce(reloaded.Id);
                        }

                        // Declarations and capability grants follow the re-read manifest
                        // (authority stays in the kernel); dependents a re-approval just
                        // unblocked join through the same snapshot pass.
                        await ApplyApprovalSnapshotAsync(previouslyBlocked).ConfigureAwait(false);
                        if (previouslyBlocked.Contains(reloaded.Id))
                        {
                            // The owner just transitioned blocked→active and already
                            // received its upsert from the snapshot pass.
                            shipOwnerReload = false;
                        }
                        else
                        {
                            foreach (var worker in owner.Workers)
                                await SendReloadAsync(owner.Id, worker.ExportName, replacement)
                                    .ConfigureAwait(false);
                            shipOwnerReload = false;
                        }
                    }
                }
                finally
                {
                    _reconcile.Release();
                }
            }
        }

        else
        {
            _logger.LogWarning(
                "Plugin '{PluginId}' could not be re-read after a watched change; its previous declarations remain active ({Failure}).",
                owner.Id,
                loadFailure);
        }

        if (shipOwnerReload)
        {
            // An in-process reload that cannot apply (isolation or import-map change)
            // still restarts the owner with its previous config so new module text is
            // picked up — but never against the approval gate: a concurrent revoke must
            // not let this resurrect old grants.
            if (!IsApprovalBlocked(owner.Id))
            {
                // The replacement worker's module text may change what its exports
                // return over unchanged registration records; the mark defers the
                // forced rediscovery to the post-reload frame (its epoch proves the
                // worker was replaced), exactly like the ordinary reload path above.
                lock (_gate)
                {
                    _contributions?.MarkReloadForce(owner.Id);
                }

                foreach (var worker in owner.Workers)
                    await SendReloadAsync(owner.Id, worker.ExportName, replacement).ConfigureAwait(false);
            }
        }
    }

    private static bool IsWithin(string path, string root, StringComparison comparison)
    {
        if (path.Equals(root, comparison)) return true;
        return path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private Task<bool> SendReloadAsync(string pluginId, string exportName, PluginHostConfigPlugin? replacement)
    {
        return SendPluginLifecycleAsync(new PluginReloadPayload(pluginId, exportName, replacement), "reload");
    }

    /// <summary>Stops one worker and its transitive dependents without restarting them —
    /// the revocation form of <c>plugin.reload</c> (ADR 0037): the workers leave the host
    /// set entirely instead of rebooting with stale grants.</summary>
    private Task<bool> SendStopAsync(string pluginId, string exportName)
    {
        return SendPluginLifecycleAsync(
            new PluginReloadPayload(pluginId, exportName, null, Stop: true),
            "stop");
    }

    /// <summary>Sends one lifecycle frame; the result is whether it was delivered, so the
    /// reconcile pass can fall back to a generation restart when a frame is lost.</summary>
    private async Task<bool> SendPluginLifecycleAsync(PluginReloadPayload payload, string action)
    {
        WebSocket? socket;
        CancellationToken lifetimeToken;
        lock (_gate)
        {
            socket = Socket;
            lifetimeToken = _lifetime.Token;
        }

        if (socket is not { State: WebSocketState.Open })
        {
            _logger.LogWarning(
                "Plugin {Action} for '{PluginId}/{ExportName}' skipped: host not connected.",
                action,
                payload.PluginId,
                payload.ExportName);
            return false;
        }

        try
        {
            var delivered = await PushAsync(
                socket,
                new ReplEnvelope(
                    EnvelopeVersion,
                    ReplMessageType.PluginReload,
                    Guid.NewGuid().ToString("N"),
                    JsonSerializer.SerializeToElement(payload, PluginHostJsonContext.Default.PluginReloadPayload)),
                lifetimeToken).ConfigureAwait(false);
            if (!delivered)
            {
                _logger.LogWarning(
                    "Plugin {Action} request for '{PluginId}/{ExportName}' could not be delivered (the host connection is detached).",
                    action,
                    payload.PluginId,
                    payload.ExportName);
                return false;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // A lifecycle frame racing the generation's shutdown dies with it; treat it
            // as undelivered rather than surfacing a raw cancellation out of the command.
            _logger.LogDebug(
                exception,
                "Plugin {Action} for '{PluginId}/{ExportName}' was cut off by shutdown.",
                action,
                payload.PluginId,
                payload.ExportName);
            return false;
        }

        _logger.LogInformation(
            "Plugin {Action} requested for '{PluginId}/{ExportName}'.",
            action,
            payload.PluginId,
            payload.ExportName);
        return true;
    }

    /// <summary>
    ///     Requests one extension point call on one plugin worker through the host connection and
    ///     waits for the outcome. The request rides the general <c>host.invoke</c> request-response
    ///     message family: the kernel sends the instruction with a correlation id, the host invokes
    ///     the plugin worker's <c>Remote&lt;T&gt;</c> surface directly in-process (ADR 0020 §7.2,
    ///     replacing the retired <c>extension.invoke</c> protocol), and answers
    ///     <c>host.invokeResult</c> / <c>host.invokeError</c> echoing that id. Failure semantics are
    ///     unchanged: an error response becomes <see cref="ExtensionCallOutcome.Error"/> and the call
    ///     cancels after <paramref name="timeout"/> (default <see cref="InvokeTimeout"/>) or when the
    ///     host disconnects.
    /// </summary>
    /// <param name="cancellableCallId">When set, cooperative cancellation is propagated to the
    /// worker: a timeout, a caller cancellation, or host shutdown posts a
    /// <c>host.invokeCancel</c> frame so the worker aborts the handler's signal instead of
    /// leaving it running unobserved. A worker that ignores the abort keeps running — the
    /// host has no per-call termination (killing it would discard the plugin's other state),
    /// so the signal is the whole mechanism.</param>
    /// <param name="pluginId">The plugin whose worker serves the extension point.</param>
    /// <param name="exportName">The worker export that carries the extension point.</param>
    /// <param name="extensionPoint">The extension point name to invoke.</param>
    /// <param name="request">The JSON payload delivered to the handler; null when the
    /// extension point takes no arguments.</param>
    /// <param name="cancellationToken">Cancels the caller's wait for the outcome.</param>
    /// <param name="timeout">Bounds the handler's run; null uses <see cref="InvokeTimeout"/>.</param>
    public async Task<ExtensionCallOutcome> InvokeExtensionPointAsync(
        string pluginId,
        string exportName,
        string extensionPoint,
        JsonElement? request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        string? cancellableCallId = null)
    {
        // Approval gate (ADR 0037): a stopped-but-registered worker would otherwise be
        // woken on demand (ADR 0035), so refusal happens here — the kernel authority —
        // before any frame reaches the host.
        if (IsApprovalBlocked(pluginId))
            return ExtensionCallOutcome.Error(
                "plugin_pending_approval",
                $"Plugin '{pluginId}' declarations are not approved ({extensionPoint}).");

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ExtensionCallOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingInvokes[correlationId] = tcs;
        try
        {
            // Built inside the guard: a stop that disposed the generation's lifetime source
            // between the call's entry and this line is a host that is gone, reported as the
            // typed unavailable outcome below instead of an ObjectDisposedException escaping
            // the invoke path (AGENTS.md invariant 18).
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            // The caller's budget is the handler's, so the wait adds the startup allowance on
            // top: a short declaration must not time out because the worker was cold.
            budget.CancelAfter(CallWaitBudget(timeout));
            await using var registration = budget.Token.Register(
                static state => (state as TaskCompletionSource<ExtensionCallOutcome>)?.TrySetCanceled(),
                tcs);
            var payload = new HostInvokePayload(pluginId, exportName, extensionPoint, request);
            var delivered = await PushAsync(
                Socket ?? throw new InvalidOperationException("The plugin host is not connected."),
                new ReplEnvelope(
                    EnvelopeVersion,
                    ReplMessageType.HostInvoke,
                    correlationId,
                    JsonSerializer.SerializeToElement(payload, ReplControlJsonContext.Default.HostInvokePayload)),
                budget.Token).ConfigureAwait(false);
            if (!delivered)
                return ExtensionCallOutcome.Error(
                    "plugin_unavailable",
                    $"The plugin host connection is not available ({extensionPoint}).");
            return await tcs.Task.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            if (cancellableCallId is not null)
                await RequestCallCancellationAsync(pluginId, exportName, cancellableCallId).ConfigureAwait(false);
            return ExtensionCallOutcome.Error(
                "plugin_unavailable",
                "The plugin host stopped before the call completed.");
        }
        catch (ObjectDisposedException)
        {
            // The generation's lifetime source was disposed by StopCoreAsync while this call
            // was in flight: the host is gone, and the failure stays recoverable within the
            // turn (AGENTS.md invariant 18) instead of surfacing as a raw exception.
            return ExtensionCallOutcome.Error(
                "plugin_unavailable",
                "The plugin host stopped before the call completed.");
        }
        catch (OperationCanceledException) when (cancellableCallId is not null)
        {
            // Either the caller cancelled (turn cancellation) or the per-call budget
            // expired. The worker still holds the handler, so propagate the abort before
            // surfacing the outcome.
            await RequestCallCancellationAsync(pluginId, exportName, cancellableCallId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _pendingInvokes.TryRemove(correlationId, out _);
        }
    }

    /// <summary>Posts one best-effort cancel instruction for a call the kernel stopped waiting
    /// on. Bounded by its own short budget so shutdown can never be extended by a stalled
    /// host, and failures are logged: the caller's outcome is already decided.</summary>
    private async Task RequestCallCancellationAsync(string pluginId, string exportName, string callId)
    {
        try
        {
            if (Socket is not { State: WebSocketState.Open } socket) return;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            budget.CancelAfter(CallCancellationBudget);
            var payload = new HostInvokeCancelPayload(pluginId, exportName, callId);
            await PushAsync(
                socket,
                new ReplEnvelope(
                    EnvelopeVersion,
                    ReplMessageType.HostInvokeCancel,
                    Guid.NewGuid().ToString("N"),
                    JsonSerializer.SerializeToElement(payload, ReplControlJsonContext.Default.HostInvokeCancelPayload)),
                budget.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "The cancel instruction for tool call '{CallId}' was not delivered.", callId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Propagating cancellation for tool call '{CallId}' failed.", callId);
        }
    }

    /// <summary>The current plugin tool declarations as provider-visible functions. An
    /// immutable snapshot: a run holds the functions it acquired, while a later registry
    /// frame may describe replacements for the next run.</summary>
    public IReadOnlyList<AIFunction> SnapshotPluginTools()
    {
        return _toolCoordinator?.SnapshotTools() ?? [];
    }

    /// <summary>Completes when no plugin tool describe pass is running or queued. Internal
    /// test observability: a registry frame's reconciliation is observable deterministically
    /// instead of by polling the tool list.</summary>
    internal Task PluginToolsIdle => _toolCoordinator?.FlightIdle ?? Task.CompletedTask;

    /// <summary>Acquires leases for plugin-discovered MCP servers that are ready.</summary>
    public async Task<IReadOnlyList<McpServerGeneration.McpServerLease>> AcquireDynamicMcpLeasesAsync(
        CancellationToken cancellationToken)
    {
        await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        return _dynamicMcpCoordinator?.AcquireLeases() ?? [];
    }

    /// <summary>Status snapshot of the plugin-discovered MCP servers (ADR 0033): the
    /// source behind <c>/mcp list</c> and the status snapshot now that the plugin
    /// data files are the only MCP configuration form. Servers awaiting reconnection
    /// still appear (reconnecting state, empty tool list).</summary>
    public IReadOnlyList<MaieuticsMcpServerInfo> GetDynamicMcpServerInfos()
    {
        return _dynamicMcpCoordinator?.GetServerInfos() ?? [];
    }

    /// <summary>The plugin-discovered MCP server generations for resource access
    /// (ADR 0026 decision 3). A racy snapshot is safe: a retired generation answers
    /// reads with the typed unavailable error until the caller releases it.</summary>
    public IReadOnlyList<McpServerGeneration> SnapshotDynamicMcpGenerations()
    {
        return _dynamicMcpCoordinator?.SnapshotGenerations() ?? [];
    }

    /// <summary>
    ///     Instructs the plugin host to derive a Deno REPL process (ADR 0020, B5b) and waits for
    ///     the outcome. The kernel decides the entry module, the complete child environment, and the
    ///     static permission shell; the host executes the derive and reports <c>host.repl.spawned</c>
    ///     / <c>host.repl.deriveFailed</c>. This method completes when the spawned report has been
    ///     processed (the pid is registered with the session registry and the permission broker by
    ///     <see cref="RegisterHostRepl"/>), or when the host reports a pre-pid failure. The host may
    ///     have emitted <c>host.repl.exited</c> already (an init-stage crash balances the spawn);
    ///     the caller observes the REPL's eval connection or its own completion instead.
    ///     <para>Correlation: the host does not echo the instruction's correlation id on its reports
    ///     (B5a), so the completion is matched by session id and generation — a report for a
    ///     session/generation this kernel did not ask it to derive is ignored here (it still
    ///     registers the pid through the spawned path). A host that derives concurrently for the
    ///     same session would be ambiguous; the session factory serializes one derive per session.</para>
    /// </summary>
    public Task<ReplDeriveOutcome> RequestReplDeriveAsync(
        HostReplDerivePayload derive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(derive);
        ArgumentException.ThrowIfNullOrWhiteSpace(derive.SessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(derive.Generation);

        var key = DeriveKey(derive.SessionId, derive.Generation);
        var tcs = new TaskCompletionSource<ReplDeriveOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingDerives.TryAdd(key, tcs))
            throw new InvalidOperationException(
                $"A REPL derive is already in flight for session '{derive.SessionId}' generation {derive.Generation}.");

        return SendDeriveAsync(derive, tcs, key, cancellationToken);
    }

    private async Task<ReplDeriveOutcome> SendDeriveAsync(
        HostReplDerivePayload derive,
        TaskCompletionSource<ReplDeriveOutcome> tcs,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            // Built inside the guard: a stop that disposed the generation's lifetime source
            // between the call's entry and this line is a host that is gone, reported as the
            // typed failed outcome below instead of an ObjectDisposedException escaping the
            // derive path (AGENTS.md invariant 18).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(ReplDeriveTimeout);
            await using var registration = timeout.Token.Register(
                static state => (state as TaskCompletionSource<ReplDeriveOutcome>)?.TrySetCanceled(),
                tcs);
            await PushAsync(
                Socket ?? throw new InvalidOperationException("The plugin host is not connected."),
                new ReplEnvelope(
                    EnvelopeVersion,
                    ReplMessageType.HostReplDerive,
                    Guid.NewGuid().ToString("N"),
                    JsonSerializer.SerializeToElement(derive, ReplControlJsonContext.Default.HostReplDerivePayload)),
                timeout.Token).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The generation's lifetime source was disposed by StopCoreAsync while this derive
            // was in flight: the host is gone, and the failure stays recoverable — the session
            // factory falls back to a kernel-derived REPL — instead of surfacing as a raw
            // exception (AGENTS.md invariant 18).
            return ReplDeriveOutcome.DeriveFailed("The plugin host stopped before the derive completed.");
        }
        finally
        {
            _pendingDerives.TryRemove(key, out _);
        }
    }

    private static string DeriveKey(string sessionId, int generation)
    {
        return $"{sessionId}\u0000{generation.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Runs the receiving loop for a plugin host WebSocket attached by the control host.
    /// One host connection is live at a time: a second attach while one is live is refused with a
    /// policy-violation close (mirroring how the REPL session hosts reject a second attach) so it
    /// can never overwrite the live socket, outbound queue, or writer.</summary>
    public async Task AttachHostAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
        bool refused;
        lock (_gate)
        {
            refused = Socket is not null;
            if (!refused)
            {
                // The accept completes the attach signal and swaps a fresh source in the same
                // locked section that installs the socket, so one await observes one accept.
                _hostConnectionAttached.TrySetResult();
                _hostConnectionAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // A fresh release signal per accepted connection: tests capture it before
                // detaching and await it instead of polling re-attach attempts.
                _hostConnectionReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Socket = socket;
                // A WebSocket permits one outstanding send: every C#→host frame is
                // serialized through this bounded channel instead of concurrent direct
                // sends, which would fault with "already one outstanding send" the
                // moment a capability reply raced a hook request.
                _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });
                _outboundWriter = WriteOutboundAsync(socket, _outbound, cancellationToken);
            }
        }

        if (refused)
        {
            _logger.LogWarning("Refused a second plugin host attach while one is already live.");
            await RejectSecondAttachAsync(socket).ConfigureAwait(false);
            return;
        }

        try
        {
            // Generation fence: this loop serves the process generation that was live at
            // attach. A plugin-set restart stops this generation and starts a new one; the
            // old loop can still hold a frame read just before the kill, and handling it
            // after the restart's Start(plan) re-seeded the registry would merge the stale
            // plugin set into the new generation until the new host's own registry frame
            // arrives. Frames from a superseded generation are dropped instead.
            var generationProcess = Volatile.Read(ref _process);
            while (socket.State == WebSocketState.Open)
            {
                var text = await ReplControlMessageReader
                    .ReadAsync(socket, cancellationToken)
                    .ConfigureAwait(false);
                if (text is null) break;
                if (!ReferenceEquals(Volatile.Read(ref _process), generationProcess)) break;

                HandleHostMessage(text);
            }
        }
        finally
        {
            Task? writer;
            lock (_gate)
            {
                // Guarded by the same identity check as Socket: when a successor owns the
                // connection, the old detach must not complete the successor's channel, clear
                // its writer, or await it.
                if (!ReferenceEquals(socket, Socket))
                {
                    writer = null;
                }
                else
                {
                    Socket = null;
                    _outbound?.Writer.TryComplete();
                    _outbound = null;
                    writer = _outboundWriter;
                    _outboundWriter = null;
                    // Signaled after the slot is cleared, under the same lock, so an awaiter is
                    // guaranteed to observe the released slot.
                    _hostConnectionReleased.TrySetResult();
                    // A fresh attach signal per detached connection: awaits captured after the
                    // detach wait for the next accept instead of observing the retired one.
                    _hostConnectionAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            // Fail dependents BEFORE draining the pump: a stuck send must not delay
            // the typed host_disconnected outcomes by even one send budget.
            FailPending("The plugin host connection closed.");

            if (writer is not null)
            {
                try
                {
                    await writer.ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or WebSocketException or InvalidOperationException)
                {
                    // The writer ends with the socket; a mid-frame failure is the
                    // disconnect itself and is surfaced through FailPending above.
                }
            }

            // A crashed or killed host leaves its derived REPL children registered; release their
            // session identities and broker policies here (the host's own exited reports never
            // arrive). Idempotent with the exited-report path.
            ReleaseHostReplRegistrations();
        }
    }

    /// <summary>Closes a refused second host attach with a policy-violation status so the
    /// connecting side learns why it was rejected (mirrors the REPL session hosts).</summary>
    private static async Task RejectSecondAttachAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "A plugin host connection is already attached.",
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The rejected socket was already gone; the refusal needs no further delivery.
        }
    }

    /// <summary>Drains the outbound queue: one writer, one outstanding WebSocket send.
    /// Per-frame send budget bounds a host that stops reading; a send failure ends the
    /// loop and the disconnect path takes over.</summary>
    private async Task WriteOutboundAsync(
        WebSocket socket,
        Channel<byte[]> channel,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await foreach (var payload in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                using var sendBudget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await socket
                    .SendAsync(payload, WebSocketMessageType.Text, true, sendBudget.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
            // Socket died, host stopped, or a frame overran its send budget.
        }
        finally
        {
            // Completing the channel makes any late enqueue fail fast with
            // ChannelClosedException instead of blocking on a dead connection.
            channel.Writer.TryComplete();
        }
    }

    private Task EnsureStoppedAsync()
    {
        lock (_lifecycleGate)
        {
            return _stopping ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        _readiness.TrySetCanceled();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        FailPending("The plugin host is stopping.");
        Task[] draining;
        lock (_gate)
        {
            draining = [.. _capabilityCalls];
            _capabilityCalls.Clear();
        }

        if (draining.Length > 0)
        {
            // One total budget for every in-flight capability call; reply writes ride
            // the bounded queue, so nothing here extends past the budget observably.
            using var drainBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await Task.WhenAll(draining).WaitAsync(drainBudget.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or AggregateException)
            {
                _logger.LogDebug(
                    exception,
                    "{Count} capability call(s) were still running at host stop.",
                    draining.Length);
            }
        }

        if (_dynamicMcpCoordinator is { } coordinator) await coordinator.DisposeAsync().ConfigureAwait(false);
        PluginToolCoordinator? tools;
        lock (_gate)
        {
            tools = _toolCoordinator;
            _toolCoordinator = null;
        }

        if (tools is not null) await tools.DisposeAsync().ConfigureAwait(false);
        ClearSkillContributions();
        RegistryChanges.Writer.TryComplete();
        StopPluginWatcher();

        Task[] reloading;
        lock (_gate)
        {
            reloading = [.. _watcherReloads];
            _watcherReloads.Clear();
        }

        if (reloading.Length > 0)
        {
            // The watcher is stopped; one total budget observes the in-flight debounced reloads
            // so their completion is never an unobserved task exception after stop.
            using var reloadBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await Task.WhenAll(reloading).WaitAsync(reloadBudget.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or AggregateException)
            {
                _logger.LogDebug(
                    exception,
                    "{Count} plugin watcher reload(s) were still running at host stop.",
                    reloading.Length);
            }
        }

        if (_process is not null)
        {
            await _process.StopAsync().ConfigureAwait(false);
            await _processExitObservation.ConfigureAwait(false);
        }
        else if (_configPath is not null)
            try
            {
                File.Delete(_configPath);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup must not mask shutdown.
            }

        _lifetime.Dispose();
    }

    /// <summary>Waits for the in-flight plugin-set restart to unwind, so an external stop
    /// (dispose or hosted-service stop) never returns while a restart could still reset the
    /// lifecycle state or start a fresh host generation behind it. Past the restart's
    /// generation swap the restart field is already cleared and the in-flight work is the
    /// successor generation's <c>starting</c> startup, which the stop cancels but never
    /// awaits — its completion is what would move the reported state after the stop
    /// returns — so both captured tasks are observed here. The restart observes the
    /// externally-stopped flag at its reset and aborts, so it settles as soon as the stop
    /// does; the joined startup settles canceled or faulted against this stop (its
    /// disposal-check failure reports through readiness) and its unwind awaits this very
    /// stop as its own teardown, which is why the observation happens in the external
    /// stop's caller and never inside <see cref="StopCoreAsync"/> — awaiting either task
    /// there would wait on a task that is itself waiting for this stop. Bounded by the
    /// same 30 s budget the stop uses for its own drains, so a wedged restart cannot hold
    /// the host stop open.</summary>
    private async Task ObserveInFlightRestartAsync(Task? inFlightRestart, Task? inFlightStartup)
    {
        if (inFlightRestart is null && inFlightStartup is null) return;

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (inFlightRestart is not null)
                await inFlightRestart.WaitAsync(budget.Token).ConfigureAwait(false);
            if (inFlightStartup is not null)
                await inFlightStartup.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The restart handles its own failures, and a joined startup already reported
            // its outcome through readiness; observing them here must not surface their
            // cancellation or fault as the stop's own failure.
            _logger.LogWarning(
                exception,
                "The plugin host restart or startup was still unwinding when the host stopped.");
        }
    }

    private async Task ObserveExitAsync(PluginHostProcess pluginProcess, string path)
    {
        try
        {
            await pluginProcess.Completion.ConfigureAwait(false);
            _logger.LogWarning(
                "Plugin host exited with code {ExitCode} (pid {ProcessId}).",
                pluginProcess.ExitCode ?? -1,
                pluginProcess.ProcessId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Plugin host exit observation failed for pid {ProcessId}.",
                pluginProcess.ProcessId);
        }
        finally
        {
            _sessionRegistry.UnregisterPluginHost(pluginProcess.ProcessId);
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private async Task<List<PluginDescriptor>> ScanPluginsAsync(CancellationToken cancellationToken)
    {
        // The plugins root always exists (EnsurePluginsRoot ran in Start): it is
        // itself the plugin project. Its maieutics.json declares its own
        // entrypoints (empty in the skeleton), and its deno.json imports declare
        // the installed plugins (local file/workspace packages whose directory
        // carries a maieutics.json). jsr:/npm: imports are resolved by the Deno
        // toolchain at install time, not by the kernel.
        var result = new List<PluginDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddDescriptor(PluginDescriptor descriptor)
        {
            if (seen.Add(descriptor.Id))
            {
                result.Add(descriptor);
                _logger.LogInformation(
                    "Discovered Maieutics plugin '{PluginName}' with {WorkerCount} worker entrypoint(s).",
                    descriptor.Name,
                    descriptor.Workers.Count);
            }
        }

        foreach (var descriptor in ScanProject(pluginsRoot))
        {
            AddDescriptor(descriptor);
        }

        // Registry-installed plugins: exact-version jsr: dependencies of the root
        // project are resolved through the Deno toolchain and loaded with the same
        // sibling manifest contract (docs/plugin-import-resolution.md §9). The
        // toolchain queries run asynchronously: a cold-cache `deno info` performs
        // real network fetches and must never block a thread-pool thread.
        var diagnostics = new List<string>();
        foreach (var import in PluginManifest.ReadImports(pluginsRoot))
        {
            if (import.Value.StartsWith("jsr:", StringComparison.Ordinal))
            {
                var jsrDescriptor = await PluginRegistryDiscovery.TryLoadJsrAsync(
                    import.Key,
                    import.Value,
                    _denoOptions.Executable,
                    (url, token) => PluginRegistryDiscovery.DenoInfoLocalPathAsync(_denoOptions.Executable, url, token),
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                if (jsrDescriptor is not null) AddDescriptor(jsrDescriptor);
            }
            else if (import.Value.StartsWith("npm:", StringComparison.Ordinal))
            {
                var npmDescriptor = await PluginRegistryDiscovery.TryLoadNpmAsync(
                    import.Key, import.Value, _denoOptions.Executable, diagnostics, cancellationToken).ConfigureAwait(false);
                if (npmDescriptor is not null) AddDescriptor(npmDescriptor);
            }
        }
        foreach (var diagnostic in diagnostics)
        {
            _logger.LogWarning("{Diagnostic}.", diagnostic);
        }
        return result;
    }

    /// <summary>Scans one plugin project directory: itself, plus its imports-declared local plugins.</summary>
    private List<PluginDescriptor> ScanProject(string projectDirectory)
    {
        var result = new List<PluginDescriptor>();
        AddScannedDescriptor(result, projectDirectory);

        foreach (var importTarget in PluginManifest.ReadLocalImportTargets(projectDirectory))
        {
            var packageDirectory = Path.GetDirectoryName(importTarget);
            if (packageDirectory is null || !Directory.Exists(packageDirectory)) continue;
            AddScannedDescriptor(result, packageDirectory);
        }
        return result;
    }

    /// <summary>Adds one scanned plugin directory when it loads and does not require
    /// process isolation. A directory that carries declarations (maieutics.json or an
    /// mcp.json data file) but fails to load is an authoring error, so it degrades
    /// visibly instead of silently disappearing from the plugin set.</summary>
    private void AddScannedDescriptor(List<PluginDescriptor> result, string directory)
    {
        if (!PluginManifest.TryLoad(directory, manifestVariables, out var descriptor, out var error))
        {
            if (File.Exists(Path.Combine(directory, "maieutics.json")) ||
                File.Exists(Path.Combine(directory, "mcp.json")))
                _logger.LogWarning(
                    "Plugin directory '{Directory}' failed to load and is skipped ({Failure}).",
                    directory,
                    error);
            return;
        }

        if (!RequiresProcessIsolation(descriptor)) result.Add(descriptor);
    }

    private static bool RequiresProcessIsolation(PluginDescriptor descriptor)
    {
        return string.Equals(descriptor.Isolation, "process", StringComparison.OrdinalIgnoreCase) ||
               descriptor.Permissions.Run.AllowAll || descriptor.Permissions.Run.Values.Count > 0 ||
               descriptor.Permissions.Ffi.AllowAll || descriptor.Permissions.Ffi.Values.Count > 0;
    }

    // —— Plugin declaration approval (ADR 0037) ——

    /// <summary>Loads the approval store once, lazily (constructors stay side-effect
    /// free). A load error leaves the empty fail-closed store and surfaces visibly; the
    /// file is not watched, so a mid-run corruption cannot silently approve anything.</summary>
    private void EnsureApprovalStoreLoaded()
    {
        if (_approvalStoreLoaded) return;
        lock (_gate)
        {
            if (_approvalStoreLoaded) return;
            _approvalStore = PluginApprovalStore.Load(_approvalsPath, out var error);
            if (error is { } loadError)
                _logger.LogError("Plugin approvals are unavailable: {Error}.", loadError);
            _approvalStoreLoaded = true;
        }
    }

    /// <summary>Whether the plugin's declarations are currently blocked from taking
    /// effect (pending approval, or blocked by a pending transitive dependency).</summary>
    private bool IsApprovalBlocked(string pluginId)
    {
        lock (_gate)
        {
            return IsApprovalBlockedLock(pluginId);
        }
    }

    /// <summary>Called under <see cref="_gate" />. Internal for the contribution
    /// adapters, which commit their slots under the same gate.</summary>
    internal bool IsApprovalBlockedLock(string pluginId)
    {
        return _approvalStates.GetValueOrDefault(pluginId) is
            PluginApprovalState.PendingApproval or PluginApprovalState.BlockedByDependency;
    }

    /// <summary>The manager's state gate, shared with the contribution adapters of this
    /// assembly: they take the same lock the contribution dictionaries took before the
    /// framework extraction. Never held across an await.</summary>
    internal Lock Gate => _gate;

    /// <summary>Captures one skills reconcile pass's host-state input under
    /// <see cref="_gate" /> — the pass's only gate section before its awaits: the
    /// generation token, the descriptor (collapsed to null when the plugin is gone or
    /// approval-blocked), its declared Skills entries, its live Skills exports, and its
    /// held-slot state.</summary>
    internal SkillPassInput CaptureSkillPassInputLock(
        string pluginId,
        string skillsKindName,
        string skillsExtensionPointName)
    {
        lock (_gate)
        {
            var invokeToken = _lifetime.Token;
            var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == pluginId);
            var holdsContribution = _skillsDelivery is not null && _skillsDelivery.HoldsFace(pluginId);
            IReadOnlyList<PluginExtensionEntry> entries;
            List<string> generatorExports;
            if (descriptor is not null && !IsApprovalBlockedLock(pluginId))
            {
                entries = descriptor.Extensions
                    .Where(entry => entry.Kind == skillsKindName)
                    .ToArray();
                generatorExports = _registrations
                    .Where(registration => registration.PluginId == pluginId &&
                                           registration.ExtensionPoint == skillsExtensionPointName)
                    .Select(static registration => registration.ExportName)
                    .ToList();
            }
            else
            {
                // Gone or blocked: the whole contribution slot goes, whatever face it had.
                descriptor = null;
                entries = [];
                generatorExports = [];
            }

            return new SkillPassInput(
                invokeToken,
                descriptor,
                entries,
                generatorExports,
                holdsContribution);
        }
    }

    /// <summary>Creates the contribution engine and its two kind deliveries (once per
    /// manager; restarts only reset the per-generation state). Called at the top of
    /// <see cref="Start"/> so every event point can reach the engine.</summary>
    private void EnsureContributionEngine()
    {
        if (_contributions is not null) return;
        var table = new ContributionSlotTable(SkillsContributionKind.ExtensionKind);
        var engine = new ContributionCoordinator(_logger);
        _mcpDelivery = new McpContributionDelivery(
            McpDiscoverContributionKind.ExtensionKind,
            ReplExtensionPointName.McpDiscover,
            () => _dynamicMcpCoordinator,
            _logger);
        _skillsDelivery = new SkillsContributionDelivery(
            this,
            SkillsContributionKind.ExtensionKind,
            ReplExtensionPointName.Skills,
            table,
            engine,
            skillCatalog,
            manifestVariables,
            workspaceRootAccessor,
            _logger);
        engine.Attach([_mcpDelivery, _skillsDelivery]);
        _contributions = engine;
    }

    /// <summary>Creates the plugin tool coordinator once per manager. It invokes through the
    /// manager's own extension-point channel, so its describe/cancel semantics match every
    /// other plugin call, and it consults the reserved tool names the runtime configuration
    /// publishes.</summary>
    private void EnsureToolCoordinator()
    {
        if (_toolCoordinator is not null) return;
        _toolCoordinator = new PluginToolCoordinator(
            (pluginId, exportName, extensionPoint, request, cancellationToken, timeout, callId) =>
                InvokeExtensionPointAsync(
                    pluginId,
                    exportName,
                    extensionPoint,
                    request,
                    cancellationToken,
                    timeout,
                    callId),
            () =>
            {
                lock (_gate) return _reservedToolNames;
            },
            _logger);
    }

    /// <summary>The snapshot of blocked plugin ids, for transition detection. Called under
    /// <see cref="_gate" />.</summary>
    private HashSet<string> BlockedIdsLock()
    {
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pluginId, state) in _approvalStates)
        {
            if (state is PluginApprovalState.PendingApproval or PluginApprovalState.BlockedByDependency)
                blocked.Add(pluginId);
        }

        return blocked;
    }

    /// <summary>Recomputes fingerprints and approval classifications for the current
    /// descriptor set: exempt declarations pass, matching persisted fingerprints are
    /// approved, everything else is pending, and pending cascades to transitive dependents
    /// (their own code cannot honor its declarations without the dependency). Called under
    /// <see cref="_gate" /> after the descriptors or the approval store changed.</summary>
    private void RecomputeApprovalStatesLock(IReadOnlyList<PluginDescriptor> current)
    {
        // Every approval-bearing transition recomputes classifications under the gate, so
        // completing the signal here covers start, watched reloads, approvals, and revokes.
        // A completed source is handed back once and a fresh one installed: a waiter that
        // races the transition observes that edge instead of waiting for the next one.
        var changed = _approvalsChanged;
        _approvalsChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();

        _declarationFingerprints.Clear();
        _approvalStates.Clear();
        foreach (var descriptor in current)
        {
            var fingerprint = PluginDeclarationFingerprint.Compute(descriptor);
            _declarationFingerprints[descriptor.Id] = fingerprint;
            if (PluginDeclarationFingerprint.IsApprovalExempt(descriptor))
                _approvalStates[descriptor.Id] = PluginApprovalState.Exempt;
            else if (_approvalStore.TryGet(descriptor.Id, out var record) && record is { } approved &&
                     string.Equals(approved.Fingerprint, fingerprint, StringComparison.Ordinal))
                _approvalStates[descriptor.Id] = PluginApprovalState.Approved;
            else
                _approvalStates[descriptor.Id] = PluginApprovalState.PendingApproval;
        }

        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var descriptor in current)
            {
                if (_approvalStates.GetValueOrDefault(descriptor.Id) != PluginApprovalState.Approved) continue;
                if (descriptor.Dependencies.Any(dependency =>
                        _approvalStates.GetValueOrDefault(dependency) is
                            PluginApprovalState.PendingApproval or PluginApprovalState.BlockedByDependency))
                {
                    _approvalStates[descriptor.Id] = PluginApprovalState.BlockedByDependency;
                    grew = true;
                }
            }
        }
    }

    /// <summary>Replaces one plugin's descriptor in the loaded set (a reload re-read it
    /// from disk). Keeps discovery order; an unseen id appends. Called under
    /// <see cref="_gate" />.</summary>
    private void ReplaceDescriptorLock(PluginDescriptor reloaded)
    {
        for (var index = 0; index < _descriptors.Count; index++)
        {
            if (!string.Equals(_descriptors[index].Id, reloaded.Id, StringComparison.Ordinal)) continue;
            _descriptors[index] = reloaded;
            return;
        }

        _descriptors.Add(reloaded);
    }

    /// <summary>Reconciles every approval-driven surface after the approval states
    /// changed (approve, revoke, or a reload whose re-read descriptor changed
    /// classification): rebuilds capability grants and declarative snapshots for the newly
    /// active set, republishes the MCP registry subset and the adjustment snapshot, ships
    /// an upsert reload to every plugin that just became active (its config joins the host
    /// worker set), and a stop to every plugin that just became blocked (its workers and
    /// their dependents close without restart). Callers hold <see cref="_reconcile" />: the
    /// whole pass — state capture, frame construction, frame delivery — is single-flight,
    /// so two approvals/reloads can never interleave their sends and leave the host's
    /// worker set contradicting the kernel's approval state. Nothing here awaits under
    /// <see cref="_gate" />.</summary>
    private async Task ApplyApprovalSnapshotAsync(HashSet<string> previouslyBlocked)
    {
        var activations = new List<(string PluginId, PluginHostConfigPlugin Config, string[] Exports)>();
        var stops = new List<(string PluginId, string[] Exports)>();
        PluginRegistration[] registrySnapshot;
        List<string> transitioningSkillPlugins;
        HashSet<string> transitioningPlugins;
        lock (_gate)
        {
            _capabilityGrants.Clear();
            _descriptorExtensions.Clear();
            _recordedDeclarations.Clear();
            foreach (var descriptor in _descriptors)
            {
                if (IsApprovalBlockedLock(descriptor.Id)) continue;
                RecordDescriptorDeclarationsLock(descriptor);
                _capabilityGrants[descriptor.Id] = descriptor.Capabilities;
            }

            _registrations.Clear();
            _registrations.AddRange(_hostRegistrations
                .Where(registration => !IsApprovalBlockedLock(registration.PluginId)));
            _registrations.AddRange(DeclarativeRegistrationsLock());

            var blocked = BlockedIdsLock();
            transitioningPlugins = new HashSet<string>(StringComparer.Ordinal);
            // Differential skill reconcile (the former whole-set pass): only plugins
            // whose approval classification actually transitioned reconcile. The face
            // check includes a held contribution slot so a publish-only plugin that
            // just got revoked still loses its published part.
            transitioningSkillPlugins = [];
            foreach (var pluginId in previouslyBlocked.Union(blocked, StringComparer.Ordinal))
            {
                if (previouslyBlocked.Contains(pluginId) == blocked.Contains(pluginId)) continue;
                transitioningPlugins.Add(pluginId);
                if (HasSkillFaceLock(pluginId)) transitioningSkillPlugins.Add(pluginId);
            }
            // Activation configs are assigned over the FULL active set, not just the
            // transitioning plugin: the storage name-collision rule (same-named plugins
            // never silently share a data dir) must hold for post-boot approvals too.
            IReadOnlyList<PluginHostConfigPlugin> activeConfigs = [];
            if (_descriptors.Any(descriptor =>
                    descriptor.Workers.Count > 0 &&
                    !RequiresProcessIsolation(descriptor) &&
                    previouslyBlocked.Contains(descriptor.Id) &&
                    !blocked.Contains(descriptor.Id)))
            {
                activeConfigs = BuildConfig(
                    [.. _descriptors.Where(descriptor => !blocked.Contains(descriptor.Id))]).Plugins;
            }

            foreach (var descriptor in _descriptors)
            {
                if (descriptor.Workers.Count == 0 || RequiresProcessIsolation(descriptor)) continue;
                var wasBlocked = previouslyBlocked.Contains(descriptor.Id);
                if (wasBlocked == blocked.Contains(descriptor.Id)) continue;
                var exports = descriptor.Workers
                    .Select(static worker => worker.ExportName)
                    .ToArray();
                if (blocked.Contains(descriptor.Id))
                {
                    stops.Add((descriptor.Id, exports));
                }
                else
                {
                    var plugin = activeConfigs.FirstOrDefault(candidate => candidate.Id == descriptor.Id);
                    if (plugin is not null) activations.Add((descriptor.Id, plugin, exports));
                }
            }

            // The MCP delivery filters its subset from the full snapshot (RegistryWide
            // kinds republish frame-level, unconditionally — a revoked plugin's
            // registration leaves the snapshot and its coordinator-side contributions
            // drop with it); the skills targets stay face-filtered PerPlugin passes.
            registrySnapshot = _registrations.ToArray();
        }

        // Approval transitions change which tool exports are routable: a revoked plugin's
        // definitions leave the snapshot and drop; a newly approved plugin's exports describe.
        _toolCoordinator?.OnRegistryFrame(registrySnapshot, transitioningPlugins);

        if (_contributions is { } engine)
        {
            engine.PublishRegistryWide(new ContributionFrameInput(
                registrySnapshot,
                ContributionFrameInput.NoForcedPlugins,
                []));
        }

        UpdateAdjustmentSnapshot();
        if (_contributions is { } reconciliationEngine && _skillsDelivery is not null)
        {
            reconciliationEngine.ReconcilePlugins(
                _skillsDelivery.KindName,
                transitioningSkillPlugins);
        }

        var allDelivered = true;
        foreach (var (pluginId, config, exports) in activations)
            foreach (var exportName in exports)
                allDelivered &= await SendReloadAsync(pluginId, exportName, config).ConfigureAwait(false);
        foreach (var (pluginId, exports) in stops)
            foreach (var exportName in exports)
                allDelivered &= await SendStopAsync(pluginId, exportName).ConfigureAwait(false);

        // Convergence fallback: any frame that could not ship — no live connection, or a
        // send that failed mid-delivery — has no later event that would resend it (an
        // unstarted activation strands an approved plugin; a lost stop leaves revoked
        // workers running), so rebuild the generation; the fresh boot config reflects the
        // approval state. Declarative-only transitions recorded above need no host.
        if ((activations.Count > 0 || stops.Count > 0) && !allDelivered)
        {
            _logger.LogInformation(
                "A plugin approval transition could not ship to the host connection; restarting the plugin host generation.");
            _ = RestartForPluginSetChangeAsync();
        }
    }

    /// <summary>Persists an approval for one discovered plugin's current declarations and
    /// activates it — plus any dependents the approval unblocks — without restarting the
    /// host process: each activation ships the plugin's full config as an upsert
    /// <c>plugin.reload</c>. The whole decision runs under <see cref="_reconcile" />, so a
    /// concurrent watcher reload cannot interleave (a descriptor replaced mid-approve is
    /// re-read and the message reflects the resulting state, never a stranded record
    /// reported as active).</summary>
    /// <exception cref="InvalidOperationException">No discovered plugin matches the id, or
    /// the plugin cannot run in the in-process host.</exception>
    /// <exception cref="IOException">The approval could not be persisted.</exception>
    public async Task<string> ApproveAsync(string pluginId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureApprovalStoreLoaded();
        PluginDescriptor descriptor;
        lock (_gate)
        {
            descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == pluginId)
                         ?? throw new InvalidOperationException($"No discovered plugin matches '{pluginId}'.");
        }

        if (PluginDeclarationFingerprint.IsApprovalExempt(descriptor))
            return $"Plugin '{descriptor.Name}' declares nothing that requires approval.";

        if (RequiresProcessIsolation(descriptor))
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' requires process isolation (run/ffi grants or isolation=process); " +
                "the in-process plugin host cannot run it.");

        PluginApprovalState finalState;
        await _reconcile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_approvalStates.GetValueOrDefault(pluginId) == PluginApprovalState.Approved)
                    return $"Plugin '{descriptor.Name}' declarations are already approved.";
            }

            // Re-read under the reconcile serialization: a watcher reload that replaced
            // the descriptor while this command ran must be what gets persisted.
            lock (_gate)
            {
                descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == pluginId) ?? descriptor;
            }

            var fingerprint = PluginDeclarationFingerprint.Compute(descriptor);
            _approvalStore.Set(
                pluginId,
                new PluginApprovalRecord(
                    fingerprint,
                    descriptor.Name,
                    _timeProvider.GetUtcNow(),
                    descriptor.Permissions));

            HashSet<string> previouslyBlocked;
            lock (_gate)
            {
                previouslyBlocked = BlockedIdsLock();
                RecomputeApprovalStatesLock(_descriptors);
                finalState = _approvalStates.GetValueOrDefault(pluginId);
            }

            await ApplyApprovalSnapshotAsync(previouslyBlocked).ConfigureAwait(false);
            _logger.LogInformation(
                "Plugin '{PluginId}' declarations approved (fingerprint {FingerprintPrefix}).",
                pluginId,
                fingerprint[..8]);
        }
        finally
        {
            _reconcile.Release();
        }

        return finalState switch
        {
            PluginApprovalState.Approved =>
                $"Approved '{descriptor.Name}' — {descriptor.Workers.Count} worker(s) active.",
            PluginApprovalState.BlockedByDependency =>
                $"Approved '{descriptor.Name}' — its declarations match, but a dependency is still pending approval.",
            _ => $"The declarations of '{descriptor.Name}' changed while approving; approve again.",
        };
    }

    /// <summary>Removes one plugin's persisted approval and stops its workers — plus the
    /// dependents the stop cascade closes — in-process; the plugin returns to pending
    /// until re-approved.</summary>
    /// <exception cref="InvalidOperationException">No discovered plugin matches the id.</exception>
    /// <exception cref="IOException">The approval removal could not be persisted.</exception>
    public async Task<string> RevokeAsync(string pluginId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureApprovalStoreLoaded();
        PluginDescriptor descriptor;
        lock (_gate)
        {
            descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == pluginId)
                         ?? throw new InvalidOperationException($"No discovered plugin matches '{pluginId}'.");
            if (!_approvalStore.TryGet(pluginId, out _))
            {
                return IsApprovalBlockedLock(pluginId)
                    ? $"Plugin '{descriptor.Name}' is already unapproved (pending)."
                    : $"Plugin '{descriptor.Name}' has no approval to revoke.";
            }
        }

        await _reconcile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<string> previouslyBlocked;
            lock (_gate)
            {
                previouslyBlocked = BlockedIdsLock();
            }

            _approvalStore.Remove(pluginId);
            lock (_gate)
            {
                RecomputeApprovalStatesLock(_descriptors);
            }

            await ApplyApprovalSnapshotAsync(previouslyBlocked).ConfigureAwait(false);
        }
        finally
        {
            _reconcile.Release();
        }

        _logger.LogInformation("Plugin '{PluginId}' approval revoked; workers stopped.", pluginId);
        return $"Revoked '{descriptor.Name}' — workers stopped pending re-approval.";
    }

    /// <summary>The approval view of every discovered plugin (classification, requested
    /// versus approved fingerprints, grant summaries), for <c>%plugin list</c> and status
    /// rendering.</summary>
    public IReadOnlyList<PluginApprovalInfo> ListPluginApprovals()
    {
        EnsureApprovalStoreLoaded();
        lock (_gate)
        {
            var result = new List<PluginApprovalInfo>(_descriptors.Count);
            foreach (var descriptor in _descriptors)
            {
                var state = _approvalStates.GetValueOrDefault(descriptor.Id);
                _declarationFingerprints.TryGetValue(descriptor.Id, out var requested);
                _approvalStore.TryGet(descriptor.Id, out var record);
                result.Add(new PluginApprovalInfo(
                    descriptor.Id,
                    descriptor.Name,
                    state,
                    requested ?? string.Empty,
                    record?.Fingerprint,
                    descriptor.Permissions,
                    record?.Grants,
                    descriptor.Workers.Count));
            }

            return result;
        }
    }

    private string WriteConfigFile(IReadOnlyList<PluginDescriptor> plugins)
    {
        var config = BuildConfig(plugins);
        var json = JsonSerializer.Serialize(config, PluginHostJsonContext.Default.PluginHostConfigFile);
        var path = Path.Combine(Path.GetTempPath(), $"mc-plugins-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Builds the host config for one plugin set. A plugin's config carries the full
    /// replacement surface (permissions, workers, dependencies, storage) so a reload can ship a
    /// single plugin's new config to the host without rewriting the shared file.</summary>
    private PluginHostConfigFile BuildConfig(IReadOnlyList<PluginDescriptor> plugins)
    {
        // Storage directories derive from the manifest identity; a name
        // collision would silently merge two stores, so the whole colliding
        // group starts WITHOUT storage (typed runtime errors) instead of
        // failing the resident host over a manifest name.
        var storage = PluginStoragePaths.Assign(
            pluginDataRoot,
            plugins.Select(descriptor =>
                string.IsNullOrWhiteSpace(descriptor.Name) ? descriptor.Id : descriptor.Name));
        // Storage assignment sees the full input set (collision detection), but the
        // approval gate filters which plugins actually join the host worker set — a
        // pending plugin ships no workers and no triggers (ADR 0037).
        var configured = plugins
            .Where(descriptor => !IsApprovalBlocked(descriptor.Id))
            .Select(descriptor =>
            {
                var identity = string.IsNullOrWhiteSpace(descriptor.Name) ? descriptor.Id : descriptor.Name;
                var dataDir = storage[identity];
                if (dataDir is null)
                {
                    _logger.LogError(
                        "Plugin '{PluginId}' storage directory collides with another plugin; " +
                        "it starts without plugin storage.",
                        descriptor.Id);
                }
                return new PluginHostConfigPlugin(
                    descriptor.Id,
                    descriptor.RootDirectory,
                    [
                        .. descriptor.Workers
                            .Select(worker => new PluginHostConfigWorker(
                                worker.ExportName,
                                worker.EntryUrl,
                                SpecifierOf(descriptor, worker.ExportName)))
                    ],
                    ToConfigPermissions(descriptor.Permissions),
                    descriptor.Dependencies.ToArray(),
                    dataDir is null ? null : new PluginHostConfigStorage(dataDir),
                    TriggerConfigs(descriptor));
            })
            .ToArray();
        return new PluginHostConfigFile(configured, pluginDataRoot, ReadIdleGraceMs());
    }

    /// <summary>Wire form of a plugin's triggers (ADR 0036): watch paths already
    /// kernel-expanded; the host only listens on what it was given.</summary>
    private static IReadOnlyList<PluginHostConfigTrigger> TriggerConfigs(PluginDescriptor descriptor)
    {
        if (descriptor.Triggers.Count == 0) return [];
        return descriptor.Triggers
            .Select(trigger => new PluginHostConfigTrigger(
                trigger.Name,
                trigger.Kind,
                PluginTrigger.ActionName(trigger.Action),
                trigger.WatchPaths.Count == 0 ? null : trigger.WatchPaths,
                trigger.Depth,
                trigger.CronExpression,
                trigger.IntervalSeconds))
            .ToArray();
    }

    /// <summary>Canonical interop specifier of one worker entrypoint: `&lt;name&gt;/&lt;entrypoint&gt;`.</summary>
    private static string SpecifierOf(PluginDescriptor descriptor, string exportName)
    {
        return $"{descriptor.Name}/{exportName}";
    }

    private static PluginHostConfigPermissions ToConfigPermissions(PluginPermissionGrants permissions)
    {
        return new PluginHostConfigPermissions(
            ToGrant(permissions.Env),
            ToGrant(permissions.Net),
            ToGrant(permissions.Read),
            ToGrant(permissions.Write),
            ToGrant(permissions.Run),
            ToGrant(permissions.Ffi),
            ToGrant(permissions.Sys),
            ToGrant(permissions.Import));
    }

    private static JsonElement ToGrant(PluginPermissionGrant grant)
    {
        return grant.AllowAll
            ? JsonSerializer.SerializeToElement(true, PluginHostJsonContext.Default.Boolean)
            : JsonSerializer.SerializeToElement([.. grant.Values], PluginHostJsonContext.Default.StringArray);
    }

    internal void HandleHostMessage(string text)
    {
        ReplEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(text, ReplControlJsonContext.Default.ReplEnvelope)
                       ?? throw new JsonException("The envelope is null.");
        }
        catch (JsonException)
        {
            return;
        }

        switch (envelope.Type)
        {
            case ReplMessageType.CapabilityInvoke:
                TrackCapabilityCall(HandleCapabilityInvokeAsync(envelope));
                break;
            case ReplMessageType.HostInvokeResult:
            case ReplMessageType.HostInvokeError:
                CompletePending(envelope);
                break;
            case ReplMessageType.ExtensionRegistry:
                UpdateRegistry(ParsePayload<ExtensionRegistryPayload>(envelope));
                break;
            case ReplMessageType.PluginTrigger:
                HandlePluginTrigger(ParsePayload<PluginTriggerPayload>(envelope));
                break;
            case ReplMessageType.HostReplSpawned:
                RegisterHostRepl(ParsePayload<HostReplSpawnedPayload>(envelope));
                break;
            case ReplMessageType.HostReplExited:
                UnregisterHostRepl(ParsePayload<HostReplExitedPayload>(envelope));
                break;
            case ReplMessageType.HostReplDeriveFailed:
                CompleteDeriveFailed(ParsePayload<HostReplDeriveFailedPayload>(envelope));
                break;
        }
    }

    /// <summary>
    ///     Registers a REPL process the plugin host derived (ADR 0020): the pid becomes a
    ///     session-owned control-channel identity and a broker-scoped permission subject, the
    ///     same registration effect a kernel-derived REPL has at spawn. The host connection is
    ///     already authenticated as a trusted host (the <c>control.hello</c> host handshake), and
    ///     like every other host message this report carries no host id.
    /// </summary>
    private void RegisterHostRepl(HostReplSpawnedPayload? payload)
    {
        if (payload is null ||
            payload.Pid <= 0 ||
            string.IsNullOrWhiteSpace(payload.SessionId))
        {
            _logger.LogWarning(
                "Ignored a malformed host REPL spawned report (session '{SessionId}', pid {Pid}).",
                payload?.SessionId,
                payload?.Pid);
            return;
        }

        if (_replPolicies.TryGetValue(payload.SessionId, out var policy))
            _logger.LogDebug(
                "Host-derived REPL for session '{SessionId}' generation {Generation} registered with pid {Pid}.",
                payload.SessionId,
                payload.Generation,
                payload.Pid);
        else
        {
            // Explicit downgrade: no kernel path cached a policy for this session (the esbuild-
            // wasm resolution failed at session start, or a non-kernel session was derived).
            // Registering the empty default policy denies every REPL permission request by
            // default — a deliberate, surfaced fallback, never a silent one. The kernel must
            // compute and pre-cache the policy before the host derives the REPL (ADR 0020).
            policy = EffectivePolicy.Default;
            _logger.LogWarning(
                "No effective REPL policy is cached for session '{SessionId}'; " +
                "registered the host-derived REPL pid {Pid} with the default policy. " +
                "The kernel must compute and pre-cache the REPL policy before the host derives it (ADR 0020).",
                payload.SessionId,
                payload.Pid);
        }

        _sessionRegistry.Register(payload.Pid, payload.SessionId);
        _broker?.RegisterPolicy(payload.Pid, policy);
        _hostReplSessions[payload.Pid] = payload.SessionId;
        _logger.LogInformation(
            "Plugin host reported a REPL process for session '{SessionId}' generation {Generation} with pid {Pid}.",
            payload.SessionId,
            payload.Generation,
            payload.Pid);
        CompleteDeriveSpawned(payload);
    }

    /// <summary>Signals the pending <c>host.repl.derive</c> completion that the host's spawned
    /// report for this session/generation has been fully processed (identity + broker policy
    /// registered). The report carries no correlation id (B5a), so the match is by session and
    /// generation.</summary>
    private void CompleteDeriveSpawned(HostReplSpawnedPayload payload)
    {
        if (_pendingDerives.TryRemove(DeriveKey(payload.SessionId, payload.Generation), out var completion))
            completion.TrySetResult(ReplDeriveOutcome.Spawned());
    }

    /// <summary>Signals the pending <c>host.repl.derive</c> completion that the host rejected the
    /// instruction before any pid existed. The report carries no correlation id (B5a), so the
    /// match is by session and generation; a stale failure (no pending derive) is ignored.</summary>
    private void CompleteDeriveFailed(HostReplDeriveFailedPayload? payload)
    {
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.SessionId) ||
            string.IsNullOrWhiteSpace(payload.Message))
        {
            _logger.LogWarning("Ignored a malformed host REPL deriveFailed report.");
            return;
        }

        _logger.LogWarning(
            "The plugin host could not derive a REPL for session '{SessionId}' generation {Generation}: {Message}",
            payload.SessionId,
            payload.Generation,
            payload.Message);
        if (_pendingDerives.TryRemove(DeriveKey(payload.SessionId, payload.Generation), out var completion))
            completion.TrySetResult(ReplDeriveOutcome.DeriveFailed(payload.Message));
    }

    /// <summary>Releases the pid-scoped identity and broker policy of a host-derived REPL that
    /// exited (ADR 0020). Idempotent: the pid may already have been unregistered by another
    /// path.</summary>
    private void UnregisterHostRepl(HostReplExitedPayload? payload)
    {
        if (payload is null ||
            payload.Pid <= 0 ||
            string.IsNullOrWhiteSpace(payload.SessionId))
        {
            _logger.LogWarning(
                "Ignored a malformed host REPL exited report (session '{SessionId}', pid {Pid}).",
                payload?.SessionId,
                payload?.Pid);
            return;
        }

        _hostReplSessions.TryRemove(payload.Pid, out _);
        _sessionRegistry.Unregister(payload.Pid);
        _broker?.UnregisterProcess(payload.Pid);
        _logger.LogDebug(
            "Plugin host reported a REPL process exit for session '{SessionId}' generation {Generation} " +
            "with pid {Pid}{Failure}.",
            payload.SessionId,
            payload.Generation,
            payload.Pid,
            payload.Failure is { } failure ? $" ({failure})" : string.Empty);
    }

    /// <summary>Releases the registrations of every host-derived REPL still tracked when the host
    /// connection ends without a matching <c>host.repl.exited</c> report — a crashed, killed, or
    /// silently disconnected host never drains its children, so the pids would otherwise leak their
    /// session identity and broker policy forever. Complements the normal exited path: pids the
    /// host already reported as exited are simply absent here, and a later
    /// <see cref="UnregisterHostRepl"/> for a pid already released is idempotent. The session-keyed
    /// policy cache is deliberately untouched: it is kernel-owned and cleared on session close, so a
    /// reconnecting host re-registers the pid with the same cached policy.</summary>
    private void ReleaseHostReplRegistrations()
    {
        foreach (var pid in _hostReplSessions.Keys)
        {
            if (!_hostReplSessions.TryRemove(pid, out var sessionId)) continue;
            _sessionRegistry.Unregister(pid);
            _broker?.UnregisterProcess(pid);
            _logger.LogWarning(
                "Released the host-derived REPL registration for session '{SessionId}' (pid {Pid}) " +
                "after the plugin host disconnected without reporting its exit.",
                sessionId,
                pid);
        }
    }

    /// <summary>Tracks one capability call so shutdown can observe it; completed entries
    /// are pruned as new calls arrive to keep the list bounded. HandleCapabilityInvokeAsync
    /// handles its own failures, so a faulted call here means the reply path failed — the
    /// continuation observes it instead of leaving an unobserved task exception.</summary>
    private void TrackCapabilityCall(Task call)
    {
        lock (_gate)
        {
            _capabilityCalls.RemoveAll(static tracked =>
            {
                if (!tracked.IsCompleted) return false;
                if (tracked.IsFaulted)
                {
                    _ = tracked.Exception;
                }

                return true;
            });
            _capabilityCalls.Add(call);
        }
    }

    /// <summary>Serves one <c>capability.invoke</c> from the plugin host: the catalog gate
    /// (core-predefined capabilities only), the per-plugin grant gate (manifest-declared,
    /// deny-by-default), then the bound kernel executor. Replies echo the envelope's
    /// correlationId on the host connection.</summary>
    private async Task HandleCapabilityInvokeAsync(ReplEnvelope envelope)
    {
        WebSocket? socket;
        lock (_gate)
        {
            socket = Socket;
        }

        if (socket is null || envelope.CorrelationId is null) return;

        // One budget per call: it bounds the kernel executor; expiry is reported as a
        // typed timeout instead of leaving the host's pending call hanging.
        using var budget = new CancellationTokenSource(CapabilityCallTimeout);
        try
        {
            var request = ParsePayload<CapabilityInvokePayload>(envelope);
            if (request is null ||
                string.IsNullOrWhiteSpace(request.PluginId) ||
                string.IsNullOrWhiteSpace(request.Capability))
            {
                await PushCapabilityErrorAsync(
                    socket,
                    envelope.CorrelationId,
                    "invalid_capability_invoke",
                    "The capability.invoke payload requires a pluginId, a capability, and a payload.",
                    budget.Token).ConfigureAwait(false);
                return;
            }

            if (!PluginCapabilityCatalog.Contains(request.Capability))
            {
                await PushCapabilityErrorAsync(
                    socket,
                    envelope.CorrelationId,
                    "capability_unknown",
                    $"Capability '{request.Capability}' is not in the kernel capability catalog.",
                    budget.Token).ConfigureAwait(false);
                return;
            }

            if (!IsCapabilityGranted(request.PluginId, request.Capability))
            {
                await PushCapabilityErrorAsync(
                    socket,
                    envelope.CorrelationId,
                    "capability_denied",
                    $"Plugin '{request.PluginId}' has not declared capability '{request.Capability}'.",
                    budget.Token).ConfigureAwait(false);
                return;
            }

            if (request.Payload is not { } payload)
            {
                await PushCapabilityErrorAsync(
                    socket,
                    envelope.CorrelationId,
                    "invalid_capability_invoke",
                    "The capability.invoke payload requires a capability payload object.",
                    budget.Token).ConfigureAwait(false);
                return;
            }

            if (request.Capability == ReplCapabilityName.UiModels)
            {
                // Plugin-owned UI models (ADR 0038 stage 3): the frame routes to the
                // composition-root sink (the comm plane lives in Frontend, which Plugins
                // must not reference). Invalid frames answer before the sink runs.
                var frame = JsonSerializer.Deserialize(
                    payload.GetRawText(),
                    ReplControlJsonContext.Default.PluginUiFramePayload);
                if (frame is null ||
                    string.IsNullOrWhiteSpace(frame.CommId) ||
                    (frame.Kind != "open" && frame.Kind != "message" && frame.Kind != "close") ||
                    (frame.Kind == "open" &&
                     (string.IsNullOrWhiteSpace(frame.TargetName) ||
                      !frame.TargetName.StartsWith("maieutics.view/", StringComparison.Ordinal))))
                {
                    await PushCapabilityErrorAsync(
                        socket,
                        envelope.CorrelationId,
                        "invalid_capability_invoke",
                        "The ui.models payload requires a kind (open|message|close), a commId, and a targetName on open.",
                        budget.Token).ConfigureAwait(false);
                    return;
                }

                var uiSink = UiFrameSink;
                if (uiSink is null)
                {
                    await PushCapabilityErrorAsync(
                        socket,
                        envelope.CorrelationId,
                        "capability_unavailable",
                        "The kernel UI frame sink is not available.",
                        budget.Token).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await uiSink(request.PluginId, frame, budget.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException exception)
                {
                    // Typed producer failures (no active session, unknown family, …).
                    await PushCapabilityErrorAsync(
                        socket,
                        envelope.CorrelationId,
                        "ui_frame_rejected",
                        exception.Message,
                        budget.Token).ConfigureAwait(false);
                    return;
                }

                await PushCapabilityReplyAsync(
                    socket,
                    new ReplEnvelope(
                        envelope.Version,
                        ReplMessageType.CapabilityResult,
                        envelope.CorrelationId,
                        JsonSerializer.SerializeToElement(
                            new CapabilityResultPayload(
                                JsonDocument.Parse("{\"status\":\"ok\"}").RootElement.Clone()),
                            ReplControlJsonContext.Default.CapabilityResultPayload)),
                    budget.Token).ConfigureAwait(false);
                return;
            }

            if (ContributionKindCatalog.ByPublishCapability(request.Capability) is { } publishKind &&
                publishKind.KindName == SkillsContributionKind.ExtensionKind)
            {
                // Plugin skill publication (ADR 0039 stage 3): the payload is the
                // replacement set for the plugin's published part — the same entry shape
                // and bounds a generator returns; an empty array clears it. The publish
                // capability routes through the kind contract; a future publish-capable
                // kind joins by declaring its own capability name.
                if (SkillsContributionDelivery.ParseGeneratedSkills(
                        payload, SkillSource.PluginPublished) is not { } published)
                {
                    await PushCapabilityErrorAsync(
                        socket,
                        envelope.CorrelationId,
                        "invalid_capability_invoke",
                        "The skills.publish payload must be an array of {name, description, body?} entries.",
                        budget.Token).ConfigureAwait(false);
                    return;
                }

                var publishedCount = _skillsDelivery is { } delivery
                    ? delivery.Publish(request.PluginId, published)
                    : null;
                if (publishedCount is null)
                {
                    await PushCapabilityErrorAsync(
                        socket,
                        envelope.CorrelationId,
                        "capability_unavailable",
                        "The kernel skill catalog is not available.",
                        budget.Token).ConfigureAwait(false);
                    return;
                }

                await PushCapabilityReplyAsync(
                    socket,
                    new ReplEnvelope(
                        envelope.Version,
                        ReplMessageType.CapabilityResult,
                        envelope.CorrelationId,
                        JsonSerializer.SerializeToElement(
                            new CapabilityResultPayload(
                                JsonDocument.Parse($"{{\"status\":\"ok\",\"published\":{publishedCount}}}")
                                    .RootElement.Clone()),
                            ReplControlJsonContext.Default.CapabilityResultPayload)),
                    budget.Token).ConfigureAwait(false);
                return;
            }

            var executor = CapabilityExecutor;
            if (executor is null)
            {
                await PushCapabilityErrorAsync(
                    socket,
                    envelope.CorrelationId,
                    "capability_unavailable",
                    "The kernel capability executor is not available.",
                    budget.Token).ConfigureAwait(false);
                return;
            }

            // ide.query (ADR 0042 stage D): the payload is a tools.invoke shape
            // narrowed to the read-only IDE tools — writes are unreachable through
            // this capability by construction.
            if (request.Capability == ReplCapabilityName.IdeQuery)
            {
                var query = JsonSerializer.Deserialize(
                    payload.GetRawText(),
                    ReplControlJsonContext.Default.ToolInvokePayload);
                if (query is null ||
                    string.IsNullOrWhiteSpace(query.Tool) ||
                    query.Arguments.ValueKind != JsonValueKind.Object ||
                    !IdeQueryToolNames.Contains(query.Tool))
                {
                    await PushCapabilityErrorAsync(
                        socket,
                        envelope.CorrelationId,
                        "invalid_capability_invoke",
                        "The ide.query payload requires one of the read-only IDE tools: " +
                        string.Join(", ", IdeQueryToolNames) + " — with object arguments.",
                        budget.Token).ConfigureAwait(false);
                    return;
                }

                var queryResult = await executor(query.Tool, query.Arguments, budget.Token).ConfigureAwait(false);
                await PushCapabilityReplyAsync(
                    socket,
                    new ReplEnvelope(
                        envelope.Version,
                        ReplMessageType.CapabilityResult,
                        envelope.CorrelationId,
                        JsonSerializer.SerializeToElement(
                            new CapabilityResultPayload(queryResult),
                            ReplControlJsonContext.Default.CapabilityResultPayload)),
                    budget.Token).ConfigureAwait(false);
                return;
            }

            // Only the catalogued shape is accepted; today that is tools.invoke.
            var invoke = JsonSerializer.Deserialize(
                payload.GetRawText(),
                ReplControlJsonContext.Default.ToolInvokePayload);
            if (invoke is null ||
                string.IsNullOrWhiteSpace(invoke.Tool) ||
                invoke.Arguments.ValueKind != JsonValueKind.Object)
            {
                await PushCapabilityErrorAsync(
                    socket,
                    envelope.CorrelationId,
                    "invalid_capability_invoke",
                    "The tools.invoke payload requires a tool name and object arguments.",
                    budget.Token).ConfigureAwait(false);
                return;
            }

            var result = await executor(invoke.Tool, invoke.Arguments, budget.Token).ConfigureAwait(false);
            await PushCapabilityReplyAsync(
                socket,
                new ReplEnvelope(
                    envelope.Version,
                    ReplMessageType.CapabilityResult,
                    envelope.CorrelationId,
                    JsonSerializer.SerializeToElement(
                        new CapabilityResultPayload(result),
                        ReplControlJsonContext.Default.CapabilityResultPayload)),
                budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            _logger.LogWarning("Capability invoke '{CorrelationId}' exceeded its budget.", envelope.CorrelationId);
            // The budget is spent, so the timeout reply rides the host lifetime only (still
            // bounded: StopCoreAsync drains tracked calls under one total budget).
            await PushCapabilityErrorAsync(
                socket,
                envelope.CorrelationId,
                "capability_timeout",
                "The capability call did not settle within its budget.",
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Capability invoke '{CorrelationId}' failed.", envelope.CorrelationId);
            await PushCapabilityErrorAsync(
                socket,
                envelope.CorrelationId,
                "capability_failed",
                "The capability call failed.",
                budget.Token).ConfigureAwait(false);
        }
    }

    private Task PushCapabilityErrorAsync(
        WebSocket socket,
        string correlationId,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        return PushCapabilityReplyAsync(
            socket,
            new ReplEnvelope(
                EnvelopeVersion,
                ReplMessageType.CapabilityError,
                correlationId,
                JsonSerializer.SerializeToElement(
                    new BusErrorPayload(code, message),
                    ReplControlJsonContext.Default.BusErrorPayload)),
            cancellationToken);
    }

    /// <summary>Sends one capability reply. Delivery rides the bounded outbound queue under
    /// the caller's budget linked with the host generation's lifetime, so a full queue can
    /// never block past the budget (or past host stop); a reply whose generation is already
    /// retired is dropped (the tracked call ends with the socket).</summary>
    private async Task PushCapabilityReplyAsync(
        WebSocket socket,
        ReplEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            // The enqueue rides the linked budget; the bounded writer owns the write.
            await PushAsync(socket, envelope, linked.Token).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // StopCoreAsync disposes the generation's lifetime source at the end of a stop; a
            // reply racing that window has no destination (the tracked call ends with the
            // socket), and the drain awaiting this call must not surface the disposal as a
            // stop failure.
        }
    }

    /// <summary>Replaces one plugin's capability grants (the kernel-parsed manifest
    /// snapshot is the authority; this seam also lets tests drive the gate without a
    /// real manifest scan).</summary>
    internal void SetCapabilityGrants(string pluginId, IReadOnlyList<string> capabilities)
    {
        lock (_gate)
        {
            _capabilityGrants[pluginId] = capabilities;
        }
    }

    /// <summary>Whether the plugin declared this capability in its manifest. Grants come
    /// from the kernel-parsed manifest snapshot, never from the host connection. The
    /// approval gate is re-checked here as depth: grants are rebuilt per reconcile, and a
    /// call racing a revoke must not slip through the microsecond between the state flip
    /// and the grant clear.</summary>
    private bool IsCapabilityGranted(string pluginId, string capability)
    {
        lock (_gate)
        {
            return !IsApprovalBlockedLock(pluginId) &&
                   _capabilityGrants.TryGetValue(pluginId, out var grants) &&
                   grants.Contains(capability);
        }
    }

    private void CompletePending(ReplEnvelope envelope)
    {
        if (envelope.CorrelationId is not { } correlationId ||
            !_pendingInvokes.TryRemove(correlationId, out var completion))
            return;

        if (envelope.Type == ReplMessageType.HostInvokeResult)
        {
            var payload = ParsePayload<HostInvokeResultPayload>(envelope);
            completion.TrySetResult(ExtensionCallOutcome.Result(payload?.Value));
            return;
        }

        var error = ParsePayload<HostInvokeErrorPayload>(envelope);
        completion.TrySetResult(
            ExtensionCallOutcome.Error(error?.Code ?? "host_invoke_failed", error?.Message ?? "the extension failed"));
    }

    private void UpdateRegistry(ExtensionRegistryPayload? payload)
    {
        if (payload is null) return;

        // ADR 0038 stage 4: the host's HTTP gateway entrance rides every registry
        // snapshot; a frame without it clears any previous descriptor (the host
        // restarted without the gateway).
        lock (_gate)
        {
            _httpGateway = payload.HttpGateway;
        }

        PluginRegistration[] registrySnapshot;
        List<string> changedSkillPlugins;
        List<string> forcedByReload;
        lock (_gate)
        {
            // The differential skill-reconcile input: the Skills exports each plugin
            // held before the rebuild. Only plugins whose export set actually changed
            // (joined, left, or re-registered differently) reconcile; an unchanged
            // frame takes zero action and re-invokes no generator — unless the frame
            // completes a requested reload (the epoch mark) or retries a plugin whose
            // last reconcile left a failed generator.
            var skillsPoint = _skillsDelivery?.ExtensionPointName ?? ReplExtensionPointName.Skills;
            var skillsBefore = ContributionCoordinator.ExportSets(_hostRegistrations, skillsPoint);
            _hostRegistrations.Clear();
            foreach (var plugin in payload.Plugins)
            {
                // The approval gate must hold here too (ADR 0037): a host payload can
                // still carry a worker the kernel just revoked or stopped — routing its
                // hooks would fail every dispatch with plugin_pending_approval until the
                // host's post-stop registry frame lands.
                if (IsApprovalBlockedLock(plugin.PluginId)) continue;
                foreach (var extensionPoint in plugin.ExtensionPoints)
                {
                    // The kernel dispatches only catalogued names; unknown names are
                    // registered inertly, and the warning makes the mismatch visible
                    // instead of leaving a plugin wondering why nothing invokes it.
                    // Known names canonicalize so any accepted spelling records the
                    // constant every consumer compares against.
                    var canonical = ReplExtensionPointName.Canonicalize(extensionPoint);
                    if (canonical is null)
                    {
                        _logger.LogWarning(
                            "Plugin '{PluginId}' export '{ExportName}' registered unknown extension point '{Name}'; " +
                            "this kernel version never dispatches it.",
                            plugin.PluginId,
                            plugin.ExportName,
                            extensionPoint);
                        continue;
                    }

                    _hostRegistrations.Add(new PluginRegistration(plugin.PluginId, plugin.ExportName, canonical));
                }
            }

            _registrations.Clear();
            _registrations.AddRange(_hostRegistrations);

            // Manifest-declared entries join the same registry: they are discovery
            // results the kernel computed from the manifest snapshot, so they ride the
            // same revision/generation pipeline without a worker.
            foreach (var registration in DeclarativeRegistrationsLock())
                _registrations.Add(registration);

            _logger.LogInformation(
                "Plugin host registered {Count} extension point(s) across {PluginCount} plugin(s).",
                _registrations.Count,
                payload.Plugins.Count);

            registrySnapshot = _registrations.ToArray();
            changedSkillPlugins = ContributionCoordinator.DiffExportSets(
                skillsBefore,
                ContributionCoordinator.ExportSets(_hostRegistrations, skillsPoint));
            forcedByReload = _contributions?.DrainReloadForces(payload.ReloadEpochs) ?? [];
            // A drained reload force schedules the plugin's skill reconcile even though
            // its export set is unchanged (the reloaded module text may return
            // different output); a plugin whose last reconcile left a failed generator
            // retries on the next frame (the MCP coordinator's "never-succeeded stays
            // new" discipline).
            var retryPending = _skillsDelivery is not null && _contributions is not null
                ? _contributions.RetrySnapshot(_skillsDelivery.KindName)
                : [];
            foreach (var pluginId in forcedByReload.Concat(retryPending))
                if (!changedSkillPlugins.Contains(pluginId))
                    changedSkillPlugins.Add(pluginId);
        }

        RegistryChanges.Writer.TryWrite(registrySnapshot);
        // Plugin tools reconcile on every frame: the describe pass owns its own
        // single-flight drain, so this call never awaits the host and never holds the gate.
        _toolCoordinator?.OnRegistryFrame(
            registrySnapshot,
            new HashSet<string>(forcedByReload, StringComparer.Ordinal));
        if (_contributions is { } engine)
        {
            // The frame the host sends after completing a requested reload carries an
            // unchanged export set, so the plugins whose marks this frame drained re-run
            // their discovery in the forced form — the plain differential alone would
            // keep their pre-reload contributions forever. The RegistryWide kind
            // receives the full snapshot every frame (its registration-level diff stays
            // inside the consumer engine); the PerPlugin targets schedule their passes.
            engine.OnRegistryFrame(new ContributionFrameInput(
                registrySnapshot,
                new HashSet<string>(forcedByReload, StringComparer.Ordinal),
                changedSkillPlugins));
        }
    }

    /// <summary>Whether a plugin currently has any skill surface to reconcile: declared
    /// Skills entries, a live Skills export, or a held contribution slot (the publish-only
    /// form — the held-slot clause reads the skills delivery's slot table). Called under
    /// <see cref="_gate"/>.</summary>
    private bool HasSkillFaceLock(string pluginId)
    {
        if (_skillsDelivery is { } skills && skills.HoldsFace(pluginId)) return true;
        if (_registrations.Any(registration =>
                registration.PluginId == pluginId &&
                registration.ExtensionPoint == ReplExtensionPointName.Skills))
            return true;
        return _descriptors.FirstOrDefault(candidate => candidate.Id == pluginId) is { } descriptor &&
               descriptor.Extensions.Any(static entry =>
                   SkillsContributionKind.Instance.OwnsExtensionKind(entry.Kind));
    }

    /// <summary>A trigger fired on the host (ADR 0036): re-run the named plugin's MCP
    /// discovery over the current local state (a forced republish — the trigger's
    /// semantics are exactly "re-run this plugin", even though its registration records
    /// are unchanged; the rest of the registry keeps its contributions) and reconcile
    /// only that plugin's skills. Pure runtime — no worker wake.</summary>
    private void HandlePluginTrigger(PluginTriggerPayload? payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.PluginId)) return;
        if (McpRegistrationsSnapshot().Length == 0 && skillCatalog is null) return;
        _logger.LogInformation(
            "Plugin '{PluginId}' trigger '{Trigger}' fired; re-running its MCP discovery.",
            payload.PluginId,
            payload.Trigger);
        ForceRediscoverPlugin(payload.PluginId);
    }

    /// <summary>The merged registry's full <c>McpDiscover</c> registration set (worker
    /// registrations plus manifest-declared entries).</summary>
    private PluginRegistration[] McpRegistrationsSnapshot()
    {
        lock (_gate)
        {
            return _registrations
                .Where(static registration => registration.ExtensionPoint == ReplExtensionPointName.McpDiscover)
                .ToArray();
        }
    }

    /// <summary>Re-runs one plugin's MCP discovery and skill reconcile: the forced
    /// publish form (a trigger or a watched reload) re-runs the plugin's discovery even
    /// though its registration records are unchanged, because its exports may return
    /// different results; the plain differential paths alone would see "no
    /// change".</summary>
    private void ForceRediscoverPlugin(string pluginId)
    {
        PluginRegistration[] snapshot;
        bool hasMcpDiscover;
        lock (_gate)
        {
            snapshot = _registrations.ToArray();
            hasMcpDiscover = _mcpDelivery is { } delivery &&
                             _registrations.Any(registration =>
                                 registration.ExtensionPoint == delivery.ExtensionPointName);
        }

        if (hasMcpDiscover && _contributions is { } engine)
        {
            engine.PublishRegistryWide(new ContributionFrameInput(
                snapshot,
                new HashSet<string>([pluginId], StringComparer.Ordinal),
                []));
        }

        if (_contributions is { } reconciliation && _skillsDelivery is not null)
            reconciliation.ReconcilePlugins(_skillsDelivery.KindName, [pluginId]);
    }

    /// <summary>Clears every plugin contribution (manager disposal). The last catalog
    /// state for the plugins outlives nothing: generated skills die with the host process
    /// and declarative roots are re-contributed by the next generation's start.</summary>
    private void ClearSkillContributions()
    {
        if (skillCatalog is null || _skillsDelivery is not { } skills) return;
        lock (_gate)
        {
            foreach (var pluginId in skills.ClearAll())
                skillCatalog.RemovePluginContribution(pluginId);
        }
    }

    /// <summary>The synthetic registrations for manifest-declared entries (the
    /// <c>McpDiscover</c> extensions section or an <c>mcp.json</c> data file, ADR 0033):
    /// produced contract-driven — every catalogued kind that declares a declarative
    /// registration form contributes its face check against the recorded snapshots.
    /// Called under <see cref="_gate" />.</summary>
    private IEnumerable<PluginRegistration> DeclarativeRegistrationsLock()
    {
        var candidates = _descriptorExtensions.Keys
            .Concat(_recordedDeclarations.Keys.Select(static key => key.PluginId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var contract in ContributionKindCatalog.Contracts)
        {
            if (!contract.HasDeclarativeRegistrations) continue;
            if (contract.ExtensionPointName is not { } extensionPoint)
                throw new InvalidOperationException(
                    $"The contribution kind '{contract.KindName}' declares synthetic registrations without a wire extension point.");
            foreach (var pluginId in candidates)
            {
                _descriptorExtensions.TryGetValue(pluginId, out var entries);
                _recordedDeclarations.TryGetValue((contract.KindName, pluginId), out var recorded);
                if (contract.HasDeclarativeRegistrationFace(entries, recorded))
                    yield return new PluginRegistration(pluginId, ManifestExportName, extensionPoint);
            }
        }
    }

    /// <summary>Updates one plugin's declarative snapshots (the grammar's extensions
    /// record and each kind's recorded interpretation) on load and reload: an empty
    /// face removes the entry, so section or file removals take effect.</summary>
    private void RecordDescriptorDeclarationsLock(PluginDescriptor descriptor)
    {
        if (descriptor.Extensions.Count > 0) _descriptorExtensions[descriptor.Id] = descriptor.Extensions;
        else _descriptorExtensions.Remove(descriptor.Id);

        foreach (var contract in ContributionKindCatalog.Contracts)
        {
            if (!contract.HasDeclarativeRegistrations) continue;
            var recorded = contract.DeclarationsFromDescriptor(descriptor);
            if (recorded.Descriptors.Count > 0 || recorded.PluginLevelError is not null)
                _recordedDeclarations[(contract.KindName, descriptor.Id)] = recorded;
            else
                _recordedDeclarations.Remove((contract.KindName, descriptor.Id));
        }

        foreach (var diagnostic in descriptor.ExtensionDiagnostics)
            _logger.LogWarning("Plugin '{PluginId}': {Diagnostic}.", descriptor.Id, diagnostic);
    }

    /// <summary>The effective policy of one plugin's process-launch scope: the plugin's
    /// declared (approval-gated, ADR 0037) grants rendered as a policy, so every child the
    /// kernel starts on that plugin's behalf — its declared MCP server children — is judged
    /// and configured by the permission module instead of launching unchecked. Only the kinds
    /// the kernel-side launch consumes are carried: <c>run</c> (the stdio command gate) and
    /// <c>env</c> (the child environment); the remaining kinds shape the worker's Deno shell,
    /// which the plugin host enforces. An absent grant leaves the kind unrestricted — the same
    /// default the run gate applies everywhere (ADR 0018 §7 decision 2). Callers hold
    /// <see cref="_gate"/>.</summary>
    private EffectivePolicy? PluginScopePolicy(string pluginId)
    {
        var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == pluginId);
        if (descriptor is null) return null;

        var run = descriptor.Permissions.Run;
        var env = descriptor.Permissions.Env;
        return new EffectivePolicy(
            new Dictionary<PermissionKind, PermissionKindRules>
            {
                [PermissionKind.Run] = new PermissionKindRules
                {
                    AllowAll = run.AllowAll,
                    Allow = ResolvePluginRunGrantValues(descriptor.RootDirectory, run.Values)
                },
                [PermissionKind.Env] = new PermissionKindRules
                {
                    AllowAll = env.AllowAll,
                    Allow = env.Values
                }
            },
            manifestVariables ?? new VariableTable(new EmptyPluginScopeVariableSource()));
    }

    /// <summary>Run grant values as the launch gate compares them. Deno resolves a
    /// rooted-relative run grant against the worker's working directory — the plugin root —
    /// so the kernel-side gate resolves the same way; bare command names and absolute paths
    /// are compared as declared.</summary>
    private static IReadOnlyList<string> ResolvePluginRunGrantValues(
        string pluginRoot,
        IReadOnlyList<string> values)
    {
        var resolved = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value.Length == 0) continue;
            resolved.Add(
                value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("../", StringComparison.Ordinal)
                    ? Path.GetFullPath(Path.Combine(pluginRoot, value))
                    : value);
        }

        return resolved;
    }

    /// <summary>Whether the owning plugin's launch policy admits the server's child command.
    /// Only stdio servers start a child — an HTTP server is remote and needs no process grant.
    /// A server whose owning scope has no policy is unrestricted, the run gate's default.</summary>
    private static bool ServerCommandPermitted(McpServerDefinition definition, EffectivePolicy? scopePolicy)
    {
        if (definition.Transport is not StdioMcpTransportDefinition stdio) return true;
        if (scopePolicy is not { } launchPolicy) return true;
        return RunGrant.Allows(launchPolicy, stdio.Command);
    }

    private void RecordServerPolicies(IReadOnlyList<McpServerDefinition> definitions, EffectivePolicy? scopePolicy)
    {
        if (scopePolicy is null) return;
        foreach (var definition in definitions)
        {
            if (definition.Transport is not StdioMcpTransportDefinition) continue;
            _mcpServerPolicies[definition.Id] = scopePolicy;
        }
    }

    private sealed class EmptyPluginScopeVariableSource : IPermissionVariableSource
    {
        public string? GetVariable(string name) => null;
    }

    private async Task<PluginMcpDiscoveryResult> DiscoverDynamicMcpAsync(
        PluginRegistration registration,
        CancellationToken cancellationToken)
    {
        if (registration.ExportName == ManifestExportName)
        {
            // Manifest-declared entries have no worker: the kernel-parsed manifest
            // snapshot is the discovery result. Failures here keep the previous
            // contribution active (the same sticky-last-good semantics as handlers).
            return DiscoverManifestMcpAsync(registration);
        }

        // The owning scope's launch policy is captured with the discovery: every definition
        // this discovery produces is judged against the declaring plugin's run grant and
        // carries the policy into its generation (rendered again at child spawn).
        EffectivePolicy? scopePolicy;
        lock (_gate)
        {
            scopePolicy = PluginScopePolicy(registration.PluginId);
        }

        var request = JsonSerializer.SerializeToElement(
            new DiscoverContextPayload("registry_update"),
            ReplControlJsonContext.Default.DiscoverContextPayload);
        var outcome = await InvokeExtensionPointAsync(
                registration.PluginId,
                registration.ExportName,
                ReplExtensionPointName.McpDiscover,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome.IsError) return PluginMcpDiscoveryResult.Failed(outcome.Code);
        if (outcome.Value is not { ValueKind: JsonValueKind.Array } array)
            return PluginMcpDiscoveryResult.Failed("invalid_discovery_result");

        var definitions = new List<McpServerDefinition>();
        foreach (var item in array.EnumerateArray())
        {
            if (!McpDiscoverContributionKind.TryToDefinition(
                    registration.PluginId,
                    item,
                    manifestVariables,
                    out var definition))
                return PluginMcpDiscoveryResult.Failed("invalid_server_definition");

            if (!ServerCommandPermitted(definition, scopePolicy))
                return PluginMcpDiscoveryResult.Failed("server_command_not_permitted");

            definitions.Add(definition);
        }

        RecordServerPolicies(definitions, scopePolicy);
        return PluginMcpDiscoveryResult.Success(definitions);
    }

    /// <summary>Parses one plugin's declarative MCP contributions — the manifest's
    /// <c>mcp.discover</c> entries plus its <c>mcp.json</c> data file (ADR 0033) — with
    /// the same merge rules the coordinator applies across plugins: identical ids with
    /// identical generation keys deduplicate; identical ids with different keys fail the
    /// discovery (the sticky-last-good rule then keeps the previous contribution active).
    /// The per-entry validation granularity is the discovery-time manifest branch's own
    /// (one invalid entry fails the whole discovery); the contract's load-time
    /// interpretation deliberately does not absorb it.</summary>
    internal PluginMcpDiscoveryResult DiscoverManifestMcpAsync(PluginRegistration registration)
    {
        IReadOnlyList<PluginExtensionEntry> entries;
        IReadOnlyList<McpServerDefinition> fileServers;
        EffectivePolicy? scopePolicy;
        lock (_gate)
        {
            var hasEntries = _descriptorExtensions.TryGetValue(registration.PluginId, out var recorded);
            var hasDeclaration = _recordedDeclarations.TryGetValue(
                (McpDiscoverContributionKind.ExtensionKind, registration.PluginId),
                out var declaration);
            if (!hasEntries && !hasDeclaration)
                return PluginMcpDiscoveryResult.Failed("unknown_manifest_plugin");
            if (declaration is { PluginLevelError: not null })
                return PluginMcpDiscoveryResult.Failed("invalid_data_file");
            entries = recorded ?? [];
            fileServers = declaration is null
                ? []
                : ContributionDescriptorUnwrap.McpServers(declaration.Descriptors);
            // The declaring plugin's launch policy is captured with the manifest snapshot it
            // authorizes (ADR 0037): its stdio servers are judged against it here and carry it
            // into their generations.
            scopePolicy = PluginScopePolicy(registration.PluginId);
        }

        var definitions = new List<McpServerDefinition>();
        foreach (var entry in entries)
        {
            if (!McpDiscoverContributionKind.Instance.OwnsExtensionKind(entry.Kind)) continue;
            if (!McpDiscoverContributionKind.TryToDefinition(
                    registration.PluginId,
                    entry.Data,
                    manifestVariables,
                    out var definition))
                return PluginMcpDiscoveryResult.Failed("invalid_server_definition");

            if (!ServerCommandPermitted(definition, scopePolicy))
                return PluginMcpDiscoveryResult.Failed("server_command_not_permitted");

            definitions.Add(definition);
        }

        foreach (var fileServer in fileServers)
        {
            var existing = definitions.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, fileServer.Id, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (string.Equals(existing.GenerationKey, fileServer.GenerationKey, StringComparison.Ordinal))
                    continue;

                return PluginMcpDiscoveryResult.Failed("conflicting_server_definition");
            }

            if (!ServerCommandPermitted(fileServer, scopePolicy))
                return PluginMcpDiscoveryResult.Failed("server_command_not_permitted");

            definitions.Add(fileServer);
        }

        RecordServerPolicies(definitions, scopePolicy);
        return PluginMcpDiscoveryResult.Success(definitions);
    }

    private Task<McpServerGeneration> CreateDynamicMcpGenerationAsync(
        McpServerDefinition definition,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string> reservedNames;
        lock (_gate)
        {
            reservedNames = _reservedToolNames;
        }

        // The same capability wiring the kernel-level servers used (ADR 0029): roots are
        // answered with the live workspace root when the definition opted in, and
        // elicitation flows through the frontend presenter. Capability follows handler
        // presence, so a null source simply means the server is never asked. The stdio
        // child's policy is the owning plugin's launch scope, captured at discovery (the
        // default transport factory renders it — run gate and child environment — before
        // the child is spawned).
        _mcpServerPolicies.TryGetValue(definition.Id, out var scopePolicy);
        return McpServerGeneration.CreateAsync(
            definition,
            _loggerFactory,
            _timeProvider,
            cancellationToken,
            null,
            reservedNames,
            workspaceRootsSource,
            elicitationPresenter,
            _adjustmentChain,
            policy: scopePolicy);
    }

    private void FailPending(string message)
    {
        foreach (var completion in _pendingInvokes.Values)
            completion.TrySetResult(ExtensionCallOutcome.Error("host_disconnected", message));

        _pendingInvokes.Clear();

        foreach (var completion in _pendingDerives.Values)
            completion.TrySetResult(ReplDeriveOutcome.DeriveFailed(message));

        _pendingDerives.Clear();
    }

    /// <summary>Ordinal comparison for the recorded-declaration key (kind name, plugin
    /// id) — the tuple's default string equality is ordinal already; this makes the
    /// intent explicit.</summary>
    private sealed class DeclarationRecordKeyComparer
        : IEqualityComparer<(string KindName, string PluginId)>
    {
        public static readonly DeclarationRecordKeyComparer Ordinal = new();

        public bool Equals((string KindName, string PluginId) left, (string KindName, string PluginId) right) =>
            string.Equals(left.KindName, right.KindName, StringComparison.Ordinal) &&
            string.Equals(left.PluginId, right.PluginId, StringComparison.Ordinal);

        public int GetHashCode((string KindName, string PluginId) key) =>
            HashCode.Combine(
                key.KindName.GetHashCode(StringComparison.Ordinal),
                key.PluginId.GetHashCode(StringComparison.Ordinal));
    }

    private static T? ParsePayload<T>(ReplEnvelope envelope)
        where T : class
    {
        if (envelope.Payload is not { } payload) return null;

        return (T?)JsonSerializer.Deserialize(payload.GetRawText(), JsonTypeInfoFor<T>());
    }

    private static JsonTypeInfo JsonTypeInfoFor<T>()
    {
        return typeof(T) switch
        {
            _ when typeof(T) == typeof(CapabilityInvokePayload) =>
                ReplControlJsonContext.Default.CapabilityInvokePayload,
            _ when typeof(T) == typeof(CapabilityResultPayload) =>
                ReplControlJsonContext.Default.CapabilityResultPayload,
            _ when typeof(T) == typeof(HostInvokeResultPayload) =>
                ReplControlJsonContext.Default.HostInvokeResultPayload,
            _ when typeof(T) == typeof(HostInvokeErrorPayload) =>
                ReplControlJsonContext.Default.HostInvokeErrorPayload,
            _ when typeof(T) == typeof(ExtensionRegistryPayload) =>
                ReplControlJsonContext.Default.ExtensionRegistryPayload,
            _ when typeof(T) == typeof(HostReplSpawnedPayload) =>
                ReplControlJsonContext.Default.HostReplSpawnedPayload,
            _ when typeof(T) == typeof(HostReplExitedPayload) =>
                ReplControlJsonContext.Default.HostReplExitedPayload,
            _ when typeof(T) == typeof(HostReplDeriveFailedPayload) =>
                ReplControlJsonContext.Default.HostReplDeriveFailedPayload,
            _ when typeof(T) == typeof(PluginTriggerPayload) =>
                ReplControlJsonContext.Default.PluginTriggerPayload,
            _ => throw new InvalidOperationException($"Unsupported extension payload type '{typeof(T).Name}'.")
        };
    }

    /// <summary>Enqueues one frame on the bounded outbound queue of the connection the frame
    /// was captured for. Returns whether the frame was accepted; a retired connection, a
    /// detached host, or a completed queue drops it, and callers that report delivery must
    /// branch on the result. The socket parameter is the generation fence: a call belongs to
    /// the connection generation that was live when its socket was captured, and a restart
    /// replaces both the socket and its queue — so a frame belonging to a retired generation
    /// is refused here instead of being written into the replacement host's queue.</summary>
    private async Task<bool> PushAsync(WebSocket socket, ReplEnvelope envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, ReplControlJsonContext.Default.ReplEnvelope);
        ChannelWriter<byte[]>? writer;
        lock (_gate)
        {
            // Same lock as the capture: the frame is enqueued only while the connection it
            // belongs to still owns the outbound slot. The replacement host must never receive
            // a frame that a call issued against the retired connection.
            writer = ReferenceEquals(socket, Socket) ? _outbound?.Writer : null;
        }

        if (writer is null)
        {
            // No attached host writer for this generation (detached, never attached, or
            // replaced by a successor): nothing to send to.
            return false;
        }

        try
        {
            // Bounded with backpressure: a host that stops reading applies pressure to the
            // enqueueing callers instead of growing the queue without bound.
            await writer.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            // The writer loop ended (disconnect or stop): the frame has no destination.
            return false;
        }
    }
}
