import MarkdownIt from "markdown-it";
import anchor from "markdown-it-anchor";
import attrs from "markdown-it-attrs";
import { full as emoji } from "markdown-it-emoji";
import { tocPlugin } from "@mdit-vue/plugin-toc";
import { frontmatterPlugin } from "@mdit-vue/plugin-frontmatter";
import { titlePlugin } from "@mdit-vue/plugin-title";
import { componentPlugin } from "@mdit-vue/plugin-component";
import { sfcPlugin } from "@mdit-vue/plugin-sfc";
import matter from "gray-matter";

const checks: Record<string, boolean> = {};
function check(name: string, value: boolean) {
  checks[name] = value;
  if (!value) throw new Error(name);
}
const md = new MarkdownIt({ html: false }).use(attrs, { allowedAttributes: ["id"] }).use(anchor).use(frontmatterPlugin).use(titlePlugin).use(tocPlugin).use(emoji);
const env: Record<string, unknown> = {};
const html = md.render('---\ntitle: "Metadata"\n---\n\n# Heading\n\n[[toc]]\n\n## Section {#custom}\n\n:tada:', env);
check("public_frontmatter_package", (env.frontmatter as { title?: string })?.title === "Metadata");
check("public_title_package", env.title === "Heading");
check("public_attrs_and_anchor_packages", html.includes('id="custom"'));
check("public_toc_uses_custom_anchor", html.includes('href="#custom"'));
check("public_emoji_package", html.includes("🎉"));
const attributesSource = "## Heading {onclick=probe()}";
const unrestricted = new MarkdownIt({ html: false }).use(attrs);
check(
  "default_attrs_allow_event_attributes",
  unrestricted.render(attributesSource).includes(' onclick="probe()"'),
);
check(
  "explicit_attribute_allowlist_blocks_event_attributes",
  !md.render(attributesSource, {}).includes(' onclick="probe()"'),
);
const cacheBefore = Object.keys(matter.cache).length;
md.render("First plain streaming source", {});
md.render("Second plain streaming source", {});
check("default_frontmatter_uses_process_global_source_cache", Object.keys(matter.cache).length === cacheBefore + 2);
md.render("---js\n{ value: (globalThis.__frontmatterProbeExecuted = true, 42) }\n---\nBody", {});
check("default_javascript_frontmatter_executes", (globalThis as Record<string, unknown>).__frontmatterProbeExecuted === true);
delete (globalThis as Record<string, unknown>).__frontmatterProbeExecuted;
const restricted = new MarkdownIt({ html: false }).use(frontmatterPlugin, {
  grayMatterOptions: {
    engines: {
      javascript: () => {
        throw new Error("Executable frontmatter disabled");
      },
    },
  },
  renderExcerpt: false,
});
let rejected = false;
try {
  restricted.render("---js\n{ value: (globalThis.__frontmatterProbeExecuted = true, 42) }\n---\nBody", {});
} catch {
  rejected = true;
}
check("explicit_engine_override_blocks_javascript", rejected && !(globalThis as Record<string, unknown>).__frontmatterProbeExecuted);
const cacheAfter = Object.keys(matter.cache).length;
restricted.render("Third plain streaming source", {});
restricted.render("Fourth plain streaming source", {});
check("explicit_options_disable_process_global_cache", Object.keys(matter.cache).length === cacheAfter);
const vueMd = new MarkdownIt({ html: true }).use(componentPlugin).use(sfcPlugin);
const vueEnv: Record<string, unknown> = {};
const vueHtml = vueMd.render("<script setup>globalThis.__upstreamProbeExecuted = true</script>\n\n{{ later.total }}", vueEnv);
check("sfc_package_extracts_source_blocks", !!vueEnv.sfcBlocks);
check("sfc_extraction_does_not_execute_source", !(globalThis as Record<string, unknown>).__upstreamProbeExecuted);
check("sfc_extraction_does_not_resolve_late_bindings", vueHtml.includes("later.total"));
console.log(JSON.stringify({ status: "ok", assertions: checks }));
