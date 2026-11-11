import { clientOnlyFactory } from "./vendor/vitepress/clientOnly.ts";
import type { RenderDiagnostic } from "./diagnostics.ts";
// @deno-types="npm:@vue/compiler-sfc@3.5.13/dist/compiler-sfc.d.ts"
import { compileScript, compileStyle, compileTemplate, parse as parseSfc } from "@vue/compiler-sfc";
import { type Node, parse } from "acorn";
import { createHash } from "node:crypto";
import { transform } from "sucrase";

/** Compiled Vue factories for the isolated SSR worker and live client island. */
export interface CompiledVueComponent {
  readonly code: string;
  /** Private SSR-target factory; never included in the frontend response. */
  readonly ssrCode?: string;
  readonly css: string;
  readonly scopeId: string;
  readonly setupKey?: string;
  readonly renderCode?: string;
}

/** Fixed diagnostics never include source text, values or compiler exception details. */
export class VueComponentError extends Error {
  constructor(
    readonly code:
      | "invalid_component"
      | "unsupported_import"
      | "component_failed"
      | "component_timeout",
    readonly diagnostics?: readonly RenderDiagnostic[],
  ) {
    super(code);
    this.name = "VueComponentError";
  }
}

function moduleBody(source: string, ssr = false): string {
  const ast = parse(source, { ecmaVersion: 2022, sourceType: "module" });
  const changes: { start: number; end: number; text: string }[] = [];
  for (const node of ast.body) {
    if (node.type === "ImportDeclaration") {
      if (node.source.value !== "vue") throw new VueComponentError("unsupported_import");
      const bindings = node.specifiers.map((item) => {
        if (item.type === "ImportNamespaceSpecifier") return `const ${item.local.name}=Vue;`;
        if (item.type !== "ImportSpecifier") throw new VueComponentError("unsupported_import");
        const name = item.imported.type === "Identifier" ? item.imported.name : item.imported.value;
        return `const ${item.local.name}=Vue[${JSON.stringify(name)}];`;
      }).join("\n");
      changes.push({ start: node.start, end: node.end, text: bindings });
    } else if (node.type === "ExportNamedDeclaration" && node.declaration) {
      changes.push({ start: node.start, end: node.declaration.start, text: "" });
    } else if (node.type.startsWith("Export")) {
      throw new VueComponentError("invalid_component");
    }
  }
  const visit = (value: unknown): void => {
    if (Array.isArray(value)) {
      for (const item of value) visit(item);
      return;
    }
    if (typeof value !== "object" || value === null) return;
    const node = value as Node;
    const member = node as Node & {
      computed?: boolean;
      object?: {
        type?: string;
        computed?: boolean;
        object?: { type?: string; meta?: { name?: string }; property?: { name?: string } };
        property?: { name?: string };
      };
      property?: { name?: string };
    };
    if (
      node.type === "MemberExpression" && !member.computed && member.property?.name === "SSR" &&
      member.object?.type === "MemberExpression" && !member.object.computed &&
      member.object.property?.name === "env" &&
      member.object.object?.type === "MetaProperty" &&
      member.object.object.meta?.name === "import" && member.object.object.property?.name === "meta"
    ) {
      changes.push({ start: node.start, end: node.end, text: String(ssr) });
      return;
    }
    if (node.type === "ImportExpression" || node.type === "MetaProperty") {
      throw new VueComponentError("unsupported_import");
    }
    for (const child of Object.values(value)) visit(child);
  };
  visit(ast);
  for (const change of changes.sort((a, b) => b.start - a.start)) {
    source = source.slice(0, change.start) + change.text + source.slice(change.end);
  }
  // Replacing a writable flag target must not produce a malformed runtime factory.
  parse(source, { ecmaVersion: 2022, sourceType: "module" });
  return source;
}

