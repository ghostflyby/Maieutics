import MarkdownIt from "markdown-it";
import { containerPlugin } from "./upstream/src/node/markdown/plugins/containers.ts";
import { preWrapperPlugin } from "./upstream/src/node/markdown/plugins/preWrapper.ts";
import { highlightLinePlugin } from "./upstream/src/node/markdown/plugins/highlightLines.ts";
import { lineNumberPlugin } from "./upstream/src/node/markdown/plugins/lineNumbers.ts";
import { gitHubAlertsPlugin } from "./upstream/src/node/markdown/plugins/githubAlerts.ts";

const results: Record<string, boolean> = {};
function check(name: string, value: boolean) {
  results[name] = value;
  if (!value) throw new Error(`Upstream extraction probe: ${name}`);
}
const md = new MarkdownIt({ html: false });
md.use(highlightLinePlugin)
  .use(preWrapperPlugin, { codeCopyButtonTitle: "Copy", hasSingleTheme: true })
  .use(containerPlugin, { hasSingleTheme: true })
  .use(lineNumberPlugin);
const source = "::: code-group\n```ts:line-numbers=7 {1} [report.ts]\nconst total = 42;\n```\n\n```json [report.json]\n{}\n```\n:::";
const output = md.render(source, {});
check("unmodified_plugins_render_code_group", output.includes('class="vp-code-group"'));
check("unmodified_plugins_render_titles", output.includes("report.ts") && output.includes("report.json"));
check("unmodified_plugins_render_line_numbers", output.includes("line-numbers-wrapper") && output.includes(">7</span>"));
check("raw_group_ids_are_nondeterministic", md.render(source, {}) !== output);

const alerts = new MarkdownIt({ html: false }).use(gitHubAlertsPlugin);
check("unmodified_alerts_render", alerts.render("> [!TIP]\n> Body").includes("github-alert"));
const attack = '> [!TIP] <img src=x onerror="probe()">\n> Body';
check("raw_alert_title_bypasses_html_false", alerts.render(attack).includes("<img src=x onerror="));
check("raw_group_title_bypasses_html_false", md.render('::: code-group\n```ts [<img src=x onerror="probe()">]\nx\n```\n:::').includes("<img src=x onerror="));
check("raw_pre_wrapper_language_needs_escaping", md.render('```ts=a"onmouseover="probe()\nx\n```').includes('onmouseover="probe()'));

alerts.renderer.rules.github_alert_open = (tokens, index) => {
  const { title, type } = tokens[index].meta;
  return `<div class="${type} custom-block github-alert"><p class="custom-block-title">${alerts.utils.escapeHtml(title)}</p>`;
};
const hardened = alerts.render(attack);
check("renderer_only_alert_patch_closes_title_injection", !hardened.includes("<img") && hardened.includes("&lt;img"));
console.log(JSON.stringify({ status: "ok", sourceTag: "v1.6.4", assertions: results }));
