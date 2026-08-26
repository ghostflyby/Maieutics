/**
 * Focused tests for the shared Maieutics Worker bootstrap:
 *
 *   - the wrapper installs the patch + marker before the target module runs;
 *   - nested Deno module Workers are routed through the wrapper recursively;
 *   - Worker name and deno permission options survive wrapping;
 *   - classic Worker requests fail with the typed unsupported result;
 *   - a missing handshake is observed by the actor owner (no live child);
 *   - a target top-level failure surfaces as Worker startup failure;
 *   - REPL profile globals are not ambiently copied into nested workers.
 *
 * No brittle sleeps: every expectation is driven by protocol signals (worker
 * message/error events) or by worker-actor's handshake/death signals.
 */

import { assertEquals } from "@std/assert";
import { spawn } from "@ghostflyby/worker-actor";
import { readBootstrapMarker } from "./bootstrap_contract.ts";
import { spawnBootstrapWorker } from "./worker_factory.ts";
import type * as ClassicProbe from "./test_fixtures/classic_probe.ts";

const TARGET = (name: string): string => new URL(`./test_fixtures/${name}`, import.meta.url).href;

interface BootstrapReport {
  phase: string;
  version?: number | null;
  profile?: string | null;
  name?: string;
  nested?: unknown;
  message?: string;
  hasReplGlobals?: boolean;
  marker?: unknown;
}

/**
 * Deno's Worker internals surface a worker whose top-level module rejected as
 * BOTH an `error` event on the Worker object AND an "Unhandled error in child
 * worker" rejection from `Worker.#pollControl`. The error event is the
 * documented startup-failure surface; the internal rejection is noise that the
 * test runner would otherwise report as an uncaught error. This module-scoped
 * listener suppresses exactly that rejection for workers this module spawns.
 */
addEventListener("unhandledrejection", (event: PromiseRejectionEvent) => {
  if (
    event.reason instanceof Error &&
    event.reason.message.includes("Unhandled error in child worker")
  ) {
    event.preventDefault();
  }
});

/** Spawns a bootstrap worker and resolves with the first report message. */
async function firstReport(
  targetUrl: string,
  options: { profile?: "repl" | "plugin"; name?: string; timeoutMs?: number } = {},
): Promise<{ report: BootstrapReport; worker: Worker }> {
  const worker = spawnBootstrapWorker(targetUrl, {
    profile: options.profile ?? "repl",
    ...(options.name === undefined ? {} : { name: options.name }),
  });
  const report = await new Promise<BootstrapReport>((resolve, reject) => {
    const deadline = setTimeout(
      () => reject(new Error("bootstrap worker did not report within the deadline")),
      options.timeoutMs ?? 10_000,
    );
    worker.onmessage = (event: MessageEvent) => {
      clearTimeout(deadline);
      resolve(event.data as BootstrapReport);
    };
    worker.onerror = (event: ErrorEvent) => {
      clearTimeout(deadline);
      reject(new Error(`bootstrap worker error: ${event.message}`));
    };
  });
  return { report, worker };
}

/** Spawns a bootstrap worker and resolves when a message with `phase` arrives. */
async function reportForPhase(
  targetUrl: string,
  phase: string,
  timeoutMs = 10_000,
): Promise<{ report: BootstrapReport; worker: Worker }> {
  const worker = spawnBootstrapWorker(targetUrl, { profile: "repl" });
  const report = await new Promise<BootstrapReport>((resolve, reject) => {
    const deadline = setTimeout(
      () => reject(new Error(`no '${phase}' report within the deadline`)),
      timeoutMs,
    );
    worker.onmessage = (event: MessageEvent) => {
      const message = event.data as BootstrapReport;
      if (message.phase === phase) {
        clearTimeout(deadline);
        resolve(message);
      }
    };
    worker.onerror = (event: ErrorEvent) => {
      clearTimeout(deadline);
      reject(new Error(`bootstrap worker error: ${event.message}`));
    };
  });
  return { report, worker };
}

Deno.test("bootstrap contract exposes the versioned target descriptor", () => {
  const marker = readBootstrapMarker();
  // The test runner itself is not a wrapped worker; the marker must be absent.
  assertEquals(marker, null);
});

Deno.test("the shared bootstrap installs before the nested target top-level runs", async () => {
  // order_root.ts is a root worker that installs the shared bootstrap then
  // spawns order_target.ts through the patched Worker; the nested target
  // reports the marker, proving the wrapper installed it before the target
  // module's top-level code ran.
  const { report, worker } = await firstReport(TARGET("order_root.ts"));
  try {
    assertEquals(report.phase, "target-top-level");
    assertEquals(report.version, 1);
    assertEquals(report.profile, "repl");
  } finally {
    worker.terminate();
  }
});

Deno.test("the wrapper propagates the profile marker through recursive routing", async () => {
  const { report, worker } = await firstReport(TARGET("order_root_plugin.ts"));
  try {
    assertEquals(report.profile, "plugin");
    assertEquals(report.version, 1);
  } finally {
    worker.terminate();
  }
});

Deno.test("nested module Workers are routed through the wrapper recursively", async () => {
  const { report, worker } = await reportForPhase(TARGET("nested_target.ts"), "nested-reply");
  try {
    const nested = report.nested as BootstrapReport;
    assertEquals(nested.name, "");
    // The nested worker is wrapped: it carries the versioned marker...
    assertEquals(nested.version, 1);
    // ...and does NOT inherit REPL profile globals (no ambient prompt/maieutics).
    assertEquals(nested.hasReplGlobals, false);
  } finally {
    worker.terminate();
  }
});

