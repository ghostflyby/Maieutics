// Recursive node:worker_threads.Worker routing patch for the Maieutics
// bootstrap contract.
//
// Installed via the preload script (`node --require`):
//
//   - it captures the native node:worker_threads.Worker constructor;
//   - it replaces the `Worker` export of node:worker_threads with a routing
//     wrapper that sends every supported Worker through the shared wrapper
//     entry (node_worker_wrapper.mjs);
//   - it re-synchronizes the builtin ESM namespace with
//     syncBuiltinESMExports() so a later `import ... from "node:worker_threads"`
//     yields the SAME patched Worker;
//   - it patches globalThis.Worker only when the realm already exposes the native
//     node:worker_threads.Worker as that alias. Node 26.7.0 does not expose a
//     global alias, so the adapter does not create one.
//
// The wrapper entry installs this patch again (recursively) before importing
// the target module, so a nested Worker routes ITS child Workers through the
// wrapper too. A nested Worker inherits this preload through process.execArgv,
// so the child's node:worker_threads.Worker is already the patched constructor
// when its wrapper entry runs.
//
// Supported Worker options survive routing:
//   - name (including empty string) and execArgv;
//   - env, argv, workerData, transferList, resourceLimits,
//     stdin/stdout/stderr, trackUnmanagedFds.
//
// Explicitly unsupported (a typed error is thrown before any Worker is
// created):
//   - type: "classic" (Node has no classic-thread semantics);
//   - type: "commonjs" (the ESM wrapper cannot preserve an explicit CommonJS
//     interpretation for a target whose package format would otherwise differ);
//   - eval: true (string-code workers cannot participate in the bootstrap);
//   - a data: URL entry (string payloads are opaque, not resolvable targets).
//
// Every other unknown option is forwarded unchanged. Node itself silently
// ignores unknown options, so the adapter does not claim to validate options
// Node does not validate; it only refuses the forms it cannot route
// truthfully.
//
// The preload marker `maieutics.bootstrap` is installed on globalThis so a
// realm (main process or worker) can be recognized as preloaded. It carries
// only the non-sensitive bootstrap version. The wrapper entry and the tests
// read it.
//
// The adapter-added descriptor contains only the target and non-sensitive
// bootstrap markers. Caller-provided workerData is forwarded as caller-owned
// data and may itself contain arbitrary values.

"use strict";

const path = require("node:path");
const { pathToFileURL } = require("node:url");
const wt = require("node:worker_threads");

const contract = require("./node_bootstrap_contract.cjs");

/** The shared wrapper entry is always this module's sibling. */
const WRAPPER_PATH = path.join(__dirname, "node_worker_wrapper.mjs");
const WRAPPER_URL = pathToFileURL(WRAPPER_PATH).href;

/** The preload entry is always this module's sibling. */
const PRELOAD_PATH = path.join(__dirname, "node_worker_preload.cjs");
const PRELOAD_REQUIRE_ARG = `--require=${PRELOAD_PATH}`;

const BOOTSTRAP_VERSION = contract.BOOTSTRAP_VERSION;
const PROFILE_QUERY_KEY = contract.PROFILE_QUERY_KEY;

/** Preload marker key on globalThis (non-sensitive; diagnostics/tests only). */
const PRELOAD_MARKER = "maieutics.bootstrap";

const NativeWorker = wt.Worker;

/**
 * Reserved workerData keys. The routing wrapper passes
 * `{ [BOOTSTRAP_WORKER_DATA_KEY]: descriptor, [USER_WORKER_DATA_KEY]: original }`
 * and the wrapper entry restores the user's original workerData view before the
 * target module is imported, so the target observes exactly what the caller
 * passed.
 */
const BOOTSTRAP_WORKER_DATA_KEY = "__maieuticsWorkerBootstrap";
const USER_WORKER_DATA_KEY = "__maieuticsUserWorkerData";

let routed = false;

/**
 * Installs the recursive Worker routing patch on the current realm's
 * node:worker_threads.Worker export. Idempotent: only the first call in a
 * realm installs. Called by the preload script and again by the wrapper entry
 * (so a nested worker that entered through the wrapper is patched before its
 * target module imports node:worker_threads).
 */
