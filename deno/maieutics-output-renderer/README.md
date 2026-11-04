# Output renderer

Executable-owned rendering primitives for the
[streaming output design](../../docs/streaming-output-rendering-design.md). The first implementation
provides complete passive Markdown snapshots through Markdown-it, the `@mdit-vue` heading plugin,
Shiki, and Vue SSR. The executable embeds `bundle/service.js` and starts the rendering child on the
first authenticated `POST /v1/render/markdown` request. No user REPL or plugin host is needed.

```ts
import { createOutputRenderer } from "./mod.ts";

using renderer = await createOutputRenderer();
const output = await renderer.render("message1", "# Result\n\n**Ready**", signal);
// output.html is derived passive HTML; output.headings describes scoped heading anchors.
```

The caller owns the renderer and its highlighter. Creation is asynchronous; disposal is idempotent.
Each render creates fresh Markdown environment state, so headings and reference definitions do not
leak between calls or documents. Render results are deterministic for identical source and document
identity. Source is capped at 200,000 UTF-16 code units and generated HTML at 4,000,000 code units.
Cancellation or disposal during SSR prevents the result from being returned. Parsing and
highlighting are synchronous within one bounded request; prompt cancellation of CPU work will
require the supervised service boundary.

## Content subset

Supported content includes headings, document-local anchor links, reference-style links, tables,
emphasis, code blocks, and `tip`, `info`, `warning`, `danger`, and `details` containers. TypeScript,
JavaScript, and JSON grammars and both GitHub light and dark themes are bundled statically. Other
code languages use escaped plain text without dynamically loading packages or starting workers.
`styles.css` provides host-scoped baseline styles using VS Code variables with browser fallbacks.

HTML, scripts, styles, Vue expressions, and directives in Markdown remain text. Executable link
schemes such as `command:` and `javascript:` are inactive. Filesystem snippet imports and include
directives are never installed. Product resource schemes are retained for a future frontend adapter;
resource retrieval and rewriting are not implemented here.

This is a selected VitePress-compatible content subset, not complete VitePress page semantics. Code
groups, frontmatter, SFC scripts, template execution, late bindings, HTML patches, interactive
islands, and host resource delivery remain later work. Re-rendering with a later link definition
corrects earlier references in a complete snapshot; stable incremental nodes require the document
runtime.

## Why compose the underlying packages

The prototype verified `vitepress@1.6.4` rendering in Deno 2.9.8, but importing its public entry
also loads site-build dependencies. A direct `deno bundle` encountered Vite's optional
`lightningcss` dependency. Its Markdown renderer also caches one parser and highlighter globally.
The standalone implementation instead composes the underlying ecosystem with independent ownership
and static grammar imports. It does not suppress bundler diagnostics or add unused native build
dependencies.

## Verification

Run from this directory after installing the workspace's locked dependencies:

```sh
deno task check
deno task test
deno task build
deno task smoke:offline
```

The build produces an ignored `dist/smoke.js` bundle containing the rendering engine, bundled
grammars, and a representative fixture. The smoke command disables npm and remote-module resolution
and denies network, child-process, filesystem read, and filesystem write access. It validates SSR,
containers, highlighting, reference links, and inert document code. The generated bundle is an
integration probe, not a shipped executable resource. The build also regenerates the committed
`bundle/service.js` embedded by the executable. Rebuild and commit it whenever its source or
dependencies change.

## Deno permissions

Source tests allow reads of `NODE_ENV` (Vue) and `VSCODE_TEXTMATE_DEBUG` (TextMate) from the
environment. The bundled smoke requires only `VSCODE_TEXTMATE_DEBUG`; Deno's bundler substitutes
`NODE_ENV` during the build. Neither command grants runtime network, process launch, or filesystem
access. Dependency installation and bundling require package-cache access and, on a cold cache,
registry access; the build writes only its output directories.

The supervised service additionally reads `MAIEUTICS_RENDERER_CALLBACK`, `MAIEUTICS_RENDERER_TOKEN`,
and `MAIEUTICS_RENDERER_NONCE`. Its built-in policy permits listening on `127.0.0.1:0` and calling
the kernel's concrete loopback frontend port. The kernel overlays app and workspace permission
layers, renders that captured effective policy before launch, and clears the inherited environment.
It provides only renderer variables, a private `DENO_DIR`, and Windows `SYSTEMROOT` when needed.
Readiness and internal render requests use authenticated HTTP. No binary data or conversation state
travels through stdout. The child belongs to the executable's presentation scope independently of
Agent runs and frontend subscribers.