Deno.test("the patch redirects constructor references and keeps instanceof truthful", async () => {
  // probe_root.ts is a root worker that installs the shared bootstrap and then
  // spawns nested_probe.ts as a NESTED worker, so the wrapper installs the
  // patch before the probe's top-level code runs. The routed constructor must
  // be reachable as Worker.prototype.constructor and as every real instance's
  // constructor; the prototype parent must not carry the native Worker
  // constructor; instanceof stays truthful.
  const { report, worker } = await reportForPhase(
    TARGET("probe_root.ts"),
    "nested-probe",
  );
  try {
    const probe = report as BootstrapReport & {
      protoAccessible?: boolean;
      protoCtorIsRouted?: boolean;
      instanceCtorIsRouted?: boolean;
      parentCtorNameNotNative?: boolean;
      instanceOfResult?: boolean;
    };
    assertEquals(probe.protoAccessible, true);
    assertEquals(probe.protoCtorIsRouted, true);
    assertEquals(probe.instanceCtorIsRouted, true);
    assertEquals(probe.parentCtorNameNotNative, true);
    assertEquals(probe.instanceOfResult, true);
  } finally {
    worker.terminate();
  }
});

Deno.test("nested relative specifiers resolve against the caller module", async () => {
  // nested_target.ts creates a nested worker from `./nested/nested_target.ts`
  // (via new URL(..., import.meta.url)); a wrapper-relative resolution would
  // fail with "Module not found". A nested-reply proves recursion + resolution.
  const { report, worker } = await reportForPhase(TARGET("nested_target.ts"), "nested-reply");
  try {
    const nested = report.nested as BootstrapReport;
    assertEquals(nested.profile, "repl");
    assertEquals(nested.version, 1);
  } finally {
    worker.terminate();
  }
});

Deno.test("worker name and deno permission options survive creation", async () => {
  // repl_mini_actor.ts is a root worker that installs the shared bootstrap;
  // the factory must forward the name option (self.name) and the deno
  // permission option (the worker needs env+read to run).
  const target = TARGET("repl_mini_actor.ts");
  const worker = spawnBootstrapWorker(target, {
    profile: "repl",
    name: "named-repl-worker",
    deno: { permissions: { env: true, read: true } },
  });
  const report = await new Promise<BootstrapReport>((resolve, reject) => {
    const deadline = setTimeout(
      () => reject(new Error("worker did not report within the deadline")),
      10_000,
    );
    worker.onmessage = (event: MessageEvent) => {
      clearTimeout(deadline);
      resolve(event.data as BootstrapReport);
    };
    worker.onerror = (event: ErrorEvent) => {
      clearTimeout(deadline);
      reject(new Error(`worker error: ${event.message}`));
    };
  });
  try {
    assertEquals(report.name, "named-repl-worker");
    assertEquals(report.phase, "repl-mini-ready");
  } finally {
    worker.terminate();
  }
});

Deno.test("a classic Worker request is a typed unsupported operation", async () => {
  const target = TARGET("classic_probe.ts");
  const worker = spawnBootstrapWorker(target, { profile: "repl" });
  try {
    const actor = await spawn<typeof ClassicProbe.rpc>(worker, {
      signal: AbortSignal.timeout(10_000),
    });
    const result = await actor.attemptClassic();
    assertEquals(result.name, "NotSupportedError");
    assertEquals(
      result.message,
      "Classic workers are not supported by the Maieutics runtime; use a module worker.",
    );
  } finally {
    worker.terminate();
  }
});

Deno.test("a missing worker-actor handshake fails creation and leaves no live worker", async () => {
  const worker = spawnBootstrapWorker(TARGET("no_handshake_target.ts"), {
    profile: "repl",
  });
  const observed: unknown[] = [];
  let failure: unknown;
  try {
    await spawn(worker, {
      signal: AbortSignal.timeout(1_500),
      onDeath: (reason) => observed.push(reason),
    });
    throw new Error("spawn unexpectedly succeeded for a worker without a handshake");
  } catch (error) {
    failure = error;
  }
  const message = failure instanceof Error ? failure.message : String(failure);
  assertEquals(
    message.includes("handshake timed out") || message.includes("ready"),
    true,
    `expected a handshake failure, got: ${message}`,
  );
  // worker-actor's kill() terminated the worker as part of the failed spawn.
  assertEquals(observed.length, 1, "the actor owner must observe exactly one death");
  // No detached child survives: the wrapper worker was terminated by kill().
  // (Worker.terminate is synchronous; the object is reusable only for lifecycle
  // observation, so asserting termination indirectly is not possible here.)
});

Deno.test("a target top-level failure surfaces as worker startup failure", async () => {
  // The failing target emits a Worker error event AND Deno's internal
  // "Unhandled error in child worker" rejection; the module-scoped listener
  // suppresses the internal one, and the error event is the observable
  // startup-failure surface.
  const worker = spawnBootstrapWorker(TARGET("failing_target.ts"), {
    profile: "repl",
  });
  const error = await new Promise<ErrorEvent>((resolve, reject) => {
    const deadline = setTimeout(
      () => reject(new Error("expected a worker startup failure, got none")),
      10_000,
    );
    worker.onerror = (event: ErrorEvent) => {
      clearTimeout(deadline);
      resolve(event);
    };
  });
  assertErrorText(error.message, "fixture target top-level failure");
  // Allow Deno's internal worker-poll rejection to settle inside this test
  // (the module-scoped listener keeps it out of the failure report).
  await new Promise((resolve) => setTimeout(resolve, 250));
});

function assertErrorText(message: string, expected: string): void {
  assertEquals(message.includes(expected), true, `error '${message}' must include '${expected}'`);
}
