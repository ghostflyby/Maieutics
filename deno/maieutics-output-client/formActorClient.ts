import type { ActorConnection, MessageTransport } from "@maieutics/plugin-sdk/actor/browser";
import {
  ActorTransportError,
  attachment,
  createActorConnection,
  envelope,
  FORM_ACTOR_LIMIT,
  type FormActions,
  type FormAttachment,
  formFrameSize,
  FormPosts,
  frame,
  identity,
  messageTransport,
} from "./formActorProtocol.ts";
import type { FormMessage } from "./forms.ts";

interface BrowserScope {
  readonly epoch: number;
  readonly connectionId: string;
  readonly transport: MessageTransport;
  readonly posts: FormPosts;
  connection?: ActorConnection<{ forms: FormActions }>;
  readonly mounted: Map<string, FormAttachment>;
  readonly pending: Set<Promise<void>>;
  retiring?: Promise<void>;
}
export interface FormActorClientOptions {
  postMessage(message: unknown): void | PromiseLike<unknown>;
  receive(message: unknown): Promise<void>;
  log(message: string): void;
}

/** Browser attachment owner. Offers come from the editor host, never from saved output. */
export class FormActorClient {
  readonly #views = new Map<string, FormAttachment>();
  readonly #tasks = new Set<Promise<void>>();
  readonly #posts = new FormPosts();
  readonly #failures: unknown[] = [];
  #scope: BrowserScope | undefined;
  #epoch = 0;
  #connectionId: string | undefined;
  #requested = false;
  #closed = false;
  #closing: Promise<void> | undefined;

  constructor(private readonly options: FormActorClientOptions) {}

  hasView(viewId: string): boolean {
    return this.#views.has(viewId);
  }

