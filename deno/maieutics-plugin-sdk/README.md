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

## Model-callable tools

`defineTool` declares a function the model can call. One export is one tool, so a carrier module
declares one `Tool` export (a second marked export logs a diagnostic and replaces the first). The
`input` schema is the single source of truth: it renders the provider-visible JSON Schema, validates
the model's arguments in the worker, and infers the handler's parameter type.

```ts
import { defineTool, schema, ToolError } from "@maieutics/plugin-sdk";

export const search = defineTool({
  name: "docs_search",
  description: "Search the local documentation index.",
  timeoutMs: 30_000,
  input: schema.object({
    query: schema.string({ description: "Free-text query.", minLength: 1 }),
    limit: schema.default(schema.integer({ minimum: 1, maximum: 50 }), 10),
    scopes: schema.optional(schema.array(schema.string())),
  }),
  async handler({ query, limit, scopes }, context) {
    const matches = await index.search(query, {
      limit,
      scopes,
      signal: context.signal,
    });
    if (matches.length === 0) throw new ToolError("no_match", "Nothing matched.");
    return { matches };
  },
});
```

- The tool name must be 1-64 ASCII letters, digits, underscores, or hyphens, and must not collide
  with a built-in, terminal, IDE, or MCP tool name; the kernel hides colliding declarations.
- `additionalProperties` defaults to `false`: an unknown input key is a typed `tool_input_invalid`
  error instead of being silently dropped. Pass `{ additionalProperties: true }` to accept them.
- No type coercion is performed (`"3"` is not a number). `schema.raw(jsonSchema)` is the escape
  hatch for JSON Schema the builders cannot express.
- `context.signal` aborts when the kernel cancels the call or the handler exceeds its `timeoutMs`. A
  handler that ignores it keeps running (its result is discarded), so honour the signal for long
  work.
- `timeoutMs` is the **handler's** budget, measured from the moment the handler starts: worker
  startup is not charged to it, so a short budget does not fire because the worker was cold. An
  over-budget call reports `tool_timeout`; a cancellation reports `tool_cancelled`.
- `ToolError` becomes a recoverable `{status: "error", code, message}` result; any other exception
  becomes `tool_failed`. Neither aborts the turn.
- Return JSON-serializable values. Return `undefined`/nothing for a null result. Binary content does
  not belong in a tool result; return a `workspace://` or `objects://` reference instead.

The kernel describes each tool export at registration and again on plugin reload or approval
transition; a failed describe keeps the previous declaration and retries on the next registry frame.

### Annotations

`annotations` classifies a tool for approval and presentation surfaces. These are hints only — no
authorization decision reads them, so declare them honestly.

```ts
export const removeFile = defineTool({
  name: "remove_file",
  description: "Delete a file from the workspace.",
  input: schema.object({ path: schema.string() }),
  annotations: {
    title: "Remove File",
    readOnly: false,
    destructive: true,
    idempotent: true,
    openWorld: false,
  },
  handler: ({ path }) => fs.remove(path),
});
```

| Member        | Meaning                                                    | Default                 |
| ------------- | ---------------------------------------------------------- | ----------------------- |
| `title`       | Display name; may contain spaces and punctuation           | absent (`name` is used) |
| `readOnly`    | The tool changes no state                                  | `false`                 |
| `destructive` | The tool may destroy data or resources                     | `true`                  |
| `idempotent`  | Repeating the call has no additional effect                | `false`                 |
| `openWorld`   | The tool reaches an unpredictable set of external entities | `true`                  |

The defaults are pessimistic in MCP's direction: an undeclared tool is assumed able to destroy, to
be non-idempotent, and to reach outside. Omitting the block entirely and passing `{}` mean the same
thing. A non-boolean hint or a blank/oversized title fails the declaration at load time rather than
silently keeping the default.

## Deno permissions

The SDK module itself performs no runtime I/O and requires no permissions to import. Plugins
importing it from `jsr:@maieutics/plugin-sdk` need the `import` permission for that specifier when
the host enforces import domains. The plugin package's own `permissions` field is enforced by the
host (worker permission grant for each carrier worker). Any change to the SDK that alters
environment, network, or filesystem behavior must update this list in the same change.

Validation: `deno task check` and `deno task test`.
