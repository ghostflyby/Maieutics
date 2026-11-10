import { VueComponentError } from "./vueComponent.ts";
import { captureTemplate, TemplateInputError } from "./template.ts";
import { timingSafeEqual } from "node:crypto";
import { type OutputRenderer, OutputRenderingError } from "./mod.ts";

export const MaximumRequestBytes = 2 * 1024 * 1024;

/** Authenticated, request/reply rendering API for the supervised internal child. */
export function createRenderHandler(renderer: OutputRenderer, credential: string) {
  const expected = new TextEncoder().encode(`Bearer ${credential}`);
  return async (request: Request): Promise<Response> => {
    const supplied = new TextEncoder().encode(request.headers.get("authorization") ?? "");
    if (supplied.length !== expected.length || !timingSafeEqual(expected, supplied)) {
      return Response.json({ code: "unauthorized" }, { status: 401 });
    }
    const path = new URL(request.url).pathname;
    if (
      !["/v1/markdown", "/v1/template", "/v1/vue", "/v1/assistant"].includes(path) ||
      request.method !== "POST"
    ) {
      return Response.json({ code: "not_found" }, { status: 404 });
    }
    try {
      const reader = request.body?.getReader();
      if (reader === undefined) return Response.json({ code: "invalid_request" }, { status: 400 });
      const chunks: Uint8Array[] = [];
      let length = 0;
      try {
        while (true) {
          request.signal.throwIfAborted();
          const { done, value } = await reader.read();
          if (done) break;
          length += value.length;
          if (length > MaximumRequestBytes) {
            await reader.cancel();
            return Response.json({ code: "request_too_large" }, { status: 413 });
          }
          chunks.push(value);
        }
      } finally {
        reader.releaseLock();
      }
      const bytes = new Uint8Array(length);
      let offset = 0;
      for (const chunk of chunks) {
        bytes.set(chunk, offset);
        offset += chunk.length;
      }
      const value: unknown = JSON.parse(new TextDecoder().decode(bytes));
      if (typeof value !== "object" || value === null) throw new SyntaxError();
      const body = value as Record<string, unknown>;
      if (
        body.version !== 1 || typeof body.documentId !== "string" || typeof body.source !== "string"
      ) {
        return Response.json({ code: "invalid_request" }, { status: 400 });
      }
      if (
        (body.progressive !== undefined && typeof body.progressive !== "boolean") ||
        (body.compileOnly !== undefined && typeof body.compileOnly !== "boolean") ||
        ((body.progressive || body.compileOnly) && path !== "/v1/assistant") ||
        (body.compileOnly && !body.progressive)
      ) return Response.json({ code: "invalid_request" }, { status: 400 });
      const vue = body.vue as { version?: number; format?: string } | undefined;
      if (
        path === "/v1/vue" &&
        (typeof vue !== "object" || vue === null || vue.version !== 1 ||
          !["markdown", "sfc"].includes(vue.format ?? ""))
      ) return Response.json({ code: "invalid_request" }, { status: 400 });
      const output = path === "/v1/assistant"
        ? await renderer.renderAssistant(
          body.documentId,
          body.source,
          request.signal,
          body.progressive ? { compileOnly: body.compileOnly === true } : undefined,
        )
        : path === "/v1/vue"
        ? await renderer.renderVue(
          body.documentId,
          body.source,
          vue?.format as "markdown" | "sfc",
          request.signal,
        )
        : path === "/v1/template"
        ? await renderer.renderTemplate(
          body.documentId,
          body.source,
          captureTemplate(body.template),
          request.signal,
        )
        : await renderer.render(body.documentId, body.source, request.signal);
      return Response.json({
        version: 1,
        documentId: output.documentId,
        html: output.html,
        renderedSource: output.renderedSource,
        css: output.vue?.css,
        vue: output.vue
          ? {
            version: 1,
            code: output.vue.code,
            scopeId: output.vue.scopeId,
            setupKey: output.vue.setupKey,
            renderCode: output.vue.renderCode,
          }
          : undefined,
        expressions: output.expressions?.map(({ state, dependencies, code }) => ({
          state,
          dependencies,
          code,
        })),
      });
    } catch (error) {
      if (
        error instanceof SyntaxError ||
        error instanceof TemplateInputError
      ) {
        return Response.json({ code: "invalid_request" }, { status: 400 });
      }
      if (error instanceof VueComponentError) {
        return Response.json({ code: error.code }, {
          status: error.code.startsWith("component_") ? 503 : 400,
        });
      }
      if (error instanceof OutputRenderingError) {
        return Response.json({ code: error.code }, { status: 400 });
      }
      // The request and document text can contain secrets. Only a fixed code crosses this boundary.
      return Response.json({ code: "render_failed" }, { status: 503 });
    }
  };
}
