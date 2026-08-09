# Lifecycle, Protocol, and API Remediation Plan

Status: Proposed

Date: 2026-08-08

## Purpose

This document turns the August 2026 architecture review into an ordered implementation plan. It is a remediation
record, not an accepted architecture decision: each change still requires focused review, its nearest `AGENTS.md`, and
tests in the owning layer.

The work deliberately starts with observable correctness and resource ownership, then evolves asynchronous APIs, and
only then adds user-facing features. This order avoids building new behavior on timing guesses, leaked generations, or
ambiguous ownership.

## Review summary

| Priority | Finding | Consequence | Intended direction |
|---|---|---|---|
| P0 | Jupyter execution completion waits for a fixed 50 ms quiet period | Correctness and latency depend on timing and machine load | Complete from protocol state and causal IOPub ordering |
| P0 | Plugin-discovered MCP generations are added but not reconciled or retired | Removed or changed definitions can remain live and retain processes/resources | Serialize refresh, diff snapshots, atomically publish, and retire old generations |
| P0 | Control-channel messages have per-read buffers but no total payload limit | A peer can force unbounded `MemoryStream` or request-body growth | Apply one explicit byte budget to HTTP and WebSocket messages |
| P0 | Model discovery catches cancellation as an ordinary error | Cancellation can appear as a successful result containing an error | Propagate cancellation and expose stable, sanitized failures |
| P1 | Plugin stdout is drained fully before stderr and both are retained until process exit | A full stderr pipe can deadlock the child; long-lived output can consume unbounded memory | Drain both streams concurrently with bounded, incremental logging |
| P1 | Run-profile acquisition is synchronous while rollback and dynamic dependencies are asynchronous | `GetResult()` is used in failure paths and async initialization leaks into DI | Make acquisition asynchronous and await cleanup normally |
| P1 | `Task<PluginHostManager>` and a blocking factory are registered as services | Service readiness and lifetime ownership are hidden in DI shapes | Register one directly owned host lifecycle with explicit async readiness |
| P1 | `IJupyterExecution` has no caller-visible abandon/dispose operation | A caller that stops observing an execution cannot release client-side routing state explicitly | Add idempotent local detach/disposal semantics |
| P2 | `AgentTurn` publicly accepts arbitrary `AIContent`, while the current boundary only submits text | The API promises input behavior that validation and adapters do not support | Narrow the current API or explicitly validate a supported content union |
| P2 | Deno is installed in CI but its formatting, checking, and tests are not run | Script plugin and control-channel code can regress outside .NET coverage | Add Deno quality gates and repair architecture-document indexing |

## Constraints and non-goals

All phases preserve the repository invariants in `AGENTS.md`, especially:

- Jupyter protocol 5.5 and classic connection files remain the compatibility target.
- `Maieutics.Jupyter.Client` and `Maieutics.Jupyter.Kernel` remain independent siblings over
  `Maieutics.Jupyter.Shared`; neither references the other.
- `Maieutics.Agent` remains independent of Jupyter and provider SDK objects.
- The local provider-neutral transcript remains canonical; provider-side state does not replace it.
- Tool execution remains serialized unless parallel semantics receive a separate design and test plan.
- Existing plugin and MCP concepts are repaired in their owning namespaces; this plan does not create new assemblies or
  parallel framework abstractions.
- Expected failures remain typed and recoverable. Cancellation remains cancellation rather than being converted into a
  data result.
- Every loop, process, generation, channel, and deferred completion has one owner and a deterministic termination path.

This plan does not add Jupyter history, comm, debug, subshell, automatic reconnect, remote provisioners, persistence
providers, worker transports, or an untrusted-code sandbox.

## Dependency order

```text
Phase 0: characterization and test harnesses
    |
    +--> Phase 1: protocol-driven execution completion
    |
    +--> Phase 2: plugin/MCP generation and process ownership
    |
    +--> Phase 3: control-plane bounds and safe discovery rendering
              |
              +--> Phase 4: asynchronous acquisition and explicit lifetimes
                           |
                           +--> Phase 5: API, documentation, and CI cleanup
                                        |
                                        `--> Follow-up user features
