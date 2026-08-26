# Runtime Bootstrapping and Worker Patching Design

Status: Implemented and verified in the research branch

Date: 2026-08-25

## Purpose

This document defines how Maieutics should initialize and patch Deno and Node execution contexts when the runtime creates nested Workers. It records the result of the preload research and separates the shared bootstrap mechanism from the different runtime surfaces of the REPL and Extension Host.

This is a standalone design document, not a new ADR. It does not reserve or consume the missing ADR 0019 number. It describes the current Maieutics implementation direction: the Aves-backed REPL compatibility layer and the out-of-process Deno Extension Host. The official `deno jupyter` behavior is included only as a runtime compatibility fact.

## Context

Maieutics has several related but different execution contexts:

- the executable starts supervised Deno processes;
- the Extension Host is a trusted Deno orchestration process;
- the Extension Host starts narrowed plugin Workers;
- the REPL process starts a Worker that owns user evaluation and the Aves kernel;
- user or plugin code may attempt to create further Workers.

A patch installed on one `globalThis` does not automatically appear in another Worker. Deno Workers run in separate threads, V8 isolates, and global scopes. A child Worker therefore receives its own native APIs even when the parent has already installed Maieutics globals or replaced a constructor.

The concern is runtime consistency, not transmission of the parent's object graph. A nested Worker must not silently become an uninitialized Maieutics execution context when the product requires the Worker to participate in the same bootstrap contract. JavaScript patching is not a replacement for process isolation or other policy enforcement; those concerns are outside this document.

## Runtime findings

### Deno process preload

Deno 2.9.5 provides the following `deno run` startup options:

```text
deno run --preload <FILE> main.ts
deno run --require <FILE> main.ts
```

`--preload` executes files before the main module. `--require` provides the corresponding CommonJS startup mechanism. In Deno 2.9.5, `--import` is a hidden alias for `--preload`, but Maieutics should use the canonical `--preload` spelling so the contract is not confused with import maps or Node's `--import` option.

This mechanism belongs to the CLI main Worker. It is not a general Worker preload facility.

### Deno Jupyter

Deno 2.9.5 does not expose `--preload`, `--import`, or `--require` on `deno jupyter`; each is rejected as an unexpected argument. The internal Jupyter bootstrap is an implementation detail of the Deno CLI, not a public extension hook. `deno.json` also has no documented preload field; import maps affect resolution only and do not execute initialization code.

Maieutics therefore must not make its runtime contract depend on a stock `deno jupyter` preload flag. A direct integration with that command would require an external wrapper, a custom kernelspec, or explicit first-cell initialization. First-cell initialization is not sufficient for mandatory runtime patching because user code could capture an original API before that cell runs.

The current Maieutics REPL is the Aves-backed compatibility layer described in `docs/deno-jupyter-compat.md`, not a direct stock `deno jupyter` kernel. Its own process and Worker entries remain the controlled initialization points.

### Deno Workers

Deno's user-facing Worker API is module-only in the supported runtime. Deno 2.9.5 rejects `type: "classic"` with `NotSupportedError: Classic workers are not supported`.

There is no Worker equivalent of `--preload`. A Worker must enter through an explicit wrapper or bootstrap module if it needs initialization before the target module is evaluated. Parent preload modules, loader hooks, and `globalThis` mutations do not automatically execute in the child isolate.

Deno Worker permission options are not part of this design. The bootstrap must preserve the caller's Worker construction semantics and must not be used as a policy mechanism.

### Node preload and Workers

Node provides `--require`, `--import`, and loader-related startup mechanisms. Node Workers can receive `execArgv`, and normally inherit the parent process's `process.execArgv`. These facilities are useful for process startup, but they are not an enforceable Worker bootstrap boundary:

- a caller can provide a different `execArgv`;
- a caller can clear `execArgv`;
- a caller can import `node:worker_threads` directly;
- a global `Worker` replacement does not replace the `Worker` constructor exported by `node:worker_threads`.

