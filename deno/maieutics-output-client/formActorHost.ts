import type { ActorConnection, MessageTransport } from "@maieutics/plugin-sdk/actor/browser";
import {
  ActorTransportError,
  attachment,
  BINARY_GRAPH_CODEC,
  binaryFrame,
  binaryFrameSize,
  createActorConnection,
  envelope,
  FORM_ACTOR_LIMIT,
  type FormActions,
  formBinaryCodec,
  FormPosts,
  type FormUpdates,
  frame,
  record,
  webSocketTransport,
} from "./formActorProtocol.ts";
import { messageTransport } from "./formActorProtocol.ts";
import type { FormActorHostOptions } from "./formActorHost.d.ts";
export type { FormActorHostOptions } from "./formActorHost.d.ts";

interface Scope<Owner> {
  readonly owner: Owner;
  readonly sessionId: string;
  readonly epoch: number;
  readonly connectionId: string;
  readonly views: Map<string, string>;
  readonly posts: FormPosts;
  readonly pending: Set<Promise<void>>;
  readonly finished: PromiseWithResolvers<void>;
  readonly lifetime: AbortController;
  timer?: ReturnType<typeof setTimeout>;
  transport?: MessageTransport;
  connection?: ActorConnection<{ view: FormUpdates }>;
  retiring?: Promise<void>;
}

