/**
 * Maieutics plugin host process entry. The kernel spawns this process with the
 * control channel address and a plugin configuration file; the host creates
 * permission-scoped workers per plugin export and bridges `extension.*` bus
 * messages between the kernel and the workers.
 */

import { type PluginConfig, PluginHost } from "./host.ts";
import { type BusConnection, connectBus } from "../shared/bus.ts";
import { type ReplEnvelope } from "../shared/protocol.ts";

const IPC_ENV = "MAIEUTICS_REPL_IPC";
const HOST_ID_ENV = "MAIEUTICS_PLUGIN_HOST_ID";
const CONFIG_ENV = "MAIEUTICS_PLUGIN_CONFIG";
const SDK_ENV = "MAIEUTICS_PLUGIN_SDK";
const WORKER_ENTRY_ENV = "MAIEUTICS_PLUGIN_WORKER_ENTRY";

function requireEnv(name: string): string {
  const value = Deno.env.get(name);
  if (!value) {
    throw new Error(`Missing ${name} environment variable for the plugin host.`);
  }
  return value;
}

function isExtensionInvoke(payload: unknown): payload is {
  pluginId: string;
  exportName: string;
  extensionPoint: string;
  request: unknown;
} {
  if (typeof payload !== "object" || payload === null) {
    return false;
  }
  const candidate = payload as Record<string, unknown>;
  return typeof candidate.pluginId === "string" &&
    typeof candidate.exportName === "string" &&
    typeof candidate.extensionPoint === "string";
}

async function main(): Promise<void> {
  const ipcAddress = requireEnv(IPC_ENV);
  const hostId = requireEnv(HOST_ID_ENV);
  const configPath = requireEnv(CONFIG_ENV);
  const sdkUrl = requireEnv(SDK_ENV);
  const workerEntryUrl = requireEnv(WORKER_ENTRY_ENV);

  const config = JSON.parse(await Deno.readTextFile(configPath)) as { plugins: PluginConfig[] };
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

  const http = Deno.createHttpClient({ proxy: { transport: "unix", path: ipcAddress } });
  const bus = await connectBus(http, {
    type: "control.hello",
    payload: { hostId },
  }, handleMessage);
  bus.send({
    type: "extension.registry",
    payload: registryPayload(registered),
  });

  const shutdown = (): void => {
    host.dispose();
    bus.close();
  };
  globalThis.addEventListener("unload", shutdown);

  function handleMessage(envelope: ReplEnvelope): void {
    if (envelope.type !== "extension.invoke") {
      return;
    }
    const payload = envelope.payload;
    if (!isExtensionInvoke(payload)) {
      bus.send({
        type: "extension.error",
        correlationId: envelope.correlationId,
        payload: { code: "invalid_invoke", message: "The extension.invoke payload is malformed." },
      });
      return;
    }
    void host.invoke(payload.pluginId, payload.exportName, payload.extensionPoint, payload.request)
      .then((value) => {
        bus.send({
          type: "extension.result",
          correlationId: envelope.correlationId,
          payload: { value },
        });
      })
      .catch((error: Error) => {
        bus.send({
          type: "extension.error",
          correlationId: envelope.correlationId,
          payload: { code: "extension_failed", message: error.message },
        });
      });
  }
}

function registryPayload(
  registrations: ReadonlyArray<{
    pluginId: string;
    exportName: string;
    extensionPoint: string;
  }>,
): { plugins: Array<{ pluginId: string; exportName: string; extensionPoints: string[] }> } {
  const byWorker = new Map<string, Map<string, string[]>>();
  for (const registration of registrations) {
    let workers = byWorker.get(registration.pluginId);
    if (workers === undefined) {
      workers = new Map();
      byWorker.set(registration.pluginId, workers);
    }
    const points = workers.get(registration.exportName) ?? [];
    points.push(registration.extensionPoint);
    workers.set(registration.exportName, points);
  }
  const plugins = [];
  for (const [pluginId, workers] of byWorker) {
    for (const [exportName, extensionPoints] of workers) {
      plugins.push({ pluginId, exportName, extensionPoints });
    }
  }
  return { plugins };
}

await main();