  /** Called by the shared Vue form renderer; actions are never replayed after reconnect. */
  send(message: FormMessage): void {
    if (this.#closed) return;
    const { modelId, viewId } = message;
    attachment(modelId, viewId);
    if (message.type === "mount") {
      if (!this.#views.has(viewId) && this.#views.size >= FORM_ACTOR_LIMIT) {
        throw new ActorTransportError("capacity", "Form Actor attachment budget exceeded");
      }
      this.#views.set(viewId, { modelId, viewId });
      if (this.#scope) this.#mount(this.#scope, modelId, viewId);
      else if (!this.#requested) {
        this.#requested = true;
        this.#posts.send(frame("request"), this.options.postMessage, (error) => {
          this.#requested = false;
          this.#log(`Form Actor request failed: ${error}`);
          this.#freeze();
        });
      }
      return;
    }
    const view = this.#views.get(viewId);
    if (view?.modelId !== modelId) return;
    const scope = this.#scope;
    if (message.type === "unmount") {
      this.#views.delete(viewId);
      if (!scope) return;
      // Start immediately, so removing the DOM cannot leave an attachment alive behind a queued action.
      this.#observe(
        scope,
        (async () => {
          await scope.connection?.ready;
          if (scope.retiring || !scope.connection) return;
          if (scope.mounted.get(viewId) === view) scope.mounted.delete(viewId);
          await scope.connection.actor("forms").unmount(modelId, viewId);
          if (!this.#views.size && this.#scope === scope) this.#retire(scope, true);
        })(),
      );
    } else if (message.type === "event" && scope?.mounted.get(viewId) === view) {
      this.#observe(
        scope,
        (async () => {
          if (scope.retiring || !scope.connection) return;
          await scope.connection.actor("forms").event(
            modelId,
            viewId,
            message.name as "submit" | "cancel",
            message.payload,
          );
        })(),
      );
    }
  }

  /** Process only host-issued offers and frames belonging to the current connection generation. */
  receive(message: unknown): void {
    if (this.#closed || !envelope(message)) return;
    if (message.type === "unavailable" && this.#requested) {
      this.#requested = false;
      this.#log(`Form Actor unavailable: ${String(message.code)}`);
      this.#freeze();
      this.#views.clear();
      return;
    }
    if (
      message.type === "offer" && Number.isSafeInteger(message.epoch) &&
      (message.epoch as number) > this.#epoch && identity(message.connectionId)
    ) {
      if (this.#scope) this.#retire(this.#scope, false);
      this.#epoch = message.epoch as number;
      this.#requested = false;
      const connectionId = message.connectionId;
      this.#connectionId = connectionId;
      const epoch = this.#epoch;
      const posts = new FormPosts();
      const transport = messageTransport({
        maxChannels: 8,
        messageSize: formFrameSize,
        send: (value) =>
          posts.send(
            frame("frame", { epoch, connectionId, value }),
            this.options.postMessage,
            (error) => {
              this.#log(`Form Actor delivery failed: ${error}`);
              transport.close(error);
            },
          ),
      });
      const scope: BrowserScope = {
        epoch,
        connectionId,
        transport,
        posts,
        mounted: new Map(),
        pending: new Set(),
      };
      this.#scope = scope;
      scope.connection = createActorConnection(transport, {
        actors: {
          view: {
            receive: async (value: unknown) => {
              if (this.#scope === scope && !scope.retiring) await this.options.receive(value);
            },
          },
        },
        maxPendingCalls: FORM_ACTOR_LIMIT,
      });
      const connection = scope.connection;
      if (scope.retiring) connection.close();
      this.#observe(
        scope,
        connection.closed.then(() => {
          this.#retire(scope, true);
        }),
      );
      for (const { modelId, viewId } of this.#views.values()) this.#mount(scope, modelId, viewId);
      if (!this.#views.size) this.#retire(scope, true);
      return;
    }
    if (message.epoch !== this.#epoch || message.connectionId !== this.#connectionId) return;
    const scope = this.#scope;
    if (message.type === "closed") {
      if (scope) this.#retire(scope, false);
      if (message.code !== "hidden") this.#views.clear();
    } else if (message.type === "frame") scope?.transport.deliver(message.value);
  }

  /** Terminal disposal joins in-flight calls, old generations and postMessage promises. */
  dispose(): Promise<void> {
    this.#closed = true;
    this.#closing ??= (async () => {
      if (this.#scope) this.#retire(this.#scope, true);
      this.#views.clear();
      while (this.#tasks.size) await Promise.all([...this.#tasks]);
      const results = await Promise.allSettled([this.#posts.join()]);
      for (const result of results) {
        if (result.status === "rejected") this.#failures.push(result.reason);
      }
      if (this.#failures.length) {
        throw new AggregateError(this.#failures, "Form Actor browser cleanup failed");
      }
    })();
    return this.#closing;
  }

  #mount(scope: BrowserScope, modelId: string, viewId: string): void {
    const view = this.#views.get(viewId);
    if (!view || scope.mounted.get(viewId) === view) return;
    scope.mounted.set(viewId, view);
    this.#observe(
      scope,
      (async () => {
        await scope.connection?.ready;
        if (
          !scope.retiring && this.#scope === scope && this.#views.get(viewId) === view
        ) await scope.connection?.actor("forms").mount(modelId, viewId);
      })(),
    );
  }
  #freeze(): void {
    for (const modelId of new Set([...this.#views.values()].map((view) => view.modelId))) {
      try {
        const update = this.options.receive({ source: "maieutics-form", type: "closed", modelId });
        this.#own(update);
      } catch (error) {
        if (this.#failures.length < FORM_ACTOR_LIMIT) this.#failures.push(error);
        this.#log("Form Actor view closure failed.");
      }
    }
  }
  #own(task: Promise<void>): void {
    const observed = task.catch((error) => {
      if (this.#failures.length < FORM_ACTOR_LIMIT) this.#failures.push(error);
      this.#log("Form Actor browser task failed.");
    }).finally(() => this.#tasks.delete(observed));
    this.#tasks.add(observed);
  }
  #log(message: string): void {
    try {
      this.options.log(message);
    } catch (error) {
      if (this.#failures.length < FORM_ACTOR_LIMIT) this.#failures.push(error);
    }
  }
  #observe(scope: BrowserScope, task: Promise<void>): void {
    if (scope.pending.size >= FORM_ACTOR_LIMIT * 2 + 2) this.#retire(scope, true);
    const observed = task.catch((error) => {
      if (!scope.retiring) {
        this.#log(`Form Actor call failed: ${error}`);
        this.#retire(scope, true);
      }
    }).finally(() => scope.pending.delete(observed));
    scope.pending.add(observed);
  }
  #retire(scope: BrowserScope, notifyHost: boolean): Promise<void> {
    if (scope.retiring) return scope.retiring;
    const finished = Promise.withResolvers<void>();
    scope.retiring = finished.promise;
    if (this.#scope === scope) {
      this.#scope = undefined;
      this.#freeze();
    }
    if (notifyHost) {
      scope.posts.send(
        frame("release", {
          epoch: scope.epoch,
          connectionId: scope.connectionId,
        }),
        this.options.postMessage,
        (error) => this.#log(`Form Actor release delivery failed: ${error}`),
      );
    }
    scope.connection?.close();
    const cleanup = (async () => {
      const results = await Promise.allSettled([scope.connection?.completion]);
      while (scope.pending.size) await Promise.all([...scope.pending]);
      results.push(...await Promise.allSettled([scope.posts.join()]));
      for (const result of results) {
        if (result.status === "rejected" && this.#failures.length < FORM_ACTOR_LIMIT) {
          this.#failures.push(result.reason);
          this.#log("Form Actor browser generation cleanup failed.");
        }
      }
    })().finally(() => finished.resolve());
    this.#own(cleanup);
    return scope.retiring;
  }
}
