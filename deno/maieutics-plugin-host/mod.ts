/**
 * Maieutics plugin host process entry. The kernel spawns this process with the
 * control channel address and a plugin configuration file; the host creates
 * permission-scoped worker actors per plugin export via worker-actor's `spawn`
 * and bridges control messages between the kernel and the workers.
 */

import { type PluginConfig, PluginHost, type PluginState } from "./host.ts";
import { ReplManager } from "./repl_manager.ts";
import { connectBus } from "../shared/bus.ts";
import type { HttpGatewayDescriptor } from "./http.ts";
import type { ReplEnvelope } from "../shared/protocol.ts";
import type { HostReplReport } from "./host_repl_protocol.ts";

const IPC_ENV = "MAIEUTICS_REPL_IPC";
const HOST_ID_ENV = "MAIEUTICS_PLUGIN_HOST_ID";
const CONFIG_ENV = "MAIEUTICS_PLUGIN_CONFIG";
const SDK_ENV = "MAIEUTICS_PLUGIN_SDK";
const WORKER_ENTRY_ENV = "MAIEUTICS_PLUGIN_WORKER_ENTRY";
const REPL_ENTRY_ENV = "MAIEUTICS_REPL_PROCESS_ENTRY";

function requireEnv(name: string): string {
  const value = Deno.env.get(name);
  if (!value) {
    throw new Error(
      `Missing ${name} environment variable for the plugin host.`,
    );
  }
  return value;
}

