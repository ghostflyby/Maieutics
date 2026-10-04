/**
 * UI bridge (custom-UI framework, ADR 0038): connects notebook renderer
 * messaging for BOTH the jupyter.widget compat family and the native view
 * families to the session comms socket — one socket, family-dispatched
 * envelopes. State lives kernel-side; the bridge caches the latest known
 * state per model id so a renderer mounting late — or after a re-render —
 * gets the current state immediately instead of replaying frames. The class
 * is transport- and vscode-free: the socket and the post sink are injected,
 * which keeps it unit-testable.
 *
 * Envelopes (renderer ↔ host):
 *   renderer → host: { source: "maieutics-widget"|"maieutics-ui", type: "mount",  modelId }
 *   renderer → host: { source, type: "update", modelId, state }
 *   renderer → host: { source: "maieutics-ui", type: "event",  modelId, name, payload? }
 *   host → renderer: { source, type: "state",  modelId, state }
 *
 * The outbound source follows the model's comm target: `jupyter.widget`
 * models speak "maieutics-widget" (byte-compatible with the old widget
 * bridge); targets under `maieutics.view/` speak "maieutics-ui".
 */

import type { CommFrame, CommMessage } from "./protocol.ts";

export interface UiCommSocket {
  send(message: CommMessage): void;
  messages: AsyncGenerator<CommFrame>;
  close(): void;
}

export interface UiBridgeOptions {
  connect(): Promise<UiCommSocket>;
  /** Broadcasts a message to every UI renderer output. */
  post(message: unknown): void;
  log(message: string): void;
}

const WIDGET_SOURCE = "maieutics-widget";
const UI_SOURCE = "maieutics-ui";
const WIDGET_TARGET = "jupyter.widget";

export class UiBridge {
  readonly #options: UiBridgeOptions;
  readonly #states = new Map<string, Record<string, unknown>>();
  readonly #sources = new Map<string, string>();
  #socket: UiCommSocket | undefined;
  #pump: Promise<void> | undefined;
  #connect: Promise<void> | undefined;
  #closed = false;

  constructor(options: UiBridgeOptions) {
    this.#options = options;
  }

  /** A renderer mounted a model view: reply with the cached state, opening
   * the socket lazily on the first mount. */
  async handleRendererMessage(message: unknown): Promise<void> {
    if (typeof message !== "object" || message === null) return;
    const record = message as Record<string, unknown>;
    const modelId = typeof record.modelId === "string" ? record.modelId : "";
    if (modelId.length === 0) return;

    if (record.type === "mount") {
      // Several outputs can mount in the same pass: one connect, ever.
      this.#connect ??= this.#startAsync();
      await this.#connect.catch((error: unknown) => {
        this.#connect = undefined;
        this.#options.log(`ui connect failed: ${error}`);
      });
      this.#postState(modelId);
      return;
    }

    if (record.type === "update" && this.#socket) {
      const state = record.state;
      if (typeof state !== "object" || state === null || Array.isArray(state)) return;
      this.#merge(modelId, state as Record<string, unknown>);
      this.#socket.send({
        kind: 1,
        commId: modelId,
        data: { method: "update", state, buffer_paths: [] },
        buffers: [],
      });
      return;
    }

    if (record.type === "event" && record.source === UI_SOURCE && this.#socket) {
      const name = record.name;
      if (typeof name !== "string" || name.length === 0) return;
      this.#socket.send({
        kind: 1,
        commId: modelId,
        data: { method: "event", name, payload: record.payload },
        buffers: [],
      });
    }
  }

  async dispose(): Promise<void> {
    this.#closed = true;
    this.#socket?.close();
    this.#socket = undefined;
    // Closing the socket ends the pump; observe its settlement so the
    // background task is complete before disposal returns.
    await this.#pump?.catch(() => {});
    this.#pump = undefined;
  }

  async #startAsync(): Promise<void> {
    this.#socket = await this.#options.connect();
    // The pump runs for the socket's lifetime; mount handling must not wait
    // on it. Failures are logged, never swallowed (the pump logs its own end).
    this.#pump = this.#pumpLoopAsync();
  }

  async #pumpLoopAsync(): Promise<void> {
    const socket = this.#socket;
    if (socket === undefined) return;
    try {
      for await (const frame of socket.messages) {
        if (this.#closed) return;
        if (frame.kind !== "comm") continue;

        const { message } = frame;
        const data = message.data as Record<string, unknown> | undefined;
        if (message.kind === 0) {
          const state = isRecord(data?.state) ? data.state as Record<string, unknown> : {};
          this.#states.set(message.commId, { ...state });
          this.#sources.set(
            message.commId,
            message.targetName === WIDGET_TARGET || message.targetName === undefined
              ? WIDGET_SOURCE
              : UI_SOURCE,
          );
        } else if (message.kind === 1 && isRecord(data?.state)) {
          this.#merge(message.commId, data.state as Record<string, unknown>);
        } else if (message.kind === 2) {
          this.#states.delete(message.commId);
          this.#sources.delete(message.commId);
          continue;
        }

        this.#postState(message.commId);
      }
    } catch (error) {
      this.#options.log(`ui bridge pump ended: ${error}`);
    }
  }

  #merge(modelId: string, state: Record<string, unknown>): void {
    const current = this.#states.get(modelId) ?? {};
    this.#states.set(modelId, { ...current, ...state });
  }

  #postState(modelId: string): void {
    const state = this.#states.get(modelId);
    if (state === undefined) return;
    const source = this.#sources.get(modelId);
    if (source === undefined) {
      // No cached comm_open for this model (e.g. its open fell out of the
      // server's bounded replay): post under both sources so either
      // renderer receives its own frames — each filters by source.
      this.#options.post({ source: WIDGET_SOURCE, type: "state", modelId, state });
      this.#options.post({ source: UI_SOURCE, type: "state", modelId, state });
      return;
    }
    this.#options.post({ source, type: "state", modelId, state });
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