/** One capability connection per visible, pinned editor; no transport or session comes from the peer. */
export class FormActorHost<Owner extends object> {
  readonly #scopes = new Map<Owner, Scope<Owner>>();
  readonly #epochs = new WeakMap<Owner, number>();
  readonly #waiting = new WeakMap<Owner, {
    sessionId: string;
    epoch?: number;
    connectionId?: string;
  }>();
  readonly #controlPosts = new FormPosts();
  readonly #retiring = new Set<Promise<void>>();
  readonly #failures: unknown[] = [];
  #closing: Promise<void> | undefined;
  #closed = false;

  constructor(private readonly options: FormActorHostOptions<Owner>) {}

  /** Accept only the versioned Actor envelope from this editor's renderer. */
  receive(owner: Owner, message: unknown): void {
    if (this.#closed || !envelope(message)) return;
    if (message.type === "request") {
      this.#offer(owner);
      return;
    }
    const scope = this.#scopes.get(owner);
    if (!scope || message.epoch !== scope.epoch || message.connectionId !== scope.connectionId) {
      return;
    }
    if (!this.#authorized(scope)) return;
    if (message.type === "release") {
      this.#retire(scope, "idle");
      return;
    }
    if (message.type !== "frame") return;
    if (this.options.listen) {
      this.#retire(scope, "protocol");
      return;
    }
    if (!scope.connection) {
      let hello: unknown;
      try {
        hello = formBinaryCodec.decode(binaryFrame(message.value));
      } catch {
        this.#retire(scope, "decode");
        return;
      }
      if (!record(hello) || hello.type !== "actor:hello") {
        this.#retire(scope, "protocol");
        return;
      }
      clearTimeout(scope.timer);
      const transport = messageTransport({
        maxChannels: 8,
        maxPendingMessages: FORM_ACTOR_LIMIT * 4,
        messageSize: binaryFrameSize,
        decode: (value, signal) => formBinaryCodec.decode(binaryFrame(value), signal),
        send: (value) =>
          this.#post(
            scope,
            frame("frame", {
              ...this.#identity(scope),
              value: formBinaryCodec.encode(value),
            }),
          ),
      });
      this.#start(scope, transport);
    }
    try {
      scope.transport?.deliver(binaryFrame(message.value));
    } catch {
      this.#retire(scope, "decode");
    }
  }

  #start(scope: Scope<Owner>, transport: MessageTransport): void {
    const owner = scope.owner;
    const actions: FormActions = {
      mount: async (modelId, viewId) => {
        this.#check(scope, modelId, viewId);
        if (!scope.views.has(viewId) && scope.views.size >= FORM_ACTOR_LIMIT) {
          throw new ActorTransportError("capacity", "Form Actor attachment budget exceeded");
        }
        scope.views.set(viewId, modelId);
        await this.options.handle(owner, {
          source: "maieutics-form",
          type: "mount",
          modelId,
          viewId,
        });
      },
      unmount: async (modelId, viewId) => {
        this.#check(scope, modelId, viewId);
        if (scope.views.get(viewId) !== modelId) return;
        scope.views.delete(viewId);
        await this.options.handle(owner, {
          source: "maieutics-form",
          type: "unmount",
          modelId,
          viewId,
        });
      },
      event: async (modelId, viewId, name, payload) => {
        this.#check(scope, modelId, viewId);
        if (scope.views.get(viewId) !== modelId || !["submit", "cancel"].includes(name)) {
          throw new ActorTransportError("protocol", "Form action has no mounted attachment");
        }
        await this.options.handle(owner, {
          source: "maieutics-form",
          type: "event",
          modelId,
          viewId,
          name,
          payload,
        });
      },
    };
    scope.transport = transport;
    scope.connection = createActorConnection(transport, {
      actors: { forms: actions },
      maxPendingCalls: FORM_ACTOR_LIMIT,
    });
    const connection = scope.connection;
    if (scope.retiring) connection.close();
    this.#observe(
      scope,
      connection.closed.then(() => {
        this.#retire(scope, "closed");
      }),
    );
    this.#observe(
      scope,
      connection.ready.catch((error) => {
        this.#log(`Form Actor handshake failed: ${error}`);
        this.#retire(scope, "protocol");
      }),
    );
  }

  /** Send a complete model projection only to this editor's current browser generation. */
  post(owner: Owner, message: unknown): void {
    const scope = this.#scopes.get(owner);
    if (!scope?.connection || !this.#authorized(scope)) return;
    const connection = scope.connection;
    this.#observe(
      scope,
      connection.ready.then(async () => {
        if (!scope.retiring) await connection.actor("view").receive(message);
      }).catch((error) => {
        this.#log(`Form Actor state delivery failed: ${error}`);
        this.#retire(scope, "closed");
      }),
    );
  }

  /** Hidden editors release all strong ownership; visibility offers a fresh attachment generation. */
  updateVisible(owners: readonly Owner[]): void {
    for (const scope of this.#scopes.values()) {
      if (!this.options.visible(scope.owner)) {
        this.#wait(scope);
        this.#retire(scope, "hidden");
      }
    }
    for (const owner of owners) {
      if (this.#waiting.has(owner)) this.#offer(owner);
    }
  }

  /** Revoke old-session capabilities before cancelling their comm scopes. */
  async releaseWhere(predicate: (owner: Owner, sessionId: string) => boolean): Promise<void> {
    const pending: Promise<void>[] = [];
    for (const scope of this.#scopes.values()) {
      if (predicate(scope.owner, scope.sessionId)) {
        this.#waiting.delete(scope.owner);
        pending.push(this.#retire(scope, "session_changed"));
      }
    }
    await Promise.all(pending);
  }

  /** Stop admission and join active connections, retired generations and asynchronous delivery. */
  dispose(): Promise<void> {
    this.#closed = true;
    this.#closing ??= (async () => {
      for (const scope of this.#scopes.values()) this.#retire(scope, "disposed");
      while (this.#retiring.size) await Promise.all([...this.#retiring]);
      try {
        await this.#controlPosts.join();
      } catch (error) {
        this.#failures.push(error);
      }
      if (this.#failures.length) {
        throw new AggregateError(this.#failures, "Form Actor host cleanup failed");
      }
    })();
    return this.#closing;
  }

  #identity(scope: Scope<Owner>) {
    return { epoch: scope.epoch, connectionId: scope.connectionId };
  }
  #offer(owner: Owner): void {
    const sessionId = this.options.session(owner);
    const previous = this.#waiting.get(owner);
    if (previous && previous.sessionId !== sessionId) {
      this.#waiting.delete(owner);
      this.#controlPost(
        owner,
        frame(previous.epoch === undefined ? "unavailable" : "closed", {
          epoch: previous.epoch,
          connectionId: previous.connectionId,
          code: "session_changed",
        }),
      );
      return;
    }
    if (!sessionId) {
      this.#controlPost(owner, frame("unavailable", { code: "form_session_unbound" }));
      return;
    }
    if (this.#scopes.has(owner)) return;
    if (!this.options.visible(owner)) {
      this.#waiting.set(owner, previous ?? { sessionId });
      return;
    }
    if (this.#scopes.size + this.#retiring.size >= FORM_ACTOR_LIMIT) {
      this.#log("Form Actor editor rejected: form_scope_limit");
      this.#controlPost(owner, frame("unavailable", { code: "form_scope_limit" }));
      return;
    }
    this.#waiting.delete(owner);
    const epoch = (this.#epochs.get(owner) ?? 0) + 1;
    this.#epochs.set(owner, epoch);
    const scope: Scope<Owner> = {
      owner,
      sessionId,
      epoch,
      connectionId: crypto.randomUUID(),
      views: new Map(),
      posts: new FormPosts(),
      pending: new Set(),
      finished: Promise.withResolvers<void>(),
      lifetime: new AbortController(),
    };
    this.#scopes.set(owner, scope);
    scope.timer = setTimeout(() => this.#retire(scope, "timeout"), 10_000);
    if (this.options.listen) {
      this.#observe(
        scope,
        this.options.listen(owner, scope.lifetime.signal, (socket) => {
          if (!this.#authorized(scope) || scope.connection) {
            socket.close();
            return;
          }
          clearTimeout(scope.timer);
          try {
            this.#start(
              scope,
              webSocketTransport(socket, {
                codec: formBinaryCodec,
                maxChannels: 8,
                maxPendingMessages: FORM_ACTOR_LIMIT * 4,
              }),
            );
          } catch (error) {
            socket.close();
            this.#log(`Form Actor binary admission failed: ${error}`);
            this.#retire(scope, "protocol");
          }
        }).then((endpoint) => {
          if (this.#authorized(scope)) {
            this.#post(
              scope,
              frame("offer", {
                ...this.#identity(scope),
                endpoint,
                encoding: BINARY_GRAPH_CODEC,
              }),
            );
          }
        }).catch((error) => {
          if (!scope.retiring) {
            this.#log(`Form Actor binary endpoint failed: ${error}`);
            this.#retire(scope, "connect");
          }
        }),
      );
    } else {this.#post(
        scope,
        frame("offer", { ...this.#identity(scope), encoding: BINARY_GRAPH_CODEC }),
      );}
  }
  #authorized(scope: Scope<Owner>): boolean {
    if (scope.retiring || this.#scopes.get(scope.owner) !== scope) return false;
    if (this.options.session(scope.owner) !== scope.sessionId) {
      this.#retire(scope, "session_changed");
      return false;
    }
    if (!this.options.visible(scope.owner)) {
      this.#wait(scope);
      this.#retire(scope, "hidden");
      return false;
    }
    return true;
  }
  #check(scope: Scope<Owner>, modelId: unknown, viewId: unknown): void {
    attachment(modelId, viewId);
    if (!this.#authorized(scope)) {
      throw new ActorTransportError("closed", "Form Actor editor capability was revoked");
    }
  }
  #wait(scope: Scope<Owner>): void {
    this.#waiting.set(scope.owner, { sessionId: scope.sessionId, ...this.#identity(scope) });
  }
  #controlPost(owner: Owner, message: unknown): void {
    this.#controlPosts.send(message, (value) => this.options.post(owner, value), (error) => {
      this.#log(`Form Actor admission feedback failed: ${error}`);
    });
  }
  #post(scope: Scope<Owner>, message: unknown): void {
    scope.posts.send(message, (value) => this.options.post(scope.owner, value), (error) => {
      this.#log(`Form Actor post failed: ${error}`);
      this.#retire(scope, "send");
    });
  }
  #log(message: string): void {
    try {
      this.options.log(message);
    } catch (error) {
      if (this.#failures.length < FORM_ACTOR_LIMIT) this.#failures.push(error);
    }
  }
  #observe(scope: Scope<Owner>, operation: Promise<void>): void {
    if (scope.pending.size >= FORM_ACTOR_LIMIT * 2 + 2) {
      this.#retire(scope, "capacity");
    }
    const observed = operation.catch((error) => {
      if (this.#failures.length < FORM_ACTOR_LIMIT) this.#failures.push(error);
      this.#retire(scope, "closed");
    }).finally(() => scope.pending.delete(observed));
    scope.pending.add(observed);
  }
  #retire(scope: Scope<Owner>, code: string): Promise<void> {
    if (scope.retiring) return scope.retiring;
    const finished = scope.finished;
    scope.retiring = finished.promise;
    this.#scopes.delete(scope.owner);
    clearTimeout(scope.timer);
    this.#post(scope, frame("closed", { ...this.#identity(scope), code }));
    scope.connection?.close();
    scope.transport?.close();
    scope.lifetime.abort();
    // Release cancels pending mount/connect work before connection.completion joins its dispatches.
    let release: Promise<void>;
    try {
      release = this.options.release(scope.owner);
    } catch (error) {
      release = Promise.reject(error);
    }
    const cleanup = (async () => {
      const results = await Promise.allSettled([release, scope.connection?.completion]);
      while (scope.pending.size) await Promise.all([...scope.pending]);
      results.push(...await Promise.allSettled([scope.posts.join()]));
      for (const result of results) {
        if (result.status === "rejected" && this.#failures.length < FORM_ACTOR_LIMIT) {
          this.#failures.push(result.reason);
          this.#log("Form Actor generation cleanup failed.");
        }
      }
      scope.views.clear();
    })().finally(() => {
      this.#retiring.delete(cleanup);
      finished.resolve();
    });
    this.#retiring.add(cleanup);
    return scope.retiring;
  }
}
