/** Explicit, versioned data snapshot from a template rich display. */
export interface TemplateContext {
  version: 1;
  bindings: readonly {
    name: string;
    state: "pending" | "ready" | "failed" | "cancelled";
    value?: unknown;
    undefined?: boolean;
  }[];
  sourceSealed?: boolean;
}

export interface VueRequest {
  version: 1;
  format: "markdown" | "sfc";
}

export const TemplateOutputMime = "application/vnd.maieutics.template+json";

/** Stateless SSR snapshots returned by the optional kernel presentation service. */
export interface RenderedMarkdown {
  version: 1;
  documentId: string;
  html: string;
  css: string;
  vue?: { version: 1; code: string; scopeId: string };
}

interface RenderRequest {
  source: "maieutics-output";
  type: "render";
  requestId: string;
  documentId: string;
  text: string;
  template?: TemplateContext;
  vue?: VueRequest;
  assistant?: boolean;
  streaming?: boolean;
}

interface OutputRenderingOptions {
  render(
    documentId: string,
    text: string,
    signal: AbortSignal,
    template?: TemplateContext,
    vue?: VueRequest,
    assistant?: boolean,
  ): Promise<RenderedMarkdown | undefined>;
  post(message: unknown): Promise<unknown>;
  log(message: string): void;
}

/** One owned request pump; pending updates coalesce per document and never queue every token. */
export class OutputRenderingBridge {
  readonly #pending = new Map<string, RenderRequest>();
  readonly #latest = new Map<string, RenderRequest>();
  #active: { documentId: string; requestId: string; controller: AbortController } | undefined;
  #pump: Promise<void> | undefined;
  #closed = false;

  constructor(private readonly options: OutputRenderingOptions) {}

  async handle(message: unknown): Promise<void> {
    if (this.#closed || typeof message !== "object" || message === null) return;
    const value = message as Record<string, unknown>;
    if (value.source !== "maieutics-output" || typeof value.documentId !== "string") return;
    if (value.type === "applied" && value.engine === "native") {
      if (
        typeof value.native === "object" && value.native !== null && "error" in value.native &&
        typeof value.native.error === "string"
      ) {
        this.options.log("Native Markdown rendering failed; displaying plain source.");
      }
      return;
    }
    if (value.type === "cancel") {
      if (
        value.requestId === undefined ||
        this.#latest.get(value.documentId)?.requestId === value.requestId
      ) {
        this.#pending.delete(value.documentId);
        this.#latest.delete(value.documentId);
      }
      if (
        this.#active?.documentId === value.documentId &&
        (value.requestId === undefined || this.#active.requestId === value.requestId)
      ) {
        this.#active.controller.abort();
      }
      return;
    }
    if (
      value.type !== "render" || typeof value.requestId !== "string" ||
      value.requestId.length > 128 || typeof value.text !== "string" ||
      value.text.length > 200_000 ||
      !/^[a-z][a-z\d_-]{0,63}$/i.test(value.documentId)
    ) return;
    if (value.template !== undefined) {
      if (typeof value.template !== "object" || value.template === null) return;
      const template = value.template as Partial<TemplateContext>;
      if (
        template.version !== 1 || !Array.isArray(template.bindings) ||
        template.bindings.length > 128
      ) return;
      try {
        if (JSON.stringify(template).length > 128_000) return;
      } catch {
        return;
      }
    }
    if (value.vue !== undefined) {
      const vue = value.vue as Partial<VueRequest> | null;
      if (
        typeof vue !== "object" || vue === null || vue.version !== 1 ||
        !["markdown", "sfc"].includes(vue.format ?? "") || value.template !== undefined
      ) return;
    }
    if (
      value.assistant !== undefined &&
      (value.assistant !== true || value.template !== undefined || value.vue !== undefined)
    ) return;
    if (
      value.streaming !== undefined &&
      (value.streaming !== true || value.assistant !== undefined ||
        value.vue !== undefined || value.template !== undefined)
    ) return;
    const request = value as unknown as RenderRequest;
    if (this.#pending.size >= 64 && !this.#pending.has(request.documentId)) {
      await this.options.post({ ...this.#identity(request), type: "unavailable" });
      return;
    }
    this.#latest.set(request.documentId, request);
    this.#pending.set(request.documentId, request);
    this.#startPump();
    await this.#pump;
  }

  async dispose(): Promise<void> {
    this.#closed = true;
    this.#pending.clear();
    this.#latest.clear();
    this.#active?.controller.abort();
    await this.#pump;
  }

  async #run(): Promise<void> {
    // Allow the owner to assign #pump before a synchronous failure or an empty queue settles it.
    await Promise.resolve();
    while (!this.#closed && this.#pending.size > 0) {
      const request = this.#pending.values().next().value;
      if (request === undefined) break;
      this.#pending.delete(request.documentId);
      const controller = new AbortController();
      this.#active = { documentId: request.documentId, requestId: request.requestId, controller };
      let result: RenderedMarkdown | undefined;
      try {
        result = await this.options.render(
          request.documentId,
          request.text,
          controller.signal,
          request.template,
          request.vue,
          request.assistant,
        );
      } catch (error) {
        if (!controller.signal.aborted) {
          // Log only the failure category; source text and response payloads are untrusted.
          this.options.log(
            `Output rendering unavailable (${error instanceof Error ? error.name : "error"}).`,
          );
        }
      }
      try {
        const latest = this.#latest.get(request.documentId);
        // A finished prefix is useful progress while an append-only stream continues. Edits,
        // bindings, completion and executable Vue still require the exact latest request.
        const progress = result !== undefined && result.vue === undefined &&
          request.streaming === true && latest?.streaming === true &&
          latest.text.startsWith(request.text);
        if (
          !this.#closed && !controller.signal.aborted &&
          (latest?.requestId === request.requestId || progress)
        ) {
          await this.options.post(
            result === undefined ? { ...this.#identity(request), type: "unavailable" } : {
              ...this.#identity(request),
              type: "rendered",
              html: result.html,
              css: result.css,
              ...(request.streaming ? { streaming: true, renderedSource: request.text } : {}),
              ...(result.vue ? { vue: result.vue } : {}),
            },
          );
        }
      } catch {
        this.options.log("Output renderer message delivery failed.");
      } finally {
        if (this.#latest.get(request.documentId)?.requestId === request.requestId) {
          this.#latest.delete(request.documentId);
        }
        this.#active = undefined;
      }
    }
  }

  #startPump(): void {
    this.#pump ??= this.#run().finally(() => {
      this.#pump = undefined;
      if (!this.#closed && this.#pending.size > 0) this.#startPump();
    });
  }

  #identity(request: RenderRequest) {
    return {
      source: "maieutics-output",
      documentId: request.documentId,
      requestId: request.requestId,
    };
  }
}