async function main(): Promise<void> {
  const ipcAddress = requireEnv(IPC_ENV);
  const hostId = requireEnv(HOST_ID_ENV);
  const configPath = requireEnv(CONFIG_ENV);
  const sdkUrl = requireEnv(SDK_ENV);
  const workerEntryUrl = requireEnv(WORKER_ENTRY_ENV);

  const config = JSON.parse(await Deno.readTextFile(configPath)) as {
    plugins: PluginConfig[];
    storageDataRoot?: string;
    idleGraceMs?: number;
  };
  const host = new PluginHost({
    sdkUrl,
    workerEntryUrl,
    plugins: config.plugins ?? [],
    storageDataRoot: config.storageDataRoot,
    idleGraceMs: config.idleGraceMs,
  });
  // The sink closure fires before the bus connects (rediscover is a no-op
  // until then), so the bus rides a holder instead of a forward-declared let.
  const busHolder: { bus?: Awaited<ReturnType<typeof connectBus>> } = {};
  // Trigger delivery sinks (ADR 0036): events dispatch to the owning worker's
  // PluginEvent extension point through the same invoke path the kernel uses
  // (start-on-demand included); rediscover sends a plugin.trigger frame the
  // kernel answers by republishing the plugin's MCP registrations.
  host.setTriggerSinks({
    async onEvent(pluginId, trigger, detail) {
      // Resolve targets through the LIVE plugin registry: a plugin activated (or
      // reshaped) after boot — the normal first-run approval flow — exists only
      // there; the frozen boot config would silently drop its events.
      const workers = host.workersOf(pluginId);
      const delivered = await Promise.allSettled(
        workers.map((worker) =>
          host.invoke(pluginId, worker.exportName, "PluginEvent", {
            trigger,
            firedAt: new Date().toISOString(),
            detail,
          })
        ),
      );
      for (const outcome of delivered) {
        if (outcome.status === "rejected") {
          console.error(
            `[plugin-host] PluginEvent delivery for '${pluginId}/${trigger}' failed: ` +
              String(outcome.reason),
          );
        }
      }
    },
    onRediscover(pluginId, trigger) {
      busHolder.bus?.send({
        type: "plugin.trigger",
        payload: { pluginId, trigger },
      });
    },
  });
  // ADR 0020: the host derives REPL processes. The entry path is optional at
  // this stage (spawnRepl is not yet called by a kernel path); the pid
  // registration + broker policy closed loop is covered by host_test.ts. The
  // reporter is wired below once the control bus is connected — a REPL must
  // not be derived before the pid report channel exists.
  const repls = new ReplManager({
    replEntryPath: Deno.env.get(REPL_ENTRY_ENV) ?? "",
  });

  const registered = await host.startAll();
  // ADR 0038 stage 4: the kernel learns the gateway's entrance (address +
  // token + live mounts) so frontends can discover plugin pages. The token is
  // a capability carrier — it rides only the authenticated control bus and is
  // surfaced by the kernel's own bearer-authed endpoints, never logged.
  const gatewayToken = crypto.randomUUID();
  const gateway = host.httpGateway();
  const gatewayAddr = await gateway.startRouter({
    token: gatewayToken,
    onListening: (address) => {
      console.error(`[plugin-host] HTTP gateway listening on ${address.hostname}:${address.port}`);
    },
  });
  const gatewayDescriptor = (): HttpGatewayDescriptor => ({
    hostname: gatewayAddr.hostname,
    port: gatewayAddr.port,
    token: gatewayToken,
    mounts: gateway.snapshots(),
  });
  console.error(
    `[plugin-host] ${hostId}: ${registered.length} extension registration(s) across ` +
      `${config.plugins?.length ?? 0} plugin(s).`,
  );

  // The control host (Kestrel in the composition root) may not be listening
  // yet when this process starts, so the bus connect is retried instead of
  // crashing the host. The first registry snapshot is sent once connected.
  busHolder.bus = await connectBusWithRetry({
    address: ipcAddress,
    hello: {
      type: "control.hello",
      payload: { hostId },
    },
    onMessage: handleMessage,
  });
  busHolder.bus!.send({
    type: "extension.registry",
    payload: {
      ...registryPayload(registered, host.states()),
      httpGateway: gatewayDescriptor(),
    },
  });
  // Host → kernel REPL pid reports ride the same bus. The reporter is wired
  // here, after the hello handshake authenticated this host; ReplManager
  // refuses to derive a REPL before it is set.
  repls.setReporter((report: HostReplReport) => busHolder.bus!.send(report));
  // ADR 0038 stage 4: mount-table changes re-report the registry (the gateway
  // descriptor's mounts must stay live) — wired after the hello handshake.
  gateway.onMountsChanged(() => {
    busHolder.bus?.send({
      type: "extension.registry",
      payload: {
        ...registryPayload(host.extensions, host.states()),
        httpGateway: gatewayDescriptor(),
      },
    });
  });

  // Kernel capability calls ride the same bus: the host relays the worker's
  // request with its derived plugin identity and completes on the correlated
  // capability.result / capability.error reply. The budget bounds a hung
  // kernel; the caller never rejects with an unobserved rejection.
  const capabilityPending = new Map<
    string,
    { resolve: (value: unknown) => void; reject: (error: Error) => void }
  >();
  host.setCapabilityCaller((plugin, capability, payload) => {
    const correlationId = crypto.randomUUID();
    return new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        if (capabilityPending.delete(correlationId)) {
          reject(new Error("The capability call did not settle within its budget."));
        }
      }, 30_000);
      capabilityPending.set(correlationId, {
        resolve: (value: unknown) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (error: Error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
      busHolder.bus?.send({
        type: "capability.invoke",
        payload: { pluginId: plugin, capability, payload },
        correlationId,
      });
    });
  });

  const shutdown = (): void => {
    // The unload event cannot await: the bounded storage flush runs alongside
    // teardown. The debounce loop has normally persisted long before this, so
    // the flush is a bounded best-effort pass over what is still dirty.
    void host.shutdown().catch((error: Error) => {
      console.error(`[plugin-host] storage flush on shutdown failed: ${error.message}`);
    });
    void repls.disposeAll();
    busHolder.bus?.close();
  };
  globalThis.addEventListener("unload", shutdown);

  function handleMessage(envelope: ReplEnvelope): void {
    if (envelope.type === "capability.result" || envelope.type === "capability.error") {
      const correlationId = envelope.correlationId;
      if (correlationId === undefined) return;
      const pending = capabilityPending.get(correlationId);
      if (pending === undefined) return;
      capabilityPending.delete(correlationId);
      const payload = envelope.payload as
        | { result?: unknown; code?: string; message?: string }
        | undefined;
      if (envelope.type === "capability.result") {
        pending.resolve(payload?.result);
      } else {
        // Carry the kernel's typed code through the rejection so the host relay
        // can hand it to the worker unchanged.
        const error = new Error(
          payload?.message ?? payload?.code ?? "The capability call failed.",
        ) as Error & { code?: string };
        error.code = payload?.code;
        pending.reject(error);
      }
      return;
    }
    if (envelope.type === "plugin.reload") {
      const payload = envelope.payload as {
        pluginId?: string;
        exportName?: string;
        plugin?: PluginConfig;
        stop?: boolean;
      };
      if (typeof payload?.pluginId === "string" && typeof payload.exportName === "string") {
        // `stop` is the revocation form (ADR 0037): close the worker and its
        // dependents without restarting; otherwise a replacement config rebuilds
        // the worker — creating it first when the host does not know it (upsert).
        const done = payload.stop === true
          ? host.stop(payload.pluginId, payload.exportName)
          : host.reload(payload.pluginId, payload.exportName, payload.plugin);
        void done.then(() => {
          busHolder.bus!.send({
            type: "extension.registry",
            payload: {
              ...registryPayload(host.extensions, host.states()),
              httpGateway: gatewayDescriptor(),
            },
          });
        }).catch((error: Error) => {
          console.error(`[plugin-host] reload of '${payload.pluginId}' failed: ${error.message}`);
        });
      }
      return;
    }
    if (envelope.type === "host.invoke") {
      // Kernel → host extension point call (retired the `extension.invoke` protocol, ADR 0020
      // §7.2). The host invokes the plugin worker's Remote<T> surface directly in-process
      // (host.invoke below) and answers with host.invokeResult / host.invokeError, echoing the
      // instruction's correlationId so the kernel can complete the pending call.
      const payload = envelope.payload as {
        pluginId?: string;
        exportName?: string;
        extensionPoint?: string;
        request?: unknown;
      };
      if (
        typeof payload?.pluginId === "string" &&
        typeof payload.exportName === "string" &&
        typeof payload.extensionPoint === "string"
      ) {
        void host.invoke(
          payload.pluginId,
          payload.exportName,
          payload.extensionPoint,
          payload.request,
        ).then((value: unknown) => {
          busHolder.bus!.send({
            type: "host.invokeResult",
            payload: { value },
            correlationId: envelope.correlationId,
          });
        }).catch((error: Error) => {
          busHolder.bus!.send({
            type: "host.invokeError",
            payload: { code: "host_invoke_failed", message: error.message },
            correlationId: envelope.correlationId,
          });
        });
      }
      return;
    }
    if (envelope.type === "host.repl.derive") {
      // ADR 0020 / B5a: kernel → host instruction stream. The kernel decides
      // the REPL entry, the complete child env, and the static permission
      // shell; ReplManager validates the payload, derives the REPL, and
      // reports spawned/exited/deriveFailed through the reporter wired above.
      // Derivation is async; failures are reported fire-and-forget inside
      // ReplManager.derive (matching the host.invoke style).
      void repls.derive(envelope);
      return;
    }
  }
}

