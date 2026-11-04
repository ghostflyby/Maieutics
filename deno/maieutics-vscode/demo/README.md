# Kernel rendering demo

This demo uses the actual Notebook renderer, extension messaging bridge, executable API, and owned
offline Deno child. It needs no model request: the notebook contains a portable output snapshot.

Build the executable and extension from the repository root:

```sh
dotnet build Maieutics.slnx
deno task --config deno/maieutics-vscode/deno.json build
```

Load this extension in VS Code using its development workflow or a locally packaged VSIX. Start a
kernel in another terminal from the repository root, with private demo state:

```sh
render_demo_root="$(mktemp -d)"
printf 'Discovery file: %s/discovery.json\n' "$render_demo_root"
MAIEUTICS_PLUGINS_ROOT="$render_demo_root/plugins" \
MAIEUTICS_PLUGIN_APPROVALS="$render_demo_root/approvals.json" \
Maieutics/bin/Debug/net10.0/maieutics \
  --config deno/maieutics-vscode/demo/maieutics.json \
  --frontend-discovery "$render_demo_root/discovery.json" \
  --Maieutics:DataRoot "$render_demo_root/data"
```

On Windows, use the corresponding `maieutics.exe` and an explicitly created temporary directory. In
VS Code set `maieutics.discoveryFile` to the printed absolute path, then run **Maieutics: New
Session** to attach the extension. Open `ssr.maieuticsnb` from this directory. Its output should
show a tip container, a table, highlighted TypeScript, and expandable details. The fixture does not
need to be executed. The demo model configuration supplies an unused local placeholder endpoint;
normal Agent turns need a real provider configuration.

Open the same snapshot without an attached connection to inspect its native Markdown fallback.
Opening a snapshot never starts a kernel solely for rendering. Stop the demo kernel with Ctrl+C; its
child and staging directory are released with the host. Clear the temporary discovery setting when
returning to an ordinary workspace configuration.

## Automated verification

With a kernel running, this command also verifies the production extension bridge and actual
Notebook MIME renderer against that kernel, in an isolated VS Code instance:

```sh
MAIEUTICS_RENDERING_DEMO_DISCOVERY="/absolute/path/discovery.json" \
deno task --config deno/maieutics-vscode/deno.json test:vscode
```

The probe waits for the renderer's application acknowledgement after it replaces the output DOM.
Without the environment variable, the standard webview test uses a snapshot fixture. Other coverage
checks coalescing, stale-result rejection, cancellation, native fallback, raw HTML remaining inert,
real child startup/disposal, and published NativeAOT rendering.

This initial demo updates complete text regions. Revisioned document patches, late TS bindings,
interactive Vue components, and frontend resource URL rewriting remain future stages in the
[design document](../../../docs/streaming-output-rendering-design.md).
