# Maieutics plugin SDK

SDK for writing Maieutics script plugins. A plugin is a standard Deno package:

- `deno.json` with `name`, `version`, `exports` (one subpath per extension carrier module), the
  standard `permissions` field (Deno 2.5+ named permission sets, positive grants only), and a
  `maieutics` marker field.
- The extension carrier module exports extension point implementations; the host recognizes them by
  their versioned marker symbols, not by export names.

Extension points are declared with `defineExtensionPoint(name, impl)` or by attaching the marker
symbol manually:

```ts
import { defineExtensionPoint, ExtensionPoint, type McpDiscovery } from "@maieutics/plugin-sdk";

export default defineExtensionPoint("McpDiscover", {
  handler(): McpDiscovery[] {
    return [{
      module: "npm:@scope/server",
      transport: { type: "http", url: "http://127.0.0.1:8080" },
    }];
  },
});
```

Object implementations must expose a `handler(context)` method; callable functions are accepted
directly. Markers are `Symbol.for` keys under `maieutics/extensionPoint/v1/...`, so they are stable
across worker isolates.

## Deno permissions

The SDK module itself performs no runtime I/O and requires no permissions to import. Plugins
importing it from `jsr:@maieutics/plugin-sdk` need the `import` permission for that specifier when
the host enforces import domains. The plugin package's own `permissions` field is enforced by the
host (worker permission grant for each carrier worker). Any change to the SDK that alters
environment, network, or filesystem behavior must update this list in the same change.

Validation: `deno task check` and `deno task test`.
