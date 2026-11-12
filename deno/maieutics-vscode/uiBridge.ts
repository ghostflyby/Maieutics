/** Connects the shared Vue form client to the session's generic comm transport. */
import type { CommFrame, CommMessage } from "./protocol.ts";
export interface UiCommSocket {
  send(message: CommMessage): void;
  messages: AsyncGenerator<CommFrame>;
  close(): void;
}
export interface UiBridgeOptions {
  connect(signal: AbortSignal): Promise<UiCommSocket>;
  post(message: unknown): void;
  log(message: string): void;
}
const SOURCE = "maieutics-form";
const TARGET = "maieutics.form";

export class UiBridge {
  readonly #options: UiBridgeOptions;
  readonly #states = new Map<string, Record<string, unknown>>();
  readonly #views = new Map<string, string>();
  #socket: UiCommSocket | undefined;
  #pump: Promise<void> | undefined;
  #connect: Promise<void> | undefined;
  #closing: Promise<void> | undefined;
  readonly #lifetime = new AbortController();
  #closed = false;
  constructor(options: UiBridgeOptions) {
    this.#options = options;
  }

  async handleRendererMessage(message: unknown): Promise<void> {
    if (
      this.#closed || !isRecord(message) || message.source !== SOURCE ||
      typeof message.modelId !== "string" || !message.modelId ||
      typeof message.viewId !== "string" || !message.viewId
    ) return;
    const { modelId, viewId } = message;
    if (message.type === "unmount") {
      if (this.#views.get(viewId) === modelId) this.#views.delete(viewId);
      return;
    }
    if (message.type === "mount") {
      this.#views.set(viewId, modelId);
      this.#connect ??= this.#startAsync();
      try {
        await this.#connect;
      } catch (error) {
        this.#connect = undefined;
        if (!this.#closed) this.#options.log(`form connect failed: ${error}`);
      }
      if (!this.#closed && this.#views.get(viewId) === modelId) this.#postState(modelId);
      return;
    }
    if (
      message.type === "event" && this.#views.get(viewId) === modelId &&
      this.#states.has(modelId) && ["submit", "cancel"].includes(String(message.name))
    ) {
      this.#socket?.send({
        kind: 1,
        commId: modelId,
        data: { method: "event", name: message.name, payload: message.payload },
        buffers: [],
      });
    }
  }

  dispose(): Promise<void> {
    this.#closing ??= this.#disposeAsync();
    return this.#closing;
  }
  async #disposeAsync(): Promise<void> {
    this.#closed = true;
    this.#lifetime.abort();
    await this.#connect?.catch(() => {});
    this.#socket?.close();
    this.#socket = undefined;
    await this.#pump;
    this.#states.clear();
    this.#views.clear();
  }
  async #startAsync(): Promise<void> {
    const socket = await this.#options.connect(this.#lifetime.signal);
    if (this.#closed) {
      socket.close();
      return;
    }
    this.#socket = socket;
    this.#pump = this.#pumpLoopAsync(socket);
  }
  async #pumpLoopAsync(socket: UiCommSocket): Promise<void> {
    try {
      for await (const frame of socket.messages) {
        if (this.#closed) return;
        if (frame.kind !== "comm") continue;
        const { message } = frame;
        const data = isRecord(message.data) ? message.data : {};
        if (message.kind === 0) {
          if (message.targetName !== TARGET) continue;
          if (!this.#states.has(message.commId) && this.#states.size >= 1024) {
            this.#options.log("form open rejected: form_state_limit");
            this.#postClosed(message.commId, "form_state_limit");
            continue;
          }
          this.#states.set(message.commId, isRecord(data.state) ? { ...data.state } : {});
        } else if (message.kind === 1) {
          const state = this.#states.get(message.commId);
          if (!state || data.method !== "update" || !isRecord(data.state)) continue;
          this.#states.set(message.commId, { ...state, ...data.state });
        } else if (message.kind === 2) {
          if (this.#states.delete(message.commId)) this.#postClosed(message.commId);
          continue;
        }
        this.#postState(message.commId);
      }
    } catch (error) {
      this.#options.log(`form bridge ended: ${error}`);
    } finally {
      for (const modelId of this.#states.keys()) this.#postClosed(modelId);
      this.#states.clear();
      if (this.#socket === socket) {
        this.#socket = undefined;
        socket.close();
      }
      this.#connect = undefined;
    }
  }
  #postState(modelId: string): void {
    const state = this.#states.get(modelId);
    if (state && [...this.#views.values()].includes(modelId)) {
      this.#options.post({ source: SOURCE, type: "state", modelId, state });
    }
  }
  #postClosed(modelId: string, code?: string): void {
    if ([...this.#views.values()].includes(modelId)) {
      this.#options.post({ source: SOURCE, type: "closed", modelId, ...(code ? { code } : {}) });
    }
  }
}
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
