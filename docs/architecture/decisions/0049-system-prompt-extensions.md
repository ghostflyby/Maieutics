# ADR 0049: System Prompt Extensions

Status: Accepted

Date: 2026-11-06

Related: ADR 0016 (plugin extension points), ADR 0020 (host invocation), ADR 0037
(declaration approval), ADR 0039 (skills), ADR 0040 (contribution kinds), ADR 0041
(user-selected MCP prompts), ADR 0048 (live assistant output).

## Context

Plugins can advertise skills or provide user-selected MCP prompts, but neither is an
unconditional system instruction. The first VitePress capability prompt was attached through
a dedicated product profile decorator. A general extension point should own this composition
surface, with explicit approval for plugins that can influence system instructions.

## Decision

1. **One append-only contribution kind, `SystemPrompt`.** The manifest kind catalog and
   worker extension-point catalog recognize the name case-insensitively, recording its canonical
   spelling. A contribution is `{name, content}`. The configured base prompt remains first;
   contributions are appended with kernel-authored source/name attribution. The extension does
   not replace the base prompt or receive its contents. These are intentionally system-level
   instructions from approved code, not tool results promoted into policy.

2. **Static and worker forms require fingerprinted declarations.** A plugin can declare:

   ```json
   {
     "extensions": {
       "SystemPrompt": [
         { "name": "style", "content": "Use concise explanations." },
         { "provider": "main" }
       ]
     },
     "entrypoints": { "worker": { "main": ["./mod.ts"] } }
   }
   ```

   The worker's module exports, for example:

   ```ts
   import { defineExtensionPoint } from "@maieutics/plugin-sdk";

   export const instructions = defineExtensionPoint("SystemPrompt", ({ workspaceRoot }) => [
     { name: "workspace", content: `The current workspace is ${workspaceRoot ?? "unset"}.` },
   ]);
   ```

   `provider` names the declared **worker export**, not the module's JavaScript export binding.
   A registered hook without a corresponding provider selector is never invoked for instructions.
   Static-only plugins need no worker. Text and provider selectors enter the existing extensions
   fingerprint domain; entrypoints enter their existing domain. No new hash domain or persisted
   approval format is needed. A changed declaration becomes pending until approved. Recognizing
   a previously unknown `SystemPrompt` declaration also changes its interpreted fingerprint and
   cannot silently activate a formerly inert declaration under its old approval. Fingerprints
   for declarations without this kind remain byte-stable.

3. **Capture once per conversation run.** After acquiring the session-selected model profile,
   `SystemPromptProfileProvider` awaits the immutable source snapshot. It composes with the existing
   subagent-options, skill and title decorators while preserving all other options, tools and
   metadata. New/resumed/forked sessions share this path. Consecutive runs recollect; source text
   never accumulates in configuration or canonical history. An active model/tool loop retains its
   captured instructions. Metadata-only title requests bypass these decorators; child agents retain
   the existing task-specific prompt replacement semantics.

4. **Per-run delivery, not a background sticky slot.** Static entries are read from the current
   approved descriptor. Worker providers are invoked in ordinal plugin-id/export order, after
   declarations in manifest order. Each provider runs once per acquisition. The collection has
   one five-second total wait budget and propagates caller cancellation. Existing authenticated
   `host.invoke` request/reply correlation and lifecycle ownership apply; no new endpoint, process,
   event loop or publisher is added. A slow hook's invocation remains owned by the existing host;
   the collection deadline bounds waiting, not arbitrary plugin CPU execution. Failure, timeout,
   missing results and invalid output log safe diagnostics and contribute no cached text.
   Provider declarations also route a cold worker before its first registry frame, so startup
   ordering cannot silently omit the first run's hook. Connection readiness shares the same budget.
   Declaration/provider removal, approval revocation, replaced descriptors and host
   stop are rechecked before publication. No lock is held across an invocation await.

5. **Bound and validate every source.** Names are 1–64 ASCII letters/digits/`-_.`; content is
   nonblank and at most 16,384 UTF-16 units. Each plugin declares at most 32 entries/selectors and
   contributes at most 32 named entries totalling 32,768 content units across static/worker forms.
   Duplicate names reject that plugin's assembled set. A malformed static set is omitted; a malformed
   worker result is omitted as a set. Unknown entry fields are tolerated. Text is literal: `${...}`
   is not interpolated and cannot read environment values. Source framing plus all contribution text
   has a 65,536-unit per-run cap, excluding the configured base prompt. Aggregate overflow fails
   profile acquisition before calling the model and disposes the acquired lease; it is never silently
   truncated. Cancellation and other acquisition failures release the same lease.

6. **Additive IPC contract.** `ReplExtensionPointName.SystemPrompt` is `"SystemPrompt"`;
   the SDK marker is `Symbol.for("maieutics/extensionPoint/v1/system.prompt")`. The request is
   `{workspaceRoot: string | null}` and successful output is `{name, content}[]`. No transcript,
   base prompt, credentials or provider SDK data crosses this boundary. The existing versioned
   envelope tolerates unknown fields; older kernels diagnose and ignore the unknown kind/marker.
   NativeAOT serializes the context through `ReplControlJsonContext`. Frontend wire, notebook
   snapshots and canonical transcript formats are unchanged.

7. **Move VitePress instructions onto the same contract.** The renderer owns an embedded static
   manifest, `deno/maieutics-output-renderer/system-prompt.json`. The composition root reads its
   `extensions.SystemPrompt` entries with the same validator and registers them as
   `builtin:output-rendering`, alongside the approved plugin source. Built-ins are immutable
   application code/data, not disk-loaded plugins or an approval exemption. The generic run-profile
   decorator contains no VitePress-specific text. Its existing authoring examples, completion
   boundary and supported-subset limits remain unchanged.

## Consequences

Plugin authors now have an explicit system-instruction surface in addition to skills and
user-selected MCP prompts. Users approve its declaration and can revoke it for subsequent runs.
Dynamic instructions cost one bounded hook collection per run, not per model iteration, and do not
gain access to the current conversation. New contextual fields, replacement/priority rules,
runtime prompt publication and child-agent instruction inheritance require separate contracts.
