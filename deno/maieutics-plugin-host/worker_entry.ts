/**
 * Plugin worker entry, materialized by the kernel and passed to each worker as
 * its module URL. The host sends an `init` frame with the plugin export module
 * URL; this entry imports the plugin module, scans its top-level exports for
 * extension point marker symbols through the SDK, and serves `invoke` frames.
 */

import { ExtensionPoint } from "../maieutics-plugin-sdk/mod.ts";

interface InitFrame {
  type: "init";
  entryUrl: string;
}

interface InvokeFrame {
  type: "invoke";
  id: string;
  extensionPoint: string;
  request: unknown;
}

let initialized: Promise<void> | undefined;
let extensions = new Map<string, unknown>();

const scope = self as unknown as {
  postMessage(message: unknown): void;
  onmessage: ((event: MessageEvent) => void) | null;
};

function fail(id: string | null, code: string, message: string): void {
  scope.postMessage({ type: "error", id, code, message });
}

async function initialize(frame: InitFrame): Promise<void> {
  const namespace = await import(frame.entryUrl);
  const extensionPoints = Object.entries(
    ExtensionPoint as Record<string, symbol>,
  );
  const found = new Map<string, unknown>();
  for (const key of Object.keys(namespace)) {
    const value = (namespace as Record<string, unknown>)[key];
    if (typeof value !== "object" && typeof value !== "function") {
      continue;
    }
    for (const [name, symbol] of extensionPoints) {
      if ((value as Record<symbol, unknown>)[symbol] === true) {
        found.set(name, value);
      }
    }
  }
  extensions = found;
  scope.postMessage({
    type: "ready",
    extensionPoints: [...found.keys()],
  });
}

async function invoke(frame: InvokeFrame): Promise<void> {
  const { id, extensionPoint, request } = frame;
  try {
    await initialized;
    const impl = extensions.get(extensionPoint);
    if (impl === undefined) {
      fail(
        id,
        "extension_point_not_registered",
        `Extension point '${extensionPoint}' is not registered.`,
      );
      return;
    }
    const value = typeof impl === "function"
      ? await (impl as (context: unknown) => unknown)(request)
      : await (impl as { handler(context: unknown): unknown }).handler(
        request,
      );
    scope.postMessage({ type: "result", id, value });
  } catch (error) {
    fail(
      id,
      "extension_failed",
      error instanceof Error ? error.message : String(error),
    );
  }
}

scope.onmessage = (event: MessageEvent): void => {
  const frame = event.data as InitFrame | InvokeFrame;
  if (frame?.type === "init") {
    initialized = initialize(frame).catch((error) => {
      fail(
        null,
        "init_failed",
        error instanceof Error ? error.message : String(error),
      );
      throw error;
    });
  } else if (frame?.type === "invoke") {
    void invoke(frame as InvokeFrame);
  }
};
