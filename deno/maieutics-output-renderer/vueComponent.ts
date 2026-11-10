// @deno-types="npm:@vue/compiler-sfc@3.5.13/dist/compiler-sfc.d.ts"
import { compileScript, compileStyle, compileTemplate, parse as parseSfc } from "@vue/compiler-sfc";
import { type Node, parse } from "acorn";
import { transform } from "sucrase";

/** Compiled component code is used unchanged by isolated SSR and the live client island. */
export interface CompiledVueComponent {
  readonly code: string;
  readonly css: string;
  readonly scopeId: string;
}

/** Fixed diagnostics never include source text, values or compiler exception details. */
export class VueComponentError extends Error {
  constructor(
    readonly code:
      | "invalid_component"
      | "unsupported_import"
      | "component_failed"
      | "component_timeout",
  ) {
    super(code);
    this.name = "VueComponentError";
  }
}

function moduleBody(source: string): string {
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
    if (node.type === "ImportExpression" || node.type === "MetaProperty") {
      throw new VueComponentError("unsupported_import");
    }
    for (const child of Object.values(value)) visit(child);
  };
  visit(ast);
  for (const change of changes.sort((a, b) => b.start - a.start)) {
    source = source.slice(0, change.start) + change.text + source.slice(change.end);
  }
  return source;
}

/** Uses Vue's SFC, template and scoped-style compilers; compilation never executes component code. */
export function compileVueComponent(
  documentId: string,
  source: string,
  frontmatter?: Record<string, unknown>,
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
    const script = descriptor.script || descriptor.scriptSetup
      ? compileScript(descriptor, {
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
      compilerOptions: { bindingMetadata: script?.bindings },
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
    const code = `"use strict";\nconst component = (()=>{${
      moduleBody(scriptJs)
    }\nreturn __maieutics_component;})();\ncomponent.render=(()=>{${
      moduleBody(template.code)
    }\nreturn render;})();\n${
      frontmatter === undefined
        ? ""
        : `const setup=component.setup;component.setup=(props,context)=>{Vue.getCurrentInstance().appContext.config.globalProperties.$frontmatter=Vue.readonly(${
          JSON.stringify(frontmatter).replaceAll("<", "\\u003c")
        });return setup?setup(props,context):{}};`
    }\ncomponent.__scopeId=${JSON.stringify(scopeId)};\nreturn component;`;
    if (code.length > 400_000 || css.length > 48_000) {
      throw new VueComponentError("invalid_component");
    }
    return { code, css, scopeId };
  } catch (error) {
    if (error instanceof VueComponentError) throw error;
    throw new VueComponentError("invalid_component");
  }
}
