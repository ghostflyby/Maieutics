# Maieutics Node Runtime

Node-side adapter for the shared Maieutics bootstrap contract
(`docs/runtime-bootstrapping-design.md`, decision 3). It provides a controlled
`node:worker_threads.Worker` path so Workers that must participate in Maieutics runtime
initialization enter through the shared wrapper.

## Layout

| File                                | Role                                                                                                                                               |
| ----------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `node_worker_preload.cjs`           | Preload-installable entry (`node --require` or `node --import`). Patches `node:worker_threads.Worker`, syncs builtin ESM exports, marks the realm. |
| `node_worker_patch.cjs`             | Recursive Worker routing patch. Captures the native constructor, routes supported Workers through the wrapper, rejects unsupported forms.          |
| `node_worker_wrapper.mjs`           | Shared wrapper entry. Installs the patch + marker, then imports the target module.                                                                 |
| `node_bootstrap_contract.cjs`       | Node-side mirror of `deno/maieutics-runtime/bootstrap_contract.ts` (marker symbol + query keys + version).                                         |
| `node_worker_patch_test_runner.cjs` | Test runner (spawns real `node` children per scenario).                                                                                            |
| `test_fixtures/`                    | Scenarios and fixtures (checked in, no temporary files).                                                                                           |

The adapter is STANDALONE: it never imports a Deno module, and Deno code never imports it. It shares
only the marker symbol (`Symbol.for("maieutics/bootstrap/v1")`) and the wrapper-URL query keys with
the Deno side.

## Installation

```bash
# CommonJS preload (runs before the main module's first require)
node --require deno/maieutics-node-runtime/node_worker_preload.cjs main.js

# ESM preload (the file is CommonJS and runs at require time)
node --import deno/maieutics-node-runtime/node_worker_preload.cjs main.mjs
```

After the preload, both `const { Worker } = require("node:worker_threads")` and
`import { Worker } from "node:worker_threads"` return the patched constructor.

## Behavior

- Every supported module Worker creation routes through `node_worker_wrapper.mjs`, which installs
  the patch + marker BEFORE importing the target module.
- The adapter patches `node:worker_threads.Worker` and synchronizes later ESM named imports. If a
  runtime already exposes that exact native constructor as `globalThis.Worker`, the alias is patched
  too. Node 26.7.0 has no global `Worker`, so the adapter does not create one.
- Nested Workers (created by target code) are routed recursively; the child realm inherits the
  preload through `process.execArgv`.
- Supported options are forwarded unchanged: `name`, `execArgv`, `env`, `argv`, `workerData`,
  `transferList`, `resourceLimits`, `stdin`, `stdout`, `stderr`, `trackUnmanagedFds`.
- Unsupported forms are rejected with a `DOMException` (`name` `NotSupportedError`) before any
  Worker is created: `type: "classic"`, `type: "commonjs"`, `eval: true`, and `data:` entries.
- Unknown options are forwarded as-is (Node itself ignores unknown options, so the adapter does not
  claim to validate what Node does not validate).
- The target descriptor travels as `workerData` (Node strips the query string from `import.meta.url`
  inside a worker). Wrapper URLs carry only the non-sensitive profile/version markers.
  Caller-provided `workerData` is forwarded unchanged; the adapter-added descriptor itself contains
  no credentials or other secrets.

## Tests

```bash
node deno/maieutics-node-runtime/node_worker_patch_test_runner.cjs
```

The runner spawns a fresh `node --require <preload>` child per scenario so each observes a clean
process state. Scenarios cover the named-import patch, the ESM namespace sync, nested marker
installation, options/name propagation, unsupported-form rejection, and startup-error behavior.

## Limitations

- A caller can bypass the patch with an explicit `execArgv` that omits the preload, or by clearing
  `execArgv`. This is inherent to preload-only startup and is documented in
  `docs/runtime-bootstrapping-design.md` as a limitation, not a supported bypass.
- The wrapper entry is an ESM `.mjs` file, so routed workers are created with `type: "module"` for
  the wrapper. Targets use their file and package metadata for CommonJS or ESM resolution. An
  explicit `type: "commonjs"` request is rejected because the wrapper cannot preserve that override
  for the target.
- Node's `--import` runs after `--require`; install this file with `--require` when both mechanisms
  are used.
- `BroadcastChannel` and permissions are out of scope for this adapter.