/** Uses Vue's SFC, template and scoped-style compilers; compilation never executes component code. */
export function compileVueComponent(
  documentId: string,
  source: string,
  frontmatter?: Record<string, unknown>,
  progressive = false,
): CompiledVueComponent {
  if (!/^[a-z][a-z\d_-]{0,63}$/i.test(documentId) || source.length > 200_000) {
    throw new VueComponentError("invalid_component");
  }
  const scopeId = `data-v-${documentId}`;
  try {
    const { descriptor, errors } = parseSfc(source, { filename: "output.vue" });
    if (
      errors.length || !descriptor.template || descriptor.template.src ||
      descriptor.customBlocks.length ||
      descriptor.script?.src || descriptor.scriptSetup?.src ||
      descriptor.scriptSetup?.attrs.generic ||
      descriptor.styles.some((style) =>
        style.src || style.module || (style.lang && style.lang !== "css")
      )
    ) throw new VueComponentError("invalid_component");
    if (progressive && (!descriptor.scriptSetup || descriptor.script)) {
      throw new VueComponentError("invalid_component");
    }
    const script = descriptor.script || descriptor.scriptSetup
      ? compileScript(progressive ? { ...descriptor, template: null } : descriptor, {
        id: scopeId,
        genDefaultAs: "__maieutics_component",
        inlineTemplate: false,
      })
      : undefined;
    const template = compileTemplate({
      source: descriptor.template.content,
      filename: "output.vue",
      id: scopeId,
      scoped: descriptor.styles.some((style) => style.scoped),
      compilerOptions: {
        bindingMetadata: script?.bindings,
        ...(progressive ? { hoistStatic: false, cacheHandlers: false } : {}),
      },
    });
    if (template.errors.length) throw new VueComponentError("invalid_component");
    const scriptJs = script
      ? transform(script.content, {
        transforms: ["typescript"],
        filePath: "output.ts",
        disableESTransforms: true,
        production: true,
      }).code
      : "const __maieutics_component = {};";
    const css = descriptor.styles.map((style) => {
      const compiled = compileStyle({
        source: style.content,
        filename: "output.vue",
        id: scopeId,
        scoped: style.scoped,
      });
      if (compiled.errors.length) throw new VueComponentError("invalid_component");
      return compiled.code;
    }).join("\n");
    const renderCode = `${moduleBody(template.code)}\nreturn render;`;
    if (progressive) {
      const rejectAsyncSetup = (value: unknown): void => {
        if (Array.isArray(value)) {
          for (const item of value) rejectAsyncSetup(item);
          return;
        }
        if (value === null || typeof value !== "object") return;
        const node = value as {
          type?: string;
          key?: { name?: string };
          value?: { async?: boolean };
        };
        if (node.type === "Property" && node.key?.name === "setup" && node.value?.async) {
          throw new VueComponentError("invalid_component");
        }
        for (const item of Object.values(value)) rejectAsyncSetup(item);
      };
      rejectAsyncSetup(parse(scriptJs, { ecmaVersion: 2022, sourceType: "module" }));
      const tree = parse(template.code, { ecmaVersion: 2022, sourceType: "module" });
      const check = (value: unknown): void => {
        if (Array.isArray(value)) {
          for (const item of value) check(item);
          return;
        }
        if (value === null || typeof value !== "object") return;
        const node = value as Node & {
          object?: { type: string; name?: string };
          property?: { type: string; name?: string };
          computed?: boolean;
        };
        if (
          node.type === "MemberExpression" && node.object?.type === "Identifier" &&
          node.object.name === "_ctx" &&
          (node.computed || node.property?.type !== "Identifier" ||
            !["$frontmatter", "$attrs", "$slots", "$props", "$emit"].includes(
              node.property?.name ?? "",
            ))
        ) {
          throw new VueComponentError("invalid_component");
        }
        for (const child of Object.values(value)) check(child);
      };
      check(tree);
    }
    const setupKey = progressive
      ? createHash("sha256").update(
        JSON.stringify([scriptJs, frontmatter, scopeId, clientOnlyFactory]),
      ).digest("hex")
      : undefined;
    const factory = (ssr: boolean) =>
      `"use strict";\nconst component = (()=>{${
        moduleBody(scriptJs, ssr)
      }\nreturn __maieutics_component;})();\ncomponent.render=(()=>{${
        moduleBody(template.code, ssr)
      }\nreturn render;})();\n${
        frontmatter === undefined
          ? ""
          : `const setup=component.setup;component.setup=(props,context)=>{Vue.getCurrentInstance().appContext.config.globalProperties.$frontmatter=Vue.readonly(${
            JSON.stringify(frontmatter).replaceAll("<", "\\u003c")
          });return setup?setup(props,context):{}};`
      }\ncomponent.components={ClientOnly:${clientOnlyFactory},...component.components};\ncomponent.__scopeId=${
        JSON.stringify(scopeId)
      };\nreturn component;`;
    const code = factory(false);
    const ssrCode = factory(true);
    if (code.length > 400_000 || ssrCode.length > 400_000 || css.length > 48_000) {
      throw new VueComponentError("invalid_component");
    }
    return {
      code,
      ...(ssrCode !== code ? { ssrCode } : {}),
      css,
      scopeId,
      ...(progressive ? { setupKey, renderCode } : {}),
    };
  } catch (error) {
    if (error instanceof VueComponentError) throw error;
    throw new VueComponentError("invalid_component");
  }
}
