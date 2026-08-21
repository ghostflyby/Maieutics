/**
 * Maieutics plugin host process entry. The kernel spawns this process with the
 * control channel address and a plugin configuration file; the host creates
 * permission-scoped worker actors per plugin export via worker-actor's `spawn`
 * and bridges control messages between the kernel and the workers.
 */

import { type PluginConfig, PluginHost, type PluginState } from "./host.ts";
import { connectBus } from "../shared/bus.ts";
import type { ReplEnvelope } from "../shared/protocol.ts";

const IPC_ENV = "MAIEUTICS_REPL_IPC";
const HOST_ID_ENV = "MAIEUTICS_PLUGIN_HOST_ID";
const CONFIG_ENV = "MAIEUTICS_PLUGIN_CONFIG";
const SDK_ENV = "MAIEUTICS_PLUGIN_SDK";
const WORKER_ENTRY_ENV = "MAIEUTICS_PLUGIN_WORKER_ENTRY";

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
  };
  const host = new PluginHost({
    sdkUrl,
    workerEntryUrl,
    plugins: config.plugins ?? [],
  });

  const registered = await host.startAll();
  console.error(
    `[plugin-host] ${hostId}: ${registered.length} extension registration(s) across ` +
      `${config.plugins?.length ?? 0} plugin(s).`,
  );

  // The control host (Kestrel in the composition root) may not be listening
  // yet when this process starts, so the bus connect is retried instead of
  // crashing the host. The first registry snapshot is sent once connected.
  const bus = await connectBusWithRetry({
    address: ipcAddress,
    hello: {
      type: "control.hello",
      payload: { hostId },
    },
    onMessage: handleMessage,
  });
  bus.send({
    type: "extension.registry",
    payload: registryPayload(registered, host.states()),
  });

  const shutdown = (): void => {
    host.dispose();
    bus.close();
  };
  globalThis.addEventListener("unload", shutdown);

  function handleMessage(envelope: ReplEnvelope): void {
    if (envelope.type === "plugin.reload") {
      const payload = envelope.payload as {
        pluginId?: string;
        exportName?: string;
        plugin?: PluginConfig;
      };
      if (typeof payload?.pluginId === "string" && typeof payload.exportName === "string") {
        const next = payload.plugin;
        void host.reload(payload.pluginId, payload.exportName, next).then(() => {
          bus.send({
            type: "extension.registry",
            payload: registryPayload(host.extensions, host.states()),
          });
        }).catch((error: Error) => {
          console.error(`[plugin-host] reload of '${payload.pluginId}' failed: ${error.message}`);
        });
      }
      return;
    }
    if (envelope.type === "extension.invoke") {
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
          bus.send({
            type: "extension.result",
            payload: { value },
            correlationId: envelope.correlationId,
          });
        }).catch((error: Error) => {
          bus.send({
            type: "extension.error",
            payload: { code: "extension_failed", message: error.message },
            correlationId: envelope.correlationId,
          });
        });
      }
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
