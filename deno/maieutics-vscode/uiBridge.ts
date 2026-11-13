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

export interface UiBridgeRegistryOptions<Owner> {
  connect(sessionId: string, signal: AbortSignal): Promise<UiCommSocket>;
  post(owner: Owner, message: unknown): void;
  log(message: string): void;
}

interface UiBridgeScope {
  readonly sessionId: string;
  readonly bridge: UiBridge;
  readonly views: Map<string, string>;
}

/** Owns one explicitly session-bound comm scope per host editor, with targeted downlink delivery. */
export class UiBridgeRegistry<Owner> {
  readonly #scopes = new Map<Owner, UiBridgeScope>();
  readonly #retiring = new Set<Promise<void>>();
  #closed = false;
  #closing: Promise<void> | undefined;

  constructor(private readonly options: UiBridgeRegistryOptions<Owner>) {}

  /** An unbound notebook cannot attach; only a mount establishes a new scope. */
  async handleRendererMessage(
    owner: Owner,
    sessionId: string | undefined,
    message: unknown,
  ): Promise<void> {
    if (
      this.#closed || !isRecord(message) || message.source !== SOURCE ||
      typeof message.modelId !== "string" || !message.modelId ||
      typeof message.viewId !== "string" || !message.viewId ||
      !["mount", "unmount", "event"].includes(String(message.type))
    ) return;
    let scope = this.#scopes.get(owner);
    if (scope && scope.sessionId !== sessionId) {
      this.#retire(owner, scope);
      scope = undefined;
    }
    if (!scope && sessionId && message.type === "mount") {
      if (this.#scopes.size + this.#retiring.size >= 128) {
        this.options.log("form editor rejected: form_scope_limit");
        this.options.post(owner, {
          source: SOURCE,
          type: "closed",
          modelId: message.modelId,
          code: "form_scope_limit",
        });
        return;
      }
      const created: UiBridgeScope = {
        sessionId,
        views: new Map(),
        bridge: new UiBridge({
          connect: (signal) => this.options.connect(sessionId, signal),
          post: (value) => {
            if (this.#scopes.get(owner) === created) this.options.post(owner, value);
          },
          log: (value) => this.options.log(value),
        }),
      };
      this.#scopes.set(owner, created);
      scope = created;
    }
    if (!scope || this.#closed || this.#scopes.get(owner) !== scope) return;
    if (message.type === "mount") scope.views.set(message.viewId, message.modelId);
    if (message.type === "unmount" && scope.views.get(message.viewId) === message.modelId) {
      scope.views.delete(message.viewId);
    }
    await scope.bridge.handleRendererMessage(message);
    if (message.type === "unmount" && scope.views.size === 0 && this.#scopes.get(owner) === scope) {
      await this.#retire(owner, scope);
    }
  }

  /** Release all editors owned by a closed notebook without closing their upstream models. */
  async releaseWhere(predicate: (owner: Owner, sessionId: string) => boolean): Promise<void> {
    const pending = [...this.#scopes].filter(([owner, scope]) => predicate(owner, scope.sessionId))
      .map(([owner, scope]) => this.#retire(owner, scope));
    await joinUiScopes(pending);
  }

  /** Cancel and join every active or retiring scope; disposal is terminal and idempotent. */
  dispose(): Promise<void> {
    this.#closing ??= this.#disposeAsync();
    return this.#closing;
  }

  async #disposeAsync(): Promise<void> {
    this.#closed = true;
    for (const [owner, scope] of this.#scopes) this.#retire(owner, scope);
    await joinUiScopes([...this.#retiring]);
  }

  #retire(owner: Owner, scope: UiBridgeScope): Promise<void> {
    this.#scopes.delete(owner);
    const completion = scope.bridge.dispose();
    this.#retiring.add(completion);
    // Old receive pumps cannot publish into a replacement scope. Freeze retained views now.
    for (const modelId of new Set(scope.views.values())) {
      try {
        this.options.post(owner, { source: SOURCE, type: "closed", modelId });
      } catch {
        this.options.log("Form closure delivery failed.");
      }
    }
    void completion.then(
      () => this.#retiring.delete(completion),
      () => {
        this.#retiring.delete(completion);
        this.options.log("Form scope cleanup failed.");
      },
    );
    return completion;
  }
}

async function joinUiScopes(pending: readonly Promise<void>[]): Promise<void> {
  const failures = (await Promise.allSettled(pending)).filter((result) =>
    result.status === "rejected"
  );
  if (failures.length) {
    throw new AggregateError(failures.map((result) => result.reason), "Form scope cleanup failed.");
  }
}

export interface UiBridgeViewsOptions<Owner extends object> extends UiBridgeRegistryOptions<Owner> {
  session(owner: Owner): string | undefined;
  visible(owner: Owner): boolean;
}

/** Visible editors own connections; dormant mount identities are weak and resume without browser retries. */
export class UiBridgeViews<Owner extends object> {
  readonly #registry: UiBridgeRegistry<Owner>;
  readonly #active = new Set<Owner>();
  #mounts = new WeakMap<Owner, { sessionId: string; views: Map<string, string> }>();
  #closed = false;

  constructor(private readonly options: UiBridgeViewsOptions<Owner>) {
    this.#registry = new UiBridgeRegistry(options);
  }

  async handleRendererMessage(owner: Owner, message: unknown): Promise<void> {
    if (
      this.#closed || !isRecord(message) || message.source !== SOURCE ||
      typeof message.modelId !== "string" || !message.modelId ||
      typeof message.viewId !== "string" || !message.viewId ||
      !["mount", "unmount", "event"].includes(String(message.type))
    ) return;
    const sessionId = this.options.session(owner);
    let mounts = this.#mounts.get(owner);
    if (mounts && mounts.sessionId !== sessionId) {
      this.#mounts.delete(owner);
      this.#active.delete(owner);
      this.#release(owner);
      mounts = undefined;
    }
    if (!sessionId) return;
    if (message.type === "mount") {
      mounts ??= { sessionId, views: new Map() };
      mounts.views.set(message.viewId, message.modelId);
      this.#mounts.set(owner, mounts);
    } else if (mounts?.views.get(message.viewId) !== message.modelId) return;
    if (message.type === "unmount") {
      mounts?.views.delete(message.viewId);
      if (mounts?.views.size === 0) this.#mounts.delete(owner);
    }
    if (!this.options.visible(owner)) {
      this.#active.delete(owner);
      this.#release(owner);
      if (message.type === "mount") {
        this.options.log("Form mount deferred until its editor is visible.");
        this.options.post(owner, {
          source: SOURCE,
          type: "closed",
          modelId: message.modelId,
          code: "form_view_inactive",
        });
      }
      return;
    }
    this.#active.add(owner);
    await this.#registry.handleRendererMessage(owner, sessionId, message);
    if (!this.#mounts.has(owner)) this.#active.delete(owner);
  }

  /** Suspend disappeared editors and restore their registered views when they become visible again. */
  async updateVisible(owners: readonly Owner[]): Promise<void> {
    if (this.#closed) return;
    const pending: Promise<void>[] = [];
    for (const owner of this.#active) {
      if (this.options.visible(owner)) continue;
      this.#active.delete(owner);
      pending.push(this.#registry.releaseWhere((candidate) => candidate === owner));
    }
    for (const owner of owners) {
      if (!this.options.visible(owner)) continue;
      if (this.#active.has(owner)) continue;
      const mounts = this.#mounts.get(owner);
      if (!mounts) continue;
      if (mounts.sessionId !== this.options.session(owner)) {
        this.#mounts.delete(owner);
        continue;
      }
      this.#active.add(owner);
      for (const [viewId, modelId] of mounts.views) {
        pending.push(
          this.#registry.handleRendererMessage(owner, mounts.sessionId, {
            source: SOURCE,
            type: "mount",
            viewId,
            modelId,
          }),
        );
      }
    }
    await joinUiScopes(pending);
  }

  async releaseWhere(predicate: (owner: Owner, sessionId: string) => boolean): Promise<void> {
    for (const owner of this.#active) {
      const mounts = this.#mounts.get(owner);
      if (!mounts || !predicate(owner, mounts.sessionId)) continue;
      this.#mounts.delete(owner);
      this.#active.delete(owner);
    }
    await this.#registry.releaseWhere(predicate);
  }

  dispose(): Promise<void> {
    this.#closed = true;
    this.#mounts = new WeakMap();
    this.#active.clear();
    return this.#registry.dispose();
  }

  #release(owner: Owner): void {
    void this.#registry.releaseWhere((candidate) => candidate === owner).catch(() =>
      this.options.log("Form view cleanup failed.")
    );
  }
}