```

Phases 1 through 3 may be developed independently after Phase 0, but Phase 4 should merge only after the plugin-host
lifecycle is deterministic. Phase 5 should not hide behavior changes inside documentation or formatting commits.

## Phase 0: Baseline and characterization

### Goal and invariants

Capture current externally visible behavior and reproduce each defect without changing production behavior. Tests must
use protocol events, task completion, or controllable fakes rather than sleeps. Real-process tests remain bounded by one
overall deadline.

### Expected files

- `Maieutics.Jupyter.Tests/JupyterProtocolSessionTests.cs`
- `Maieutics.Jupyter.Tests/DenoKernelIntegrationTests.cs`
- `Maieutics.Jupyter.Tests/PluginHostIntegrationTests.cs`
- `Maieutics.Jupyter.Tests/McpServerGenerationTests.cs`
- `Maieutics.Jupyter.Tests/ReplControlHostTests.cs`
- `Maieutics.Jupyter.Tests/MaieuticsConfigurationTests.cs`
- focused Agent tests if the acquisition contract is characterized before Phase 4

### Work

1. Add execution sequences covering output before parented `idle`, shell reply before/after `idle`, unrelated traffic,
   and a deliberately delayed message after `idle`. Record which sequences are protocol-valid and which are tolerated
   compatibility cases.
2. Reproduce dynamic MCP add, unchanged refresh, replacement, removal, refresh overlap, and disposal with an outstanding
   lease. Assert generation creation and retirement counts, not elapsed delays.
3. Exercise plugin children that write enough stdout and stderr to fill either pipe, plus a noisy long-lived child.
4. Send fragmented WebSocket messages and HTTP JSON bodies that cross the future aggregate limit.
5. Verify model-discovery cancellation separately from provider failure, and render adversarial IDs/errors containing
   Markdown control characters, newlines, and links.
6. Characterize what happens when an execution consumer stops reading outputs or never awaits completion.

### Compatibility and risk

Characterization tests must not canonize an implementation accident. In particular, a post-`idle` output should be
marked as a compatibility observation, not a valid Jupyter ordering requirement. Avoid real-time sleeps except for an
outer test timeout.

### Completion criteria

- Every P0/P1 finding has a deterministic failing test or an explicit statement explaining why only integration
  coverage can reproduce it.
- Test helpers can observe generation retirement, process exit, socket close status, and execution detachment.
- The unchanged production tree passes the pre-existing suite; newly added defect tests may be introduced with the fix
  in the same commit if the repository does not permit intentionally failing tests.

### Suggested commit boundary

One tests-only characterization commit per subsystem: Jupyter, plugins/MCP, and control/configuration. Do not combine
them with production fixes.

## Phase 1: Make Jupyter execution completion protocol-driven

### Goal and invariants

Remove `TailSettleTimeout` and make completion depend on the execution's `execute_reply` plus parented IOPub `idle`.
All parented output emitted by a conforming kernel must be routed before the output stream completes. Shell reply order
relative to IOPub remains unconstrained; order within IOPub remains authoritative.

### Expected files

- `Maieutics.Jupyter.Client/Protocol/JupyterProtocolSession.cs`
- `Maieutics.Jupyter.Client/Transport/IJupyterTransport.cs`
- `Maieutics.Jupyter.Client/Transport/NetMqJupyterTransport.cs`
- `Maieutics.Jupyter.Kernel/JupyterKernelHost.cs` only if characterization finds that the owned kernel emits output
  after its parented `idle`
- `Maieutics.Jupyter.Tests/JupyterProtocolSessionTests.cs`
- `Maieutics.Jupyter.Tests/SelfHostedJupyterIntegrationTests.cs`
- `Maieutics.Jupyter.Tests/DenoKernelIntegrationTests.cs`

### Work

1. Define the terminal state as “reply observed and parented IOPub `idle` observed.” Route the `idle` event before
   completing the output writer and result task.
2. Remove mailbox quiet-period polling. If `PendingIncomingCount` and timed `WaitToReadAsync` no longer have a protocol
   use, remove them from the transport boundary rather than retaining speculative hooks.
3. Ensure the owned kernel drains/awaits all output publication associated with `ExecuteAsync` before publishing its
   parented `idle`. Fix this at the producer if it is currently violated.
4. Treat post-terminal messages as late/unhandled protocol events according to a documented policy. Do not reopen a
   completed execution and do not add a replacement delay.
5. Keep failure, disposal, and cancellation single-shot: output and completion must terminate exactly once.

### Compatibility and risk

An external non-conforming kernel that publishes parented output after `idle` may no longer have that output attached to
the completed execution. Validate real Deno behavior before merging. If a compatibility mode is genuinely required, it
must be explicit and testable, not a hidden duration heuristic.

### Focused verification

- Protocol tests for both reply/idle orderings and for output immediately before `idle`.
- A test proving completion has no clock-based minimum delay.
- Self-hosted wire-order assertion: busy, parented outputs, reply, then idle as required by the owned host contract.
- Real `deno jupyter` execution with multiple streamed outputs.

### Completion criteria

- No fixed settle timeout participates in execution correctness.
- Transport APIs expose transport concerns, not mailbox inspection used to guess protocol completion.
- All completion and output-stream terminal paths are deterministic and exactly once.

### Suggested commit boundary

One Jupyter Client commit; a separate Kernel commit if producer ordering also changes.

## Phase 2: Reconcile plugin MCP generations and harden child output

### Goal and invariants

Make the latest plugin registry snapshot authoritative. Unchanged MCP definitions reuse generations; replaced and
removed definitions retire after their last lease. Refresh work is serialized, cancellable by the manager lifetime, and
fully observed. Plugin process output can never block the child or grow host memory without bound.

### Expected files

- `Maieutics/Plugins/PluginHostManager.cs`
- `Maieutics/Plugins/PluginHostProcess.cs`
- `Maieutics/Mcp/McpServerGeneration.cs` only if a small lifecycle hook is missing
- `Maieutics.Jupyter.Tests/PluginHostIntegrationTests.cs`
- `Maieutics.Jupyter.Tests/McpServerGenerationTests.cs`

### Work

1. Give every registry update a monotonic revision and feed it to one owned refresh loop (or an equivalent serialized
   gate). Coalesce obsolete pending snapshots; never run overlapping unowned refresh tasks.
2. Discover definitions into a complete candidate snapshot keyed by server ID and generation key. Reject duplicate IDs
   deterministically and keep the previous valid snapshot when discovery of the candidate is unusable.
3. Construct all added/replaced generations before publication. Atomically swap the snapshot, then retire removed and
   replaced generations. A failed candidate must retire only its newly created generations.
4. Pass the manager lifetime token through extension invocation and generation creation. Re-throw expected
   cancellation; log other discovery failures without letting an older refresh overwrite a newer revision.
5. Drain stdout and stderr concurrently from process start. Log incrementally with per-line/per-entry truncation and a
   bounded retained tail for diagnostics; do not retain the entire lifetime output.
6. Make drain tasks owned and observed. Stop should cancel, terminate the owned process when required, await both drains
   and exit under one total timeout budget, and remain idempotent under concurrent disposal.

### Compatibility and risk

The definition ID remains the logical identity, while a generation key determines reuse. Do not terminate a generation
that still has an active run lease. Logs may become truncated by design; include an explicit truncation marker and never
log credentials or complete control payloads.

### Focused verification

- Add/unchanged/replace/remove reconciliation, including an active lease on the retired generation.
- Two registry revisions completing discovery out of order; only the newest candidate may publish.
- Candidate construction failure rolls back without damaging the last-known-good snapshot.
- Children that saturate stdout, stderr, and both streams exit without deadlock.
- Noisy long-lived output stays within the configured diagnostic bound.
- Concurrent stop/dispose observes every task and retires every generation exactly once.

### Completion criteria

- Dynamic generation count converges to the current authoritative definitions.
- No refresh uses `CancellationToken.None`, fire-and-forget task, or check-then-add lifecycle race.
- Process output memory is bounded and neither pipe can block waiting for the other to drain.

### Suggested commit boundary

Use two commits: generation reconciliation first, process supervision/output draining second.

## Phase 3: Bound control traffic and sanitize model discovery

### Goal and invariants

Treat all control traffic, provider messages, and rendered strings as untrusted. Oversized inputs fail early and
predictably. Cancellation crosses the discovery boundary unchanged. Notebook Markdown never interprets provider error
text or discovered identifiers as markup.

### Expected files

- `Maieutics/Control/ReplControlHost.cs`
- `Maieutics/Plugins/PluginHostManager.cs` for the plugin-side WebSocket reader
- the executable's control-channel option/constant owner, without creating another project
- `Maieutics/Configuration/MaieuticsRuntimeConfiguration.cs`
- `Maieutics/Jupyter/MaieuticsAgentKernelApplication.cs`
- `Maieutics.Jupyter.Tests/ReplControlHostTests.cs`
- `Maieutics.Jupyter.Tests/PluginHostIntegrationTests.cs`
- `Maieutics.Jupyter.Tests/MaieuticsConfigurationTests.cs`
- `Maieutics.Jupyter.Tests/AgentJupyterIntegrationTests.cs`

### Work

1. Introduce one named aggregate message/body limit shared by the HTTP and WebSocket entry points. The existing 256 KiB
   receive buffer is a chunk size, not a payload limit.
2. Count WebSocket bytes across fragments before growing storage. Close oversized messages with
   `MessageTooBig`; reject oversized HTTP requests with 413 before deserialization. Bound JSON depth as defense in
   depth.
3. Keep the limit constant initially unless a concrete deployment requirement justifies configuration. Document units
   and whether the bound applies to compressed or decoded bytes.
4. In model discovery, catch `OperationCanceledException` associated with the caller token separately and rethrow it.
   Log provider details internally, but return a stable typed/category error suitable for display instead of raw
   exception text.
5. Centralize the small amount of Markdown-safe rendering needed by the Jupyter adapter. Render identifiers as robust
   code spans and errors as escaped plain text; cover backticks, brackets, angle brackets, newlines, and link/image
   syntax. Do not mark untrusted HTML as trusted MIME.

### Compatibility and risk

Large payloads that previously consumed unbounded memory will now fail. Choose a documented default from observed
legitimate traffic and test boundary values exactly. Avoid turning every provider failure into one opaque message:
retain a stable category/correlation detail for support while keeping secrets and raw endpoints out of notebook output.

### Focused verification

- HTTP and single-frame/fragmented WebSocket payloads at limit, one byte over, and well over the limit.
- Correct close/status behavior without partial envelope dispatch.
- Caller cancellation produces a canceled task and is not cached as a discovery failure.
- Provider failure remains recoverable and does not expose raw exception details.
- Adversarial discovered models and failures render as inert Markdown text.

### Completion criteria

- Every control ingress has an aggregate byte bound and a deterministic error response.
- Discovery cancellation and provider failure are observably distinct.
- No untrusted discovered value is concatenated into executable Markdown syntax.

### Suggested commit boundary

Use two commits: control-channel bounds, then discovery cancellation/error rendering.

## Phase 4: Make acquisition and lifetimes explicitly asynchronous

### Goal and invariants

Remove sync-over-async from run-profile rollback and plugin-host access. Constructors and DI factories remain
side-effect free. A caller can explicitly release an abandoned Jupyter execution without implying a kernel-wide
interrupt.

### Expected files

- `Maieutics.Agent/AgentRunProfile.cs`
- `Maieutics.Agent/AgentSession.cs`
- `Maieutics/Configuration/MaieuticsRuntimeConfiguration.cs`
- `Maieutics/MaieuticsHost.cs`
- `Maieutics/Plugins/PluginHostManager.cs`
- `Maieutics/Plugins/PluginHostStartupHostedService.cs` (expected to disappear or become unnecessary)
- `Maieutics.Jupyter.Client/IJupyterExecution.cs`
- `Maieutics.Jupyter.Client/Protocol/JupyterExecution.cs`
- `Maieutics.Jupyter.Client/Protocol/JupyterProtocolSession.cs`
- corresponding Agent, configuration, Jupyter protocol, and host integration tests

### Work

1. Evolve `IAgentRunProfileProvider.Acquire()` to `AcquireAsync(CancellationToken)` returning
   `Task<IAgentRunProfileLease>`. Await MCP/plugin readiness and rollback; remove `GetAwaiter().GetResult()` from the
   acquisition path.
2. Preserve one-turn semantics: acquire one immutable profile lease before provider streaming, hold it for the run, and
   dispose it once on every success/failure/cancellation path. Do not hold configuration locks while awaiting.
3. Replace `Task<PluginHostManager>` plus `Func<PluginHostManager>` with one directly registered manager whose owner
   performs async startup and exposes explicit readiness to async consumers. The same instance must serve hosted
   startup, control routing, configuration acquisition, and disposal.
4. Keep startup failure visible to the generic host and process exit status. Do not convert host readiness into a
   service-locator lookup or mutable DI registration.
5. Make `IJupyterExecution` asynchronously disposable (or add an equivalently explicit abandon operation). Disposal is
   local detach: remove routing state, complete the output stream/result with a documented cancellation/abandon
   outcome, reject future stdin replies, and make late messages follow the Phase 1 policy.
6. Keep kernel interruption separate because Jupyter interrupt affects kernel execution globally. If a convenience API
   is later added, name that semantic distinction explicitly.

### Compatibility and risk

`IAgentRunProfileProvider` and `IJupyterExecution` are public API changes. Update XML documentation, test doubles, and
all call sites in one bounded migration. Do not retain synchronous wrappers that recreate the same blocking behavior.
Execution disposal must not silently send a global interrupt.

### Focused verification

- Cancellation while waiting for profile/plugin readiness.
- Partial lease acquisition failure awaits rollback of every acquired generation exactly once.
- Host startup failure and concurrent shutdown are observed without deadlock.
- Abandon before reply, after reply, while output is active, and concurrently with session disposal.
- Repeated execution disposal and late stdin reply are deterministic.

### Completion criteria

- No sync-over-async remains in run-profile acquisition or plugin-host DI access.
- One object owns plugin-host startup, readiness, refresh loops, process, and shutdown.
- Every started execution has an explicit terminal owner action: normal completion, failure, session disposal, or caller
  abandonment.

### Suggested commit boundary

Use three commits: asynchronous profile contract, plugin-host DI/lifecycle simplification, then Jupyter execution
disposal.

### Implementation record (2026-08-09)

- `IAgentRunProfileProvider` now exposes only `AcquireAsync(CancellationToken)`. The session reservation covers the
  readiness wait, configuration locks cover only generation selection/lease capture, and cancellation or construction
  failure asynchronously rolls back every captured lease.
- `PluginHostManager` is the directly registered hosted owner for startup, readiness, the dynamic MCP coordinator,
  process observation, and idempotent shutdown. Configuration and control routing receive that same instance; the
  task-shaped service, blocking factory, and separate startup hosted service were removed.
- `IJupyterExecution` is asynchronously disposable. Early disposal cancels only local output/completion routing,
  rejects later stdin replies, and classifies later parented IOPub messages through the existing late-output policy;
  it never sends a kernel interrupt.
- Focused and repository gates passed with Agent 57/57 and Jupyter/product 229/229 tests, a warning-free
  warnings-as-errors build, `osx-arm64` NativeAOT publish (apart from the existing AsyncIO `IL3053` aggregate warning),
  and published-process smoke coverage 4/4.

## Phase 5: Narrow APIs and close documentation/CI gaps

### Goal and invariants

Make public APIs advertise only implemented behavior, and make automated checks cover both implementation languages.
Keep compatibility cleanup separate from the correctness fixes above.

### Expected files

- `Maieutics.Agent/AgentModels.cs`
- `Maieutics.Agent/AgentSession.cs`
- `Maieutics.Agent.Tests/*` call sites and validation tests
- `.github/workflows/ci.yml`
- `deno/deno.json` and plugin-script task definitions only if task names need normalization
- `docs/architecture/README.md`
- `docs/architecture/decisions/0016-script-plugins-and-extension-points.md`
- any ADR links that refer to the duplicated number/title

### Work

1. Decide the current `AgentTurn` contract explicitly. The minimal option is a text value plus `FromText`; the broader
   option is a Maieutics-owned discriminated content union with validation. Do not expose arbitrary MEAI `AIContent`
   until binary/multimodal input has end-to-end transcript, provider, persistence, limit, and Jupyter tests.
2. If source compatibility matters, stage the narrowing with an obsolete factory/constructor and a documented removal
   point. Because `Maieutics.Agent` is internal and non-packable today, prefer the smaller honest API unless a real
   consumer is found.
3. Add Deno checks to CI after setup: formatting check, type/task check, and Deno tests using repository-defined tasks.
   Run them once per appropriate job rather than redundantly on every OS unless platform-specific behavior requires it.
4. Add ADR 0014 through 0016 to the architecture index. Correct the script-plugin document's internal duplicated
   “ADR 0015” title to ADR 0016 and verify incoming links.
5. Record any API migration and control-channel limit in the relevant user/developer documentation.

### Compatibility and risk

The `AgentTurn` change is the only intentional source-compatibility question in this phase. Make it a reviewed decision,
not incidental cleanup. CI task additions can expose pre-existing Deno failures; land mechanical formatting separately
from behavior changes.

### Focused verification

- Agent input validation and transcript round-trip for every supported input kind.
- Full Deno formatting/check/test tasks locally and in CI.
- Markdown link check or targeted inspection of all ADR index entries and titles.
- Standard .NET suite after the API migration.

### Completion criteria

- The Agent input API and actual supported inputs match.
- CI fails on Deno formatting, type/check, or test regressions.
- ADR numbers, titles, index entries, and links agree.

### Suggested commit boundary

Use separate commits for Agent API migration, Deno CI, and documentation corrections.

## Follow-up product features

These features become safer after the remediation phases and should not be mixed into them:

1. **`%status` notebook command.** Show selected profile/source, workspace, active Deno REPL generation, plugin/MCP
   readiness, and last configuration reload result. Use immutable snapshots and redact paths/credentials where needed.
2. **Safe tool-activity presentation.** Render call start/progress/result with stable correlation IDs and bounded,
   escaped summaries. Preserve structured results internally; the notebook renderer owns presentation.
3. **Artifact boundary.** Move large/binary results behind typed artifact references before adding multimodal `AgentTurn`
   input or richer plugin results. Version the representation and keep binary data binary until MIME projection.

Recommended order is `%status`, then tool-activity presentation, then artifact work. The first two improve operability of
the repaired lifecycles; artifacts require a separate architecture decision because they touch storage and durable
formats.

## Repository-wide verification gates

Run focused tests first for the phase being changed, then the standard acceptance gates:

```bash
dotnet test Maieutics.slnx
dotnet build Maieutics.slnx --no-restore -warnaserror
git diff --check
```

For Deno-owned files, also run the repository tasks equivalent to:

```bash
deno fmt --check deno
deno task --config deno/deno.json check
deno task --config deno/deno.json test
```

Run the supported-RID NativeAOT publish check only when executable composition, provider adapters, function runtime, or
other trimming/AOT-sensitive code changes. Protocol, process-lifetime, cancellation, and timing changes require their
focused real-process tests before the full suite.

## Definition of done for the remediation program

The program is complete when:

- execution completion contains no duration guess;
- dynamic MCP state converges to the latest plugin registry and retires replaced resources safely;
- plugin child output, control payloads, queues, and retained diagnostics are bounded;
- cancellation remains cooperative and distinguishable across discovery and acquisition;
- DI contains no task-shaped service or blocking async bridge for plugin readiness;
- callers can deterministically release executions and every background task is owned and observed;
- the Agent input API states only supported capabilities;
- .NET, Deno, documentation, and applicable NativeAOT checks pass; and
- each behavior or compatibility change is documented in its owning layer without altering the repository dependency
  direction.
