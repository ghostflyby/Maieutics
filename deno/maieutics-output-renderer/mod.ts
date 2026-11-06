/** Executable-owned content rendering primitives; no frontend transport or Agent dependency. */
export { createOutputRenderer, OutputRenderingError } from "./renderer.ts";
export type { OutputRenderer, RenderedHeading, RenderedOutput } from "./renderer.ts";

export type { TemplateBinding, TemplateContext, TemplateResult } from "./template.ts";

export { TemplateInputError } from "./template.ts";