/**
 * Opens the control bus, retrying until the control host (Kestrel in the
 * composition root) is listening. The host process is a resident orchestration
 * process and must tolerate the kernel's server coming up slightly later; a
 * one-shot connect would crash the host on a startup race. Retries every
 * 250ms up to a bounded window, then fails.
 */
async function connectBusWithRetry(
  options: Parameters<typeof connectBus>[0],
): Promise<ReturnType<typeof connectBus>> {
  const deadline = Date.now() + 30_000;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      return await connectBus(options);
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
  }
  throw lastError ?? new Error("The control bus could not be opened.");
}

function registryPayload(
  registrations: ReadonlyArray<{
    pluginId: string;
    exportName: string;
    extensionPoint: string;
    specifier: string;
  }>,
  states: readonly PluginState[],
): {
  plugins: Array<
    {
      pluginId: string;
      exportName: string;
      extensionPoints: string[];
      specifier?: string;
    }
  >;
  states?: readonly PluginState[];
} {
  const byWorker = new Map<string, Map<string, { points: string[]; specifier?: string }>>();
  for (const registration of registrations) {
    let workers = byWorker.get(registration.pluginId);
    if (workers === undefined) {
      workers = new Map();
      byWorker.set(registration.pluginId, workers);
    }
    const entry = workers.get(registration.exportName) ??
      { points: [], specifier: registration.specifier };
    entry.points.push(registration.extensionPoint);
    entry.specifier ??= registration.specifier;
    workers.set(registration.exportName, entry);
  }
  const plugins = [];
  for (const [pluginId, workers] of byWorker) {
    for (const [exportName, entry] of workers) {
      plugins.push({
        pluginId,
        exportName,
        extensionPoints: entry.points,
        ...(entry.specifier === undefined ? {} : { specifier: entry.specifier }),
      });
    }
  }
  return { plugins, states };
}

await main();