Node support therefore requires an explicit Worker creation path for `node:worker_threads.Worker` in addition to any global alias. Node preload options may supplement startup, but they cannot be the only mechanism on which the Maieutics contract depends.

## Decision

### 1. Define one Maieutics bootstrap contract, with runtime-specific adapters

Maieutics should provide one conceptual bootstrap contract and separate adapters for process entry and Worker entry. It should not try to force one CLI option to cover `deno run`, `deno jupyter`, Deno Workers, and Node Workers.

The proposed executable-owned Deno-side module is:

```text
deno/maieutics-runtime/
    bootstrap_contract.ts
    worker_bootstrap.ts
    worker_factory.ts
    worker_patch.ts
```

The exact file split may change during implementation, but the ownership boundary should remain. This is an internal runtime module, not part of the reusable Jupyter assemblies and not part of the public plugin-author SDK.

The shared module owns:

- bootstrap version and target resolution;
- the wrapper entry used before the target module is imported;
- recursive Worker construction;
- propagation of Worker construction options that are not bootstrap metadata;
- bootstrap failure and Worker death propagation;
- non-sensitive runtime profile/version markers.

It must not own REPL control protocol, Aves initialization, plugin actor semantics, extension point discovery, or runtime-specific global injection.

### 2. Use explicit Worker wrappers and recursive construction

Every Worker that is required to participate in Maieutics runtime initialization
enters through a controlled entry. Root REPL and plugin Workers enter directly
at their product-owned entry modules so Deno can statically analyze their bare
specifier graph under the existing narrowed Worker setup. Those entries install
the shared patch before profile initialization. Nested Workers enter through the
shared wrapper recursively:

```text
controlled root entry or shared Worker wrapper
    -> install shared Worker patch
    -> resolve target module
    -> import target module
```

The Worker factory creates the wrapper Worker and passes the target as bootstrap metadata. The target, version, and profile may be represented in a controlled module URL or equivalent internal descriptor. Credentials, HMAC keys, connection-file contents, and other sensitive values must not be encoded into that URL or passed through a text bootstrap payload.

The wrapper installs the shared Worker patch before importing the target. The patch retains the original constructor for internal use and replaces the product-visible construction path with a factory that routes child module Workers through the same wrapper. The original constructor is used exactly once for each controlled creation and the wrapper must not recursively wrap itself.

The factory must preserve the caller's relevant Worker semantics, including:

- `type`;
- `name`;
- the complete `deno` option where the runtime exposes it;
- other platform options and transfer behavior that are part of the supported API.

The bootstrap may change only the script URL and its own internal metadata. It must not silently widen permissions, change a Worker from module to classic, or alter actor handshake behavior.

The Deno contract is explicitly module Worker-only. A request for a classic Worker is a typed unsupported operation, even though current Deno rejects that form itself.

#### Constructor redirection, not full removal

The patch replaces the user-visible constructor binding (`globalThis.Worker`,
the `node:worker_threads.Worker` export) with a plain routing function, and
redirects every user-visible `constructor` reference to it:

- `Worker.prototype.constructor` — a plain function's own `prototype` property
  is writable in Node, and the native prototype's `constructor` slot is
  writable+configurable in both runtimes (verified on Node 26.7.0 and Deno
  2.9.5);
- a real instance's `constructor` (`instance.constructor`,
  `Object.getPrototypeOf(instance).constructor`) — the native prototype's
  `constructor` is rewritten to the routing function;
