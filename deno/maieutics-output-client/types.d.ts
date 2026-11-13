/// <reference lib="dom" />

/** Selecting assistant/vue is an explicit host execution decision, never a persisted trust flag. */
export interface Output {
  readonly id: string;
  readonly kind: "turn" | "assistant" | "vue" | "template";
  readonly value: unknown;
  readonly metadata?: unknown;
}

/** Context for an optional host Markdown renderer; cancellation must stop pending preparation. */
export interface MarkdownContext {
  readonly outputId: string;
  readonly index: number;
  readonly metadata?: unknown;
  readonly signal: AbortSignal;
}

/** Outbound browser presentation messages; the owner connects these to its authenticated backend. */
export type OutputMessage =
  | {
    readonly source: "maieutics-output";
    readonly type: "render";
    readonly documentId: string;
    readonly requestId: string;
    readonly text: string;
    readonly template?: unknown;
    readonly vue?: { readonly version: 1; readonly format: "markdown" | "sfc" };
    readonly assistant?: true;
    readonly progressive?: true;
    readonly streaming?: true;
  }
  | {
    readonly source: "maieutics-output";
    readonly type: "cancel";
    readonly documentId: string;
    readonly requestId?: string;
  }
  | {
    readonly source: "maieutics-output";
    readonly type: "copy";
    readonly requestId: string;
    readonly text: string;
  }
  | {
    readonly source: "maieutics-output";
    readonly type: "applied";
    readonly documentId: string;
    readonly requestId: string;
    readonly engine: "native" | "ssr" | "vue";
    readonly native?: {
      readonly headings: number;
      readonly tables: number;
      readonly error?: string;
    };
  };

/** Host services. No VS Code APIs, credentials, network or clipboard globals are captured. */
export interface OutputRendererOptions {
  /** Dispatch synchronously; the host owns asynchronous delivery and observes its failures. */
  sendMessage(message: OutputMessage): void;
  /** Optional native Markdown adapter; the default is passive, bundled Markdown-it. */
  renderMarkdown?(
    text: string,
    element: HTMLElement,
    context: MarkdownContext,
  ): void | Promise<void>;
  /** Handle explicitly rendered action links, such as the notebook retry command. */
  onAction?(href: string): void;
}

/** One browser rendering scope. Hosts own transport subscriptions and must release this scope. */
export interface OutputRenderer {
  /** Prepare fallback and submit rendering; backend commits arrive separately through receive. */
  render(output: Output, element: HTMLElement, signal?: AbortSignal): Promise<void>;
  /** Apply a backend reply. Unknown, stale and disposed messages are ignored. */
  receive(message: unknown): Promise<void>;
  /** Release one output, or every current output when omitted; the scope remains usable. */
  disposeOutput(id?: string): void;
  /** Abort pending preparation and unmount every component; idempotent and terminal. */
  dispose(): void;
}

/** Owned component island. Only use code supplied by the authenticated presentation backend. */
export interface VueIslandDisposer {
  (): void;
  update?(setupKey: string, renderCode: string): Promise<boolean>;
}

/** Low-level hydration for hosts that already own SSR DOM and verified compiled component code. */
export declare function mountVueIsland(
  container: HTMLElement,
  code: string,
  progressive?: { setupKey: string; renderCode: string },
  http?: PluginHttpClient,
): VueIslandDisposer;

/** Create an independently owned browser renderer. */
export declare function createOutputRenderer(options: OutputRendererOptions): OutputRenderer;
/** Await the bundled Vue runtime's queued reactive DOM work. */
export declare function vueUpdatesComplete(): Promise<void>;
/** Render a timeline snapshot to markup; use a passive Markdown callback for text. */
export declare function renderTurn(
  turn: unknown,
  markdownToHtml?: (text: string) => string,
): string;
/** Render child-run summaries to markup. */
export declare function renderSubagents(
  subagents: unknown,
  overflow?: boolean,
  markdownToHtml?: (text: string) => string,
): string;

export interface FormRenderer {
  render(id: string, value: unknown, element: HTMLElement): void;
  receive(message: unknown): Promise<void>;
  disposeOutput(id?: string): void;
  dispose(): void;
}

/** Native HTTP semantics with paths resolved within the host-authorized plugin root. */
export type PluginFetch = (path: string, init?: RequestInit) => Promise<Response>;

export interface PluginHttpOptions {
  /** Host transport; browser hosts must arrange CSP/CORS access or supply a stream adapter. */
  readonly fetch?: typeof globalThis.fetch;
}

/** Host-owned request scope. Mount credentials must be issued by the server for one plugin. */
export interface PluginHttpClient {
  readonly fetch: PluginFetch;
  /** Install into an app using this ESM's Vue runtime, before mounting. */
  install(app: { provide(key: symbol, value: PluginFetch): unknown }): void;
  /** Abort requests and active response bodies; terminal and idempotent. */
  dispose(): void;
}
export interface FormMessage {
  readonly source: "maieutics-form";
  readonly type: "mount" | "unmount" | "event";
  readonly modelId: string;
  readonly viewId: string;
  readonly name?: "submit" | "cancel";
  readonly payload?: { values: Record<string, unknown> };
}

/** Create the built-in Vue form renderer; the caller owns transport subscriptions. */
export function createFormRenderer(
  options: { sendMessage(message: FormMessage): void },
): FormRenderer;