function installNodeWorkerPatch(profile) {
  if (routed) return;
  routed = true;
  const Wrapped = createRoutingWorker(profile);
  wt.Worker = Wrapped;
  if (globalThis.Worker === NativeWorker) {
    globalThis.Worker = Wrapped;
  }
  try {
    // Redirect every user-visible `constructor` reference to the routed
    // constructor. The native prototype's `constructor` is writable and
    // configurable (verified on Node 26.7.0), so `instance.constructor`,
    // `Object.getPrototypeOf(instance).constructor`, and
    // `Object.getPrototypeOf(Worker.prototype).constructor` all resolve to
    // RoutingWorker — the instance and prototype-parent chains are closed.
    // The native prototype object itself
    // (Object.getPrototypeOf(instance)) stays reachable but its `constructor`
    // is rewritten and it cannot construct Workers.
    Object.defineProperty(NativeWorker.prototype, "constructor", {
      value: Wrapped,
      writable: true,
      configurable: true,
    });
  } catch {
    // Non-configurable on some host: the native constructor stays reachable
    // through the instance chain (documented residual exposure).
  }
  try {
    // Re-sync the ESM builtin namespace so `import { Worker } from
    // "node:worker_threads"` and `import * as wt from "node:worker_threads"`
    // observe the same patched constructor after a preload patch.
    require("node:module").syncBuiltinESMExports();
  } catch {
    // If the sync is unavailable the CJS export path is still patched; the
    // ESM namespace stays on the native constructor (documented limitation).
  }
}

function createRoutingWorker(profile) {
  function RoutingWorker(filename, options) {
    return routeConstruction(filename, options, profile);
  }
  // The routing prototype inherits the native instance methods but points its
  // own `constructor` at RoutingWorker, so `Worker.prototype.constructor` does
  // not expose the native constructor. `instanceof` is preserved through
  // Symbol.hasInstance because real Worker instances keep the native
  // prototype, which has no RoutingWorker.prototype on its chain. Combined
  // with the install-time rewrite of the native prototype's `constructor`
  // slot, every user-visible `constructor` reference resolves to RoutingWorker
  // (verified: `Worker.prototype.constructor`, `instance.constructor`,
  // `Object.getPrototypeOf(instance).constructor`, and
  // `Object.getPrototypeOf(Worker.prototype).constructor` all return the
  // routing function). The native constructor itself lives only inside this
  // module's closure.
  const routingPrototype = Object.create(NativeWorker.prototype, {
    constructor: {
      value: RoutingWorker,
      enumerable: false,
      writable: true,
      configurable: true,
    },
  });
  RoutingWorker.prototype = routingPrototype;
  Object.defineProperty(RoutingWorker, Symbol.hasInstance, {
    value: (value) => value instanceof NativeWorker,
  });
  return RoutingWorker;
}

function routeConstruction(filename, options, profile) {
  const opts = options === undefined || options === null ? {} : options;
  if (opts.type === "classic") {
    throw new DOMException(
      "Classic workers are not supported by the Maieutics runtime; use a module worker.",
      "NotSupportedError",
    );
  }
  if (opts.type === "commonjs") {
    throw new DOMException(
      "CommonJS workers are not supported by the Maieutics runtime; use a module worker.",
      "NotSupportedError",
    );
  }
  if (opts.type !== undefined && opts.type !== "module") {
    throw new DOMException(
      `Worker type '${String(opts.type)}' is not supported by the Maieutics runtime.`,
      "NotSupportedError",
    );
  }
  if (opts.eval) {
    throw new DOMException(
      "eval workers are not supported by the Maieutics runtime; the entry must be a file/URL.",
      "NotSupportedError",
    );
  }
  if (filename instanceof URL) {
    if (filename.protocol === "data:") {
      throw new DOMException(
        "data: worker entries are not supported by the Maieutics runtime.",
        "NotSupportedError",
      );
    }
    if (
      filename.protocol !== "file:" && filename.protocol !== "http:" &&
      filename.protocol !== "https:"
    ) {
      throw new DOMException(
        `Unsupported Worker entry protocol '${filename.protocol}'; expected a file or HTTP(S) URL.`,
        "NotSupportedError",
      );
    }
  } else if (typeof filename !== "string") {
    throw new DOMException(
      "Worker entry must be a string path or a file/HTTP(S) URL.",
      "NotSupportedError",
    );
  } else if (/^data:/i.test(filename)) {
    throw new DOMException(
      "data: worker entries are not supported by the Maieutics runtime.",
      "NotSupportedError",
    );
  }

  const target = toTargetUrl(filename);
  const wrapperUrl = buildWrapperUrl(WRAPPER_URL, profile, BOOTSTRAP_VERSION);
  const forwarded = forwardOptions(opts, {
    targetUrl: target,
    version: BOOTSTRAP_VERSION,
    profile,
  });
  // Node only accepts file:/http(s): URLs as URL objects; a string with a
  // `file://` scheme is rejected with ERR_WORKER_PATH.
  return new NativeWorker(new URL(wrapperUrl), forwarded);
}

