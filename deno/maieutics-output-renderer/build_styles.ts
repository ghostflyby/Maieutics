// @deno-types="npm:@vue/compiler-sfc@3.5.13/dist/compiler-sfc.d.ts"
import { compileStyle } from "@vue/compiler-sfc";

const scope = ":where(.maieutics-output)";
const dark = [
  ":host-context(.vscode-dark)",
  ":host-context(.vscode-high-contrast)",
  ":host-context(.dark)",
  ".vscode-dark",
  ".vscode-high-contrast",
  ".dark",
].map((context) => `${context} ${scope}`).concat(`${scope}.dark`);

const hostVariables: Record<string, string> = {
  "--vp-c-bg": "--vscode-editor-background",
  "--vp-c-bg-alt": "--vscode-editor-background",
  "--vp-c-bg-soft": "--vscode-textCodeBlock-background",
  "--vp-c-text-1": "--vscode-editor-foreground",
  "--vp-c-text-2": "--vscode-descriptionForeground",
  "--vp-c-text-3": "--vscode-descriptionForeground",
  "--vp-c-divider": "--vscode-panel-border",
  "--vp-c-border": "--vscode-panel-border",
  "--vp-c-brand-1": "--vscode-textLink-foreground",
  "--vp-c-brand-2": "--vscode-textLink-activeForeground",
  "--vp-font-family-base": "--vscode-font-family",
  "--vp-font-family-mono": "--vscode-editor-font-family",
  "--vp-code-block-bg": "--vscode-textCodeBlock-background",
  "--vp-code-line-highlight-color": "--vscode-editor-lineHighlightBackground",
  "--vp-code-line-diff-add-color": "--vscode-diffEditor-insertedTextBackground",
  "--vp-code-line-diff-remove-color": "--vscode-diffEditor-removedTextBackground",
};

/** Parse trusted upstream CSS through the already pinned Vue/PostCSS toolchain. */
export function parseStyles(source: string) {
  const result = compileStyle({
    source,
    filename: "output.css",
    id: "output-styles",
    scoped: false,
  });
  const root = result.rawResult?.root;
  if (result.errors.length || !root) throw new Error("Invalid output stylesheet.");
  return root;
}

/** Deterministic, output-scoped extraction of upstream default-theme content styles. */
export function buildStyles(
  base: string,
  vars: string,
  doc: string,
  blocks: string,
  host: string,
): string {
  const reset = parseStyles(base);
  reset.walkRules((rule) => {
    // Leave host layout and native interactive controls to their existing owners.
    const selectors = rule.selectors.filter((selector) =>
      !/(?:^|[\s,])(?:html|body|main|button|input|textarea|select|optgroup|fieldset|label|area|summary|vite-error-overlay)\b|\[role=/
        .test(selector)
    );
    if (!selectors.length) rule.remove();
    else rule.selectors = selectors;
  });
  const content = parseStyles(doc + "\n" + blocks);
  content.walkRules((rule) => {
    const selectors = rule.selectors.filter((selector) =>
      !/\.VPTeam|\.tabs\b|button\.copy|span\.lang|\.line-numbers/.test(selector)
    );
    if (!selectors.length) rule.remove();
    else rule.selectors = selectors;
  });
  const variables = parseStyles(vars);
  const used = new Set<string>();
  const collect = (text: string) => {
    for (const match of text.matchAll(/var\((--vp-[\w-]+)/g)) used.add(match[1]);
  };
  collect(reset.toString() + content.toString() + host);
  let previous = -1;
  while (previous !== used.size) {
    previous = used.size;
    variables.walkDecls((decl) => {
      if (used.has(decl.prop)) collect(decl.value);
    });
  }
  variables.walkDecls((decl) => {
    if (!used.has(decl.prop)) decl.remove();
    else if (hostVariables[decl.prop]) {
      decl.value = `var(${hostVariables[decl.prop]}, ${decl.value})`;
    }
  });
  variables.walkRules((rule) => {
    if (!rule.nodes.length) rule.remove();
  });
  const result = parseStyles(variables.toString() + reset.toString() + content.toString());
  result.walkRules((rule) => {
    rule.selectors = rule.selectors.map((selector) =>
      selector.replace(/(\.diff(?:\.[\w-]+)*)::before/g, "$1::after")
    ).flatMap((selector) =>
      selector === ".dark" ? dark : [
        selector.startsWith(":root")
          ? selector.replace(":root", scope)
          : selector.startsWith(".vp-doc")
          ? scope + selector
          : `${scope} ${selector}`,
      ]
    );
  });
  result.append(parseStyles(host).nodes);
  result.walkComments((comment) => {
    comment.remove();
  });
  result.walkRules((rule) => {
    if (!rule.nodes.length) rule.remove();
  });
  result.walkAtRules((rule) => {
    if (rule.nodes?.length === 0) rule.remove();
  });
  result.walk((node) => {
    node.raws.before = "";
    if (node.type === "decl") node.raws.between = ":";
    if (node.type === "rule" || node.type === "atrule") {
      node.raws.between = "";
      node.raws.after = "";
    }
  });
  result.raws.after = "";
  const css = result.toString() + "\n";
  // Baseline and authored component styles each have a 48k budget.
  if (css.length > 48000) {
    throw new Error(`Baseline CSS exceeds its compatibility budget: ${css.length}`);
  }
  if (
    /@import|@font-face/i.test(css) ||
    [...css.matchAll(/url\(([^)]*)\)/gi)].some((match) =>
      !match[1].trim().replace(/^["']|["']$/g, "").startsWith("data:image/svg+xml,")
    )
  ) throw new Error("Output styles must remain self-contained.");
  return css;
}

/** Loads only pinned source and the product adapter; no site configuration or runtime I/O. */
export async function readStyles(): Promise<string> {
  const root = new URL("./vendor/vitepress/styles/", import.meta.url);
  const [base, vars, doc, blocks, host, license] = await Promise.all([
    ...["base.css", "vars.css", "vp-doc.css", "custom-block.css"].map((name) =>
      Deno.readTextFile(new URL(name, root))
    ),
    Deno.readTextFile(new URL("./styles.host.css", import.meta.url)),
    Deno.readTextFile(new URL("./vendor/vitepress/LICENSE", import.meta.url)),
  ]);
  const css = buildStyles(base, vars, doc, blocks, host);
  const output =
    `/*! Generated by build_styles.ts from VitePress v1.6.4 default-theme CSS (MIT).\n${license}*/\n${css}`;
  if (output.length > 48000) {
    throw new Error(`Licensed CSS exceeds its compatibility budget: ${output.length}`);
  }
  return output;
}

if (import.meta.main) {
  await Deno.writeTextFile(new URL("./styles.css", import.meta.url), await readStyles());
}
