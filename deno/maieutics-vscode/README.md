# Maieutics for Visual Studio Code

Notebook-native frontend for the [Maieutics](../../) agent. The extension speaks the custom web
protocol (`docs/web-frontend-protocol.md`) over HTTP + WebSocket to the `maieutics` executable — no
Jupyter kernel involved (ADR 0023).

## Features

- `.maieuticsnb` notebooks: one ordinary cell is one submitted Agent turn.
- Streaming assistant markdown, tool activity, and typed turn failures.
- Command cells (`%status`, `%session`, `%model`, `%workspace`) answer inline.
- Session commands: new session, resume a stored session, show status.

## Connection

By default the extension launches the `maieutics` executable for the first workspace folder
(`maieutics.executablePath`) and reads the discovery file it publishes. To attach to an externally
launched instance instead, set `maieutics.discoveryFile` to the discovery file the instance was
started with (`maieutics --frontend-discovery <path>`).

## Development

The toolchain is pure Deno — dependencies live in `deno.json` (`package.json` is only the extension
manifest), and `@vscode/vsce` runs directly under `deno run npm:@vscode/vsce` (the old chalk
incompatibility, deno#26637, is fixed on current Deno).

```sh
deno task check   # type-check shipped sources against ES2023 + @types/node
deno task test    # unit tests (pure modules + protocol client against a mock server)
deno task build   # bundle dist/extension.js (CJS, vscode external)
deno task package # deno run npm:@vscode/vsce package --no-dependencies → .vsix
```

Type-checking uses two configs on purpose: `deno.extension.json` gives shipped sources an ES2023 lib
plus `@types/node` — the runtime the VS Code extension host actually provides — so no phantom Deno
globals slip into the bundle. The member `deno.json` (used by `deno test`) keeps the default Deno
libs because the tests use the Deno CLI runtime (`Deno.test`, `Deno.serve`).

Debug: open this folder in VS Code and use "Run Extension" (F5) after `deno task build` —
`launch.json` points the extension host at `dist/`.

## Permissions

Development runtime needs `allow-net` (loopback mock server tests) and `allow-read` (discovery
file). The packaged extension talks to the executable over loopback HTTP/WS only; spawning the
executable uses the Node child-process API, not Deno permissions.