/**
 * Builds the wrapper module URL for a target. Only the non-sensitive profile
 * and version markers are encoded; the target travels as workerData (Node
 * strips the query from import.meta.url in workers).
 */
function buildWrapperUrl(wrapperUrl, profile, version) {
  const url = new URL(wrapperUrl);
  url.searchParams.set(PROFILE_QUERY_KEY, profile);
  url.searchParams.set("maieuticsVersion", String(version));
  return url.href;
}

/** Converts a Worker entry (string path or URL) to an absolute file URL. */
function toTargetUrl(filename) {
  if (filename instanceof URL) return filename.href;
  const raw = String(filename);
  if (/^file:|^https?:/.test(raw)) return raw;
  return pathToFileURL(path.resolve(raw)).href;
}

/**
 * Forwards the supported Worker options unchanged, pins the wrapper runtime to
 * module ESM (the wrapper entry is an .mjs file), and injects the bootstrap
 * descriptor into workerData. The target module's own format (CommonJS or
 * ESM) is resolved from its file and package metadata. An explicit
 * `type: "commonjs"` Worker request is rejected before this function runs
 * because the wrapper cannot preserve that override for the target. The
 * user's original workerData is preserved under a reserved key so the wrapper
 * entry can restore it before the target is imported.
 *
 * A worker realm must always start with the Maieutics preload. The wrapper
 * entry installs the patch anyway, but a hostile --require/--import that runs
 * BEFORE the wrapper (via user-provided execArgv or env.NODE_OPTIONS) could
 * capture the native constructor; prepending our preload makes it run first,
 * so the realm is patched before any user preload executes.
 */
function forwardOptions(opts, descriptor) {
  const forwarded = { type: "module" };
  for (const key of WORKER_OPTION_KEYS) {
    if (opts[key] !== undefined) forwarded[key] = opts[key];
  }
  if (forwarded.execArgv !== undefined) {
    forwarded.execArgv = ensurePreloadFirst(forwarded.execArgv);
  }
  if (
    forwarded.env !== undefined && forwarded.env !== null &&
    typeof forwarded.env === "object" &&
    forwarded.env.NODE_OPTIONS !== undefined
  ) {
    forwarded.env = {
      ...forwarded.env,
      NODE_OPTIONS: ensurePreloadFirst(String(forwarded.env.NODE_OPTIONS)),
    };
  }
  forwarded.workerData = {
    [BOOTSTRAP_WORKER_DATA_KEY]: descriptor,
    [USER_WORKER_DATA_KEY]: opts.workerData,
  };
  return forwarded;
}

/**
 * Ensures the Maieutics preload is the FIRST preload in a worker's startup
 * arguments. Accepts an execArgv array or a NODE_OPTIONS string (both are
 * used by Node at worker startup). Idempotent: if the preload is already
 * present, the value is returned unchanged; otherwise it is prepended. The
 * NODE_OPTIONS string is only prefixed, never split, so quoted arguments the
 * user may have written are preserved.
 */
function ensurePreloadFirst(argv) {
  if (Array.isArray(argv)) {
    if (argvContainsPreload(argv)) return argv;
    return [PRELOAD_REQUIRE_ARG, ...argv];
  }
  const value = String(argv);
  if (value.includes(PRELOAD_PATH)) return value;
  return value.length === 0 ? PRELOAD_REQUIRE_ARG : `${PRELOAD_REQUIRE_ARG} ${value}`;
}

/** True when an execArgv array already contains the Maieutics preload. */
function argvContainsPreload(argv) {
  return argv.some((entry, index) =>
    entry === PRELOAD_PATH || entry === PRELOAD_REQUIRE_ARG ||
    (entry === "--require" && argv[index + 1] === PRELOAD_PATH)
  );
}

/** Worker options the adapter forwards unchanged. */
const WORKER_OPTION_KEYS = [
  "name",
  "execArgv",
  "env",
  "argv",
  "workerData",
  "transferList",
  "resourceLimits",
  "stdin",
  "stdout",
  "stderr",
  "trackUnmanagedFds",
];

module.exports = {
  installNodeWorkerPatch,
  WRAPPER_URL,
  PROFILE_QUERY_KEY,
  BOOTSTRAP_VERSION,
  PRELOAD_MARKER,
  BOOTSTRAP_WORKER_DATA_KEY,
  USER_WORKER_DATA_KEY,
};
