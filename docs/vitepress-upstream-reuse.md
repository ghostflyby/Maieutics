# Reusing VitePress upstream components

Status: Source audit and offline extraction probes completed; product migration remains pending.

Date: 2026-11-04

Related: [Streaming output rendering design](streaming-output-rendering-design.md).

## Recommended reuse boundary

Use public ecosystem packages for Markdown features that already have independent releases. Extract
the small VitePress content plugins where they carry additional semantics, preserving upstream
provenance and a narrow adaptation patch. Keep resource authorization, document identities,
streaming revisions, late values, and frontend lifetime in Maieutics.

The audit uses VitePress **v1.6.4**, commit `1fc537b78cda287fa23c1129a815ad9455fd8106`, matching the
current compatibility target. It does not upgrade production dependencies or claim compatibility
with VitePress 2 prereleases. Source links below refer to that tag unless a package is named
separately.

## Public entry points and packaging

The npm package exports the node entry and a `./dist/*` wildcard, but its published node plugins are
combined into generated chunks. It does not ship an independent `containers` or `lineNumbers` source
entry. Deep-importing a generated chunk would depend on build filenames and mixed site dependencies.
The [Rollup configuration](https://github.com/vuejs/vitepress/blob/v1.6.4/rollup.config.ts) explains
this packaging boundary; source extraction can bypass it.

The public
[Markdown factory](https://github.com/vuejs/vitepress/blob/v1.6.4/src/node/markdown/markdown.ts) is
experimental and owns module-level parser/highlighter state. Its composition also installs snippet,
component, SFC, and site-link behavior. The earlier complete-entry bundle probe reached Vite's
optional `lightningcss` dependency. This is evidence against that particular import path, not
against the portability of individual plugins. The `cache` option controls compiled-page caching in
`markdownToVue.ts`; it does not make the Markdown factory independently owned per call.

## Direct package reuse

| Feature                                   | Upstream package                                                           | Integration condition                                                                                                                                                |
| ----------------------------------------- | -------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| TOC, heading metadata, title              | `@mdit-vue/plugin-toc`, `plugin-headers`, `plugin-title`, shared utilities | Keep anchors, TOC links, and heading metadata under the same document scope. TOC reads heading IDs assigned by the anchor plugin.                                    |
| Custom heading IDs                        | `markdown-it-attrs` plus `markdown-it-anchor`                              | Configure an explicit attribute allowlist and scope accepted IDs. Default attrs behavior accepts event attributes even with raw HTML disabled.                       |
| Frontmatter                               | `@mdit-vue/plugin-frontmatter`                                             | Supply explicit, uncached parser options; reject executable languages; bound metadata and alias expansion. Default options are unsuitable for model-authored source. |
| Emoji                                     | `markdown-it-emoji`                                                        | Select the required definition set and account for bundle size.                                                                                                      |
| Code annotations                          | `@shikijs/transformers`                                                    | Already reused in production. Keep static grammars, bounded metadata parsing, and explicit highlighter ownership.                                                    |
| SFC block extraction and component syntax | `@mdit-vue/plugin-sfc`, `plugin-component`                                 | Reusable parsing machinery for a future authorized template path. They do not compile TS, execute producers, resolve late identifiers, or maintain a document.       |
| Math                                      | `markdown-it-mathjax3`                                                     | Independent optional package used by VitePress; offline assets and notebook CSP still need a separate prototype.                                                     |

The audited standalone package probes use `@mdit-vue` 2.1.3, attrs 4.3.1, anchor 9.2.0, and emoji
3.0.0 with Markdown-it 14.3.1. These are probe dependencies, not additions to the product workspace.

## VitePress source extraction candidates

Paths in this table are relative to the upstream repository.

| Source                                                                                                                | Reusable part                                                            | Required adaptation                                                                                                                                                                                                  |
| --------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/node/markdown/plugins/containers.ts`                                                                             | Container registration, inline titles, code-group recognition and markup | Escape group labels; use deterministic document-scoped IDs; retain the passive-content rule for HTML blocks and mixed groups. Ordinary container titles use `renderInline`, unlike the current literal-title subset. |
| `src/node/markdown/plugins/githubAlerts.ts`                                                                           | Alert marker recognition and token conversion                            | Replace its title rendering with escaped text or the restricted inline renderer. The raw plugin interpolates the title into HTML.                                                                                    |
| `src/node/markdown/plugins/preWrapper.ts`                                                                             | Fence language/title conventions, code wrapper and copy-button markup    | Escape interpolated language/title values; inject only trusted button labels; align the wrapper with code-group styling and frontend handlers.                                                                       |
| `src/node/markdown/plugins/highlightLines.ts`                                                                         | Normalizing line-highlight syntax after attrs parsing                    | Keep plugin order explicit and enforce bounded range handling in the highlighter adapter. This plugin alone does not highlight lines.                                                                                |
| `src/node/markdown/plugins/lineNumbers.ts`                                                                            | Numbering the rendered code lines                                        | Depends on the pre-wrapper HTML shape. Adopt with that shape or retain a deliberate rendering adapter.                                                                                                               |
| `src/node/markdown/plugins/highlight.ts`                                                                              | Transformer selection and conventions                                    | Keep public Shiki transformers. Do not copy the whole loader: it uses `synckit`, `require.resolve`, a worker, and dynamic grammar loading. Replace its range expansion with the current bounded interval handling.   |
| `src/client/app/composables/codeGroups.ts`                                                                            | Switching active code blocks and scrolling labels                        | Remove site hooks and global event ownership; bind to the owning output/shadow root, return cleanup, and restore selection using local presentation state.                                                           |
| `src/client/app/composables/copyCode.ts`                                                                              | Excluding removed diff lines and ignored nodes; shell prompt stripping   | Use an explicit clipboard adapter and handle its failures. Own listeners and feedback timers; resolve targets inside the output's shadow root.                                                                       |
| `src/client/theme-default/styles/components/{vp-code,vp-code-group,custom-block}.css` and relevant `vp-doc.css` rules | Content appearance and class conventions                                 | Scope selected rules and map `--vp-*` variables to host themes. Validate selectors inside the actual Markdown shadow boundary; do not import the whole theme, resets, or fonts.                                      |

The upstream code-group HTML, `.active` block switching, and CSS form one contract. Its radio labels
change selection styling, but block visibility relies on the client handler. Copying only the
container plugin or only its stylesheet does not provide working tabs. The handler currently uses
`window` and `event.target`; adapting it for an owned Shadow DOM boundary is necessary. This follows
from source inspection, not a completed browser migration.

If adopting that contract, migrate the renderer, scoped handler, styles, and tests together.
Replacing the current native-radio markup with a partial upstream structure would lose already
verified switching and selection retention. The intended outcome is shared upstream syntax and
behavior with small host adapters, rather than maintaining two overlapping implementations
indefinitely.

## Behavior requiring policy adapters

### Titles and attributes

The offline probes demonstrate that raw code-group labels and alert titles can introduce an HTML
element despite `html: false`. Pre-wrapper language text can also introduce an attribute. These
plugins write their own output strings, outside Markdown-it's ordinary raw-HTML switch. An alert
renderer-only escape patch closes the demonstrated title injection while retaining upstream parsing.

The attrs probe likewise confirms that the default plugin accepts `onclick`; an explicit `id`
allowlist blocks that case. Attribute configuration and renderer escaping belong in the adapter and
its regression tests. These are differences between trusted site authoring and Maieutics model
output, not claims that VitePress promises an untrusted-document sandbox.

### Frontmatter parsing and caching

`@mdit-vue/plugin-frontmatter` 2.1.3 calls `gray-matter` 4.0.3. The latter's
[JavaScript engine](https://github.com/jonschlinkert/gray-matter/blob/master/lib/engines.js)
evaluates JavaScript frontmatter. Its
[default cache](https://github.com/jonschlinkert/gray-matter/blob/master/index.js) retains
source-keyed results at module scope when no options object is supplied. Both behaviors were
reproduced against the locked package artifacts, including plain streaming source without
frontmatter.

Passing an explicit options object disables that cache in this version. Overriding the JavaScript
engine with a rejecting parser blocks the demonstrated `js` header, including its engine alias. A
production adapter must additionally enforce the accepted language set, data shape, nesting/size
limits, and YAML alias policy; setting `language: 'yaml'` alone does not prevent a source header
from selecting another engine. Metadata must not change process permissions or become document code.

### Resource and compilation boundaries

Do not install
[snippet file reads](https://github.com/vuejs/vitepress/blob/v1.6.4/src/node/markdown/plugins/snippet.ts)
or
[Markdown inclusion](https://github.com/vuejs/vitepress/blob/v1.6.4/src/node/utils/processIncludes.ts)
unchanged. Pure path/region parsing can be extracted, but resource acquisition must use the kernel's
authorized resource plane, with explicit resolution, limits, and producer ownership.

Likewise, the site `link.ts` rewrites page paths to `.html` and tracks dead links; that is not the
product resource URL contract. `image.ts` can contribute lazy-loading behavior, but its URL
adaptation must remain product-owned. `restoreEntities.ts` serves Vue template compilation and
should not be installed in the passive HTML path without proving the resulting escaping contract.

The
[Markdown-to-Vue wrapper](https://github.com/vuejs/vitepress/blob/v1.6.4/src/node/markdownToVue.ts)
combines files, routes, Git timestamps, page data, scripts, and styles. It is not a self-contained
streaming compiler. Use Vue's public compilers and the `@mdit-vue` parsing packages for a separately
authorized template path; Maieutics must still provide binding state, producer execution,
cancellation, revision checks, and recovery.

## Offline probe evidence

Two [reproducible probe entries](research/vitepress-upstream/) run with network, filesystem,
subprocess, and environment access denied after bundling. All **23 checks** pass under Deno 2.9.8.

| Probe          | Result                                      | Scope                                                                                                                                                                                                    |
| -------------- | ------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `plugins.ts`   | 9 checks; 85-module bundle, about 153 KiB   | Unmodified upstream containers, pre-wrapper, line normalization, line numbers and alerts render; identifies random IDs and the title/language escaping gaps; demonstrates a small alert-renderer patch.  |
| `ecosystem.ts` | 14 checks; 141-module bundle, about 275 KiB | Public TOC/custom-anchor/title/emoji/frontmatter integration; attributes and frontmatter policy differences; explicit cache/engine overrides; SFC extraction without execution or late-value resolution. |

Sizes include Markdown-it, probe code, and selected packages. They are not product size deltas or a
performance benchmark. These are isolated bundling/runtime probes; a production extraction still
requires TypeScript integration, representative upstream fixtures, current product regressions,
actual Notebook CSP/theme coverage, and published executable tests.

On a fresh repository checkout, run from `docs/research/vitepress-upstream`:

```sh
git clone --depth 1 --single-branch --branch v1.6.4 --filter=blob:none \
  https://github.com/vuejs/vitepress.git upstream
git -C upstream rev-parse HEAD
deno bundle --minify --config deno.json -o dist/plugins.js plugins.ts
deno bundle --minify --config deno.json -o dist/ecosystem.js ecosystem.ts
deno run --no-config --no-npm --no-remote --cached-only \
  --deny-net --deny-run --deny-read --deny-write --deny-env dist/plugins.js
deno run --no-config --no-npm --no-remote --cached-only \
  --deny-net --deny-run --deny-read --deny-write --deny-env dist/ecosystem.js
```

The checked-out source and generated bundles are ignored. Probe inputs only set a temporary boolean
to detect JavaScript-frontmatter execution; no model-generated code is evaluated. These probes are
outside the production Deno workspace and leave its dependency lock unchanged.

## Extraction maintenance and rollout

1. Add TOC, custom anchors, and title through the public packages first. Keep identity scoping and
   attribute policy in one adapter, with later headings and link definitions in the fixtures.
2. Extract alerts and common container behavior from the pinned upstream source, with escaped
   rendering and localized labels. Reuse the relevant upstream Markdown cases.
3. Migrate the code-block family as a coherent change if adopting upstream markup: parsing,
   pre-wrapper, line numbers, code groups, scoped switch/copy handlers, selected styles, and
   existing snapshot-selection tests. Keep public Shiki core/transformers and bounded range
   handling.
4. Add frontmatter only through the explicit data-only, uncached policy. Evaluate additional
   languages, math, resources, and authorized SFC compilation as separate capability increments.

For extracted source, retain the upstream
[MIT license](https://github.com/vuejs/vitepress/blob/v1.6.4/LICENSE) and copyright notice in source
and distributed artifacts, record tag/commit and file hashes in a vendor manifest, and maintain a
short patch list explaining escaping, identity, bounds, ownership, and host changes. Keep extracted
files recognizable; apply product behavior through wrappers where feasible. Review upstream diffs
against both its fixtures and Maieutics regressions before updating the manifest. No production
vendor tree has been introduced by this research change.
