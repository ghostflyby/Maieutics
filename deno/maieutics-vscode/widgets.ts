/**
 * Widget bridge (ADR 0024): connects the widget renderer's messaging to the
 * session comms socket. State lives kernel-side; the bridge caches the
 * latest known state per model id so a renderer mounting late — or after a
 * re-render — gets the current state immediately instead of replaying frames.
 * The class is transport- and vscode-free: the socket and the post sink are
 * injected, which keeps it unit-testable.
 */

import type { CommFrame, CommMessage } from "./protocol.ts";

export interface WidgetCommSocket {
  send(message: CommMessage): void;
  messages: AsyncGenerator<CommFrame>;
  close(): void;
}

export interface WidgetBridgeOptions {
  connect(): Promise<WidgetCommSocket>;
  /** Broadcasts a message to every widget renderer output. */
  post(message: unknown): void;
  log(message: string): void;
}

export class WidgetBridge {
  readonly #options: WidgetBridgeOptions;
  readonly #states = new Map<string, Record<string, unknown>>();
  #socket: WidgetCommSocket | undefined;
  #pump: Promise<void> | undefined;
  #closed = false;

  constructor(options: WidgetBridgeOptions) {
    this.#options = options;
  }

  /** A renderer mounted a widget view: reply with the cached state, opening
   * the socket lazily on the first mount. */
  async handleRendererMessage(message: unknown): Promise<void> {
    if (typeof message !== "object" || message === null) return;
    const record = message as Record<string, unknown>;
    const modelId = typeof record.modelId === "string" ? record.modelId : "";
    if (modelId.length === 0) return;

    if (record.type === "mount") {
      if (!this.#socket && !this.#closed) await this.#startAsync();
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
        } else if (message.kind === 1 && isRecord(data?.state)) {
          this.#merge(message.commId, data.state as Record<string, unknown>);
        } else if (message.kind === 2) {
          this.#states.delete(message.commId);
          continue;
        }

        this.#postState(message.commId);
      }
    } catch (error) {
      this.#options.log(`widget bridge pump ended: ${error}`);
    }
  }

  #merge(modelId: string, state: Record<string, unknown>): void {
    const current = this.#states.get(modelId) ?? {};
    this.#states.set(modelId, { ...current, ...state });
  }

  #postState(modelId: string): void {
    const state = this.#states.get(modelId);
    if (state === undefined) return;
    this.#options.post({ source: "maieutics-widget", type: "state", modelId, state });
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