- the prototype parent's `constructor` (`Object.getPrototypeOf(
  Worker.prototype).constructor`).

The native constructor itself is never exposed: `new native(...)` is the only
way to create a real Worker with its internal thread/isolate slots, so it lives
only inside the patch closure. What remains reachable is the native prototype
object itself (`Object.getPrototypeOf(instance)`); its `constructor` slot is
rewritten and it cannot construct Workers. An independent audit confirmed the
native constructor is unreachable from user code in a patched realm on the
default path (Deno side is fully closed; Node side is closed after the
constructor redirection).

A worker realm must always start with the Maieutics preload. Node callers can
provide `execArgv` or `env.NODE_OPTIONS`; the adapter PREPENDS the Maieutics
preload to both so it runs before any user preload, preventing a hostile
`--require`/`--import` from capturing the native constructor or creating an
uninitialized descendant realm (verified by a black-box regression scenario
that stashes the constructor via both vectors and spawns a grandchild through
it). This is a targeted defense against accidental or incidental preload
conflicts, not a guarantee against a caller who fully controls the worker start
options and deliberately clears the patch — that is a caller-deliberate escape,
outside the default-path contract. A realm that was never under Maieutics
control (no preload, no wrapper entry) is likewise outside this contract, and
closing that requires a separate realm/isolate/process — the permission
architecture's boundary.

### 3. Support Node's `node:worker_threads` constructor explicitly

Node support is part of the shared design, not a follow-up option. Patching only `globalThis.Worker` is insufficient.

The Node adapter supports `node:worker_threads.Worker` directly and synchronizes
CommonJS and later ESM builtin imports. On Node 26.7.0, `globalThis.Worker` is
not exposed; if a future host provides that exact native constructor as a global
alias, the adapter patches the alias too without creating a new global.
The current wrapper supports module Workers and target format resolution from
file/package metadata. An explicit `type: "commonjs"` request is rejected
because the ESM wrapper cannot preserve that override for the target. The
adapter-added workerData descriptor contains only the target and non-sensitive
bootstrap markers; caller-provided workerData remains caller-owned data and is
forwarded unchanged.


If Node code imports the builtin module through an unmodifiable or deliberately bypassed path, a JavaScript-level patch cannot guarantee interception. Such a case must be treated as outside the runtime patch contract rather than described as isolated by the patch.

### 4. Keep REPL and Extension Host runtime profiles separate

The bootstrap mechanics are shared, but the post-bootstrap runtime profiles are different.

#### Shared mechanics

REPL and plugin Workers may share:

- wrapper entry resolution;
- initialization ordering before target import;
- recursive Worker routing;
- construction-option propagation;
- failure and death propagation;
- version/profile identification without secrets.

#### REPL profile

`deno/maieutics-deno-repl/repl_worker.ts` remains responsible for the user execution surface. Its initialization order must remain:

```text
install shared Worker bootstrap
installMaieuticsNamespace()
installHostEnvironment()
createReplKernel()
```

The REPL-specific surface includes:

- `globalThis.maieutics` and its comm proxy;
- captured `console` output;
- blocking `prompt`, `confirm`, and `alert` through the input mailbox;
- `Deno.jupyter` display, update, clear, and comm behavior;
- Aves `createReplKernel()` and eval execution.

These capabilities are root REPL capabilities. They must not be copied into every nested Worker as ambient globals. A nested Worker that needs a REPL service should receive an explicit actor or `MessagePort` capability through the existing product protocol.

#### Extension Host and plugin profile

`deno/maieutics-plugin-host/worker_entry.ts` remains the plugin-specific entry. It must install the shared Worker bootstrap before `initPluginWorker()` loads the plugin entry. The plugin profile owns:

- the host configuration handshake;
- plugin identity;
- dependency actor acquisition;
- `serveWorker` and actor registration;
- plugin export loading;
- extension-point discovery and lifecycle.

Plugin Workers must not receive `Deno.jupyter`, REPL input functions, the REPL comm proxy, or an implicit REPL actor surface. The Extension Host itself remains a trusted orchestration process and does not receive the REPL user globals merely because it derives a REPL process.

#### Process entries

`deno/maieutics-deno-repl/process_main.ts` remains a process actor entry. It starts the host-derived process RPC surface and waits for the host to initialize and start the REPL. It is not the user execution Worker and must not install the REPL user globals.

`deno/maieutics-repl-client/mod.ts` remains a control-plane client. It owns IPC/eval/comm protocol behavior and does not own Worker patching.

### 5. API patch scope

The API decisions are:

| API | Decision | Scope |
|---|---|---|
| Deno `Worker` | Mandatory recursive patch target | REPL and plugin Worker profiles |
| Node `node:worker_threads.Worker` | Mandatory explicit adapter | Node execution profile |
| `globalThis.Worker` | Patch only as an alias to the controlled adapter | Where the host exposes it |
| `BroadcastChannel` | Explicitly out of scope | Not considered by this design |
| `console` | Keep the existing runtime-specific patch | REPL root only |
| `prompt`, `confirm`, `alert` | Keep the existing mailbox-backed patch | REPL root only |
| `Deno.jupyter` | Keep the existing runtime-specific patch | REPL root only |
| `globalThis.maieutics` | Keep the existing runtime-specific injection | REPL root only |
| entire `globalThis` | Do not replace | All profiles |
| ECMAScript intrinsics, `eval`, `Function`, timers, crypto, WebAssembly | Do not patch | All profiles |
| unrelated Web and Deno APIs | Do not patch in this design | All profiles |

Permission APIs and policy enforcement are intentionally outside the scope of this document. This design does not use JavaScript patching as a substitute for those mechanisms.

The practical consequence is that `Worker` is the first mandatory generic patch because it creates a new execution context. Other APIs remain profile-specific only when they are part of an existing Maieutics surface.

## Ownership and integration points

The implementation should remain in the executable-owned Deno runtime area and adapt the following existing locations:

| Location | Responsibility after implementation |
|---|---|
| `deno/maieutics-runtime/` | Shared bootstrap contract, wrapper, factory, and Worker patch |
| `deno/maieutics-deno-repl/repl_actor.ts` | Create the REPL user Worker through the shared factory |
| `deno/maieutics-deno-repl/repl_worker.ts` | Install the REPL profile after shared bootstrap, before Aves |
| `deno/maieutics-plugin-host/host.ts` | Create plugin Workers through the shared factory and preserve plugin options |
| `deno/maieutics-plugin-host/worker_entry.ts` | Install the plugin profile before plugin entry loading |
| `deno/maieutics-deno-repl/process_main.ts` | Keep process actor startup separate from user Worker initialization |
| `deno/maieutics-repl-client/mod.ts` | Keep control-plane protocol responsibilities unchanged |
| `Maieutics/DenoRepl/DenoReplProcess.cs` | Optionally add process-level `--preload` where useful; do not treat it as Worker preload |
| `Maieutics/DenoRepl/DenoReplSessionFactory.cs` | Keep kernel-derived and host-derived sessions on the same REPL Worker contract |
| `Maieutics/DenoRepl/DenoReplModule.cs` | Materialize and embed the new Deno runtime files |

If the module is embedded into the executable, the implementation must update all three materialization surfaces together:

1. the Deno files and `deno.json` imports/exports;
2. `Maieutics/Maieutics.csproj` embedded resources;
3. `Maieutics/DenoRepl/DenoReplModule.cs` `Entries`.

The shared runtime module should not be added to `Maieutics.Jupyter.Shared`, `Maieutics.Jupyter.Client`, `Maieutics.Jupyter.Kernel`, or the public plugin SDK. It is product-owned runtime composition.

## Lifecycle and failure semantics

Bootstrap is part of Worker startup, not an optional best-effort decoration.

- The wrapper must install the patch before importing the target.
- A bootstrap import or initialization failure must surface as Worker startup failure.
- The actor owner must observe the Worker death and fail the corresponding operation or generation exactly once.
- Cancellation and disposal must terminate the wrapper Worker and any target Worker it owns; no detached nested Worker may survive a failed actor generation.
- A nested Worker must not be considered ready until its wrapper has completed initialization and the target module has entered its normal actor handshake.
- The existing REPL and plugin host ownership trees remain responsible for observing background failures and completing shutdown.

These rules keep bootstrap failures compatible with the existing REPL generation, actor, and process lifecycle semantics.

## Verification plan

Implementation should add focused tests before broad integration runs.

### Deno Worker tests

- A wrapper installs the shared patch before the target module's top-level code runs.
- A nested Deno module Worker is routed through the wrapper recursively.
- Worker `type`, `name`, and supported construction options survive wrapping.
- A classic Worker request fails with the typed unsupported result expected by the adapter.
- A target import failure is observed by the actor owner and does not leave a live child Worker.
- REPL-specific globals are not ambiently copied into a nested Worker.
- The shared bootstrap runs before `createReplKernel()` and before plugin entry loading.

### Node Worker tests

The standalone Node adapter now verifies:

- `node:worker_threads.Worker` uses the controlled adapter and reaches the wrapper entry;
- later ESM named imports observe the patched builtin constructor;
- static ESM `workerData` observes the caller's original value after wrapper restoration;
- nested Workers route recursively and install the marker before target top-level code;
- supported name and workerData options survive routing;
- classic, explicit CommonJS, eval, and data URL forms fail with typed errors;
- target startup failures surface through the Worker error/exit surface;
- both `--require` and `--import` preload modes work.

Node 26.7.0 does not expose `globalThis.Worker`; the adapter does not create a
new global. If a host already aliases that exact native constructor globally,
the alias is synchronized with the controlled adapter.

### Product integration tests

Extend the existing REPL and host-derived coverage in:

- `deno/maieutics-deno-repl/repl_test.ts`;
- `deno/maieutics-deno-repl/repl_sync_input_test.ts`;
- `Maieutics.Jupyter.Tests/DenoReplSessionTests.cs`;
- `Maieutics.Jupyter.Tests/DenoReplHostDeriveTests.cs`.

The tests should compare kernel-derived and host-derived REPL behavior, exercise nested Worker creation, verify bootstrap failure during generation startup, and verify concurrent disposal does not retain child Workers.

Deno module changes should be checked with `deno check`, `deno fmt`, `deno lint`, and `deno test`. The Node adapter scenarios run through the root `deno.json` `test` task (`node maieutics-node-runtime/node_worker_patch_test_runner.cjs`); the Node runtime files are included in the root `deno check` task. The repository-level .NET checks remain separate from this design document and are not run as part of the documentation-only change unless implementation begins.

## Consequences

- There is one conceptual Maieutics bootstrap contract, but no misleading universal preload flag.
- Deno process startup can use `--preload` where the process entry is under Maieutics control; controlled root Workers use direct product-owned entries and nested Workers use the explicit shared wrapper/factory.
- Node Worker support is explicit for `node:worker_threads.Worker`; an existing matching global alias is synchronized when exposed, rather than relying on a global-only monkey patch.
- REPL and Extension Host share mechanics without sharing ambient capabilities or lifecycle responsibilities.
- The design keeps `BroadcastChannel` out of scope as requested.
- The design does not change permission architecture or claim that JavaScript patching is a security boundary.
- The first implementation can remain internal to the executable and can evolve without creating a new reusable Jupyter or plugin SDK dependency.

## References

- `docs/architecture/decisions/0003-deno-jupyter-repl-output-bridge.md`
- `docs/architecture/decisions/0004-deno-extension-protocol.md`
- `docs/architecture/decisions/0014-deno-repl-ipc-and-http-control.md`
- `docs/architecture/decisions/0018-declarative-permission-store-and-deno-execution-module.md`
- `docs/architecture/decisions/0020-repl-extension-host-actor-boundary.md`
- `docs/deno-jupyter-compat.md`
- `deno/maieutics-deno-repl/repl_actor.ts`
- `deno/maieutics-deno-repl/repl_worker.ts`
- `deno/maieutics-plugin-host/host.ts`
- `deno/maieutics-plugin-host/worker_entry.ts`
- Deno 2.9.5 `deno run --preload` and `--require` documentation and CLI source
- Deno 2.9.5 Worker and `deno jupyter` documentation and runtime source
- Node 26.7.0 CLI and `worker_threads` documentation
