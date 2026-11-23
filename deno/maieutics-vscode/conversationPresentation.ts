import type { FrontendClient } from "./client.ts";
import { OutputRenderingBridge } from "./outputRendering.ts";
import { FormActorGateway } from "./formActorGateway.ts";
// @deno-types="./runtime/formActorHost.d.ts"
import { FormActorHost } from "./runtime/formActorHost.js";
import { UiBridgeViews } from "./uiBridge.ts";
import { waitForStartup } from "./startupWait.ts";

interface Options {
  readonly sessionId: string;
  readonly signal: AbortSignal;
  client(): Promise<FrontendClient>;
  post(message: unknown): Promise<void>;
  externalUri(url: string): Promise<string>;
  writeClipboard(text: string): Promise<void>;
  log(message: string): void;
}

/** One document generation reuses the Notebook's rendering and binary form bridges. */
export class ConversationPresentation {
  private readonly gateway: FormActorGateway;
  private readonly rendering: OutputRenderingBridge;
  private readonly actors: FormActorHost<object>;
  private readonly forms: UiBridgeViews<object>;
  private readonly owner = {};
  private closing: Promise<void> | undefined;
  constructor(private readonly options: Options) {
    this.gateway = new FormActorGateway(options);
    this.rendering = new OutputRenderingBridge({
      writeClipboard: options.writeClipboard,
      render: async (documentId, text, cancellation, template, vue, assistant, progressive) => {
        const signal = AbortSignal.any([cancellation, options.signal]);
        const client = await waitForStartup(options.client(), signal);
        const capabilities = await client.capabilities(signal);
        if (
          capabilities.rendering?.version !== 1 ||
          (template && capabilities.rendering.templateVersion !== 1) ||
          (vue && capabilities.rendering.vueVersion !== 1)
        ) return undefined;
        return await client.renderMarkdown(
          documentId,
          text,
          signal,
          template,
          vue,
          assistant && capabilities.rendering.assistantVersion === 1 &&
            (!progressive?.streaming || capabilities.rendering.progressiveVersion === 1),
          capabilities.rendering.progressiveVersion === 1 ? progressive : undefined,
        );
      },
      post: options.post,
      log: options.log,
    });
    this.actors = new FormActorHost({
      session: () => options.sessionId,
      visible: () => !options.signal.aborted,
      handle: (owner, message) => this.forms.handleRendererMessage(owner, message),
      release: (owner) => this.forms.releaseWhere((candidate) => candidate === owner),
      post: (_owner, message) => options.post(message),
      listen: (_owner, signal, accept) => this.gateway.listen(signal, accept),
      log: options.log,
    });
    this.forms = new UiBridgeViews({
      session: () => options.sessionId,
      visible: () => !options.signal.aborted,
      connect: async (sessionId, cancellation) => {
        const signal = AbortSignal.any([cancellation, options.signal]);
        const client = await waitForStartup(options.client(), signal);
        const capabilities = await client.capabilities(signal);
        if (!capabilities.comm) throw new Error("comm_unavailable");
        return client.commSocket(sessionId, { signal });
      },
      post: (owner, message) => this.actors.post(owner, message),
      log: options.log,
    });
  }
  connectionSource(): Promise<string> {
    return this.gateway.connectionSource(this.options.signal);
  }
  async receive(message: unknown): Promise<void> {
    if (this.options.signal.aborted) return;
    if (typeof message !== "object" || !message) return;
    if ("source" in message && message.source === "maieutics-form-actor") {
      this.actors.receive(this.owner, message);
    } else await this.rendering.handle(message, { waitForIdle: false });
  }
  dispose(): Promise<void> {
    this.closing ??= (async () => {
      const results = await Promise.allSettled([
        this.rendering.dispose(),
        this.actors.dispose(),
        this.forms.dispose(),
        this.gateway.dispose(),
      ]);
      const errors = results.flatMap((result) =>
        result.status === "rejected" ? [result.reason] : []
      );
      if (errors.length) {
        throw new AggregateError(errors, "Conversation presentation cleanup failed");
      }
    })();
    return this.closing;
  }
}
