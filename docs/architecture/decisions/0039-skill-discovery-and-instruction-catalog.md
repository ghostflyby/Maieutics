# ADR 0039: Skill Discovery and the Skill Instruction Catalog

Status: Draft

Date: 2026-10-07

Related: ADR 0026 (virtual resource URLs — the `skill://` plane), ADR 0032
(instruction-surface write gate — the filesystem trust anchor), ADR 0037 (plugin
declaration approval — governs plugin participation, not file content),
`docs/web-frontend-protocol.md` (no wire changes; the catalog is kernel-composed)

## Context

Maieutics had no skill concept: `.agents/skills/**` appeared only as a write-gated
instruction-surface path (ADR 0032). But the skill pattern — a named, human-authored
procedure whose one-line description rides the system prompt and whose body is read on
demand — is the cheapest reuse unit an agent session can offer, and the ecosystem
convention (`<root>/.agents/skills/<name>/SKILL.md`) already exists in the wild.

Two design forces shaped this ADR:

1. **Trust anchors differ by source.** A skill that arrives as a file was authored by a
   human; its mutation channel is already closed by the instruction-surface write gate
   (ADR 0032 denies model-initiated writes by default). A skill that arrives from a
   plugin is declaration-governed content whose *participation* is approved and
   fingerprinted (ADR 0037). One mechanism must not force the other's trust model onto
   its source: file skills carry no approval, plugin skills carry no write gate.
2. **Location is dynamic.** The interesting roots — the workspace and the user profile —
   sit outside every plugin root, and a static manifest cannot express them. The
   framework therefore separates *where skills come from* (sources) from *how a source
   produces its contribution* (production modes), and stages plugin integration as
   later production modes on the same registry.

## Decision

1. **One merged catalog, sources by precedence.** A kernel singleton (`SkillCatalog`)
   merges contributions in the order workspace → user → plugin (plugin kinds arriving
   with stages 2-3). A same-name skill from a lower-precedence source is shadowed with a
   diagnostic; nothing errors. Discovery failures are per-skill diagnostics carried in
   the snapshot — a broken skill never blocks the catalog, the prompt, or the process.

2. **The discovery primitive is one rule set for every layout.** Within a declared root
   the walk is recursive, and the shallowest `SKILL.md` in any subtree fixes that
   subtree's skill boundary: deeper `SKILL.md` files inside an accepted skill directory
   are that skill's resources, never nested skills. The filename match is exact
   (`SKILL.md`, ordinal). A root that directly contains a `SKILL.md` is a
   single-directory skill (name from frontmatter, fallback the root directory name);
   otherwise each first-level directory carrying a `SKILL.md` is a flat skill (name from
   frontmatter, fallback the directory name). Frontmatter is a restricted line format —
   one `---`-fenced block of `key: value` scalars, only `name` and `description` read,
   unknown keys ignored — parsed without a YAML dependency. A skill without a
   description is inert (the catalog entry *is* the description). Names are the
   `skill://` host: `^[a-z0-9][a-z0-9-]{0,63}$`. Containment is enforced lexically and
   against the final link target, both at discovery and at every body read.

3. **`skill://` is a reserved built-in scheme.** The plane is the whitelist: only files
   discovery accepted are readable, which is exactly what lets user-profile skills
   (outside the workspace) be served without granting the model any broader filesystem
   access. `skill://{name}` is the only accepted URI form (verbatim, like
   `objects://{hash}`); bodies stream fresh from disk on every read, bounded; inert
   skills never enter the plane. The scheme joins both reserved-scheme tables
   (`ResourceRegistry.ReservedSchemeOwners`, `CustomResourceProviderOptions.ReservedSchemes`).

4. **Propagation is next-turn, by construction.** The catalog is appended to
   `SystemPrompt` when a run's profile lease is acquired — the same per-run composition
   point the subagents decorator uses. An in-flight run keeps its captured instructions;
   a watcher-driven catalog rebuild reaches the next run. The appended section never
   enters configuration-reload identity because it composes after lease acquisition.
   Subagent child runs replace the system prompt wholesale (existing semantics) and
   therefore carry no catalog. The prompt carries only name, description, source group,
   and `skill://` pointer — bodies are read on demand.

5. **Filesystem roots are startup-fixed; freshness is event-driven.** Stage 1 ships two
   sources: `<workspace-root>/.agents/skills` and `~/.agents/skills` (both created if
   missing, both overridable via `Maieutics:Skills`, discovery disableable). Each root
   owns a filesystem watcher whose events feed a per-root debounce pump that rescans and
   atomically republishes the merged snapshot. Roots do not participate in configuration
   reload; the catalog is not a model setting.

6. **Plugin participation is staged as production modes on the same registry.** Each
   plugin owns one contribution slot; modes merge within the slot and the slot joins the
   catalog below both filesystem sources, labeled by source (`Plugin skills`,
   `Plugin-generated`, `Plugin-published`) so plugin context is never presented as human
   policy. All modes ride the existing extension-point grammar
   (`extensions: { "Skills": [...] }`) and the `McpDiscover` invocation machinery:
   - **Stage 2a — interpolated roots**: declared roots expand through the single-source
     variable table (`${env.*}`, `${var.*}`), resolved at load; the fingerprint hashes
     the literal patterns. A root resolving outside the plugin root is admitted only
     when the plugin's own (fingerprinted) read grants cover it — deny-wins — so an env
     change can redirect a root only inside already-approved read scope.
   - **Stage 2b — generator entrypoint**: the kernel pulls a catalog from a declared TS
     module at load/reload; output is schema-validated and bounded; a failed
     contribution keeps the last good one (the MCP discovery discipline).
   - **Stage 3 — worker publish**: a capability-gated `skills.publish` model API pushes
     contributions at worker runtime into the same slot with atomic snapshot semantics.
   In-corpus correction to the earlier design discussion: `SKILL.md` bodies inside a
   plugin root do **not** enter the approval fingerprint. ADR 0037 fingerprints
   declarations, never code; in-root skill files are code-like, and out-of-root reads
   are gated by the fingerprinted read grants.

## Consequences

- The Agent runtime stays untouched: no Maieutics.Agent change carries the catalog; the
  executable composes above the profile provider boundary, as it already does for
  subagents.
- `read_text` on `skill://` is the only body path for the model; the Deno control
  channel's `/v1/resource` bridge reaches the same registry unchanged.
- Catalog cost per run is names and descriptions only; body cost is paid on demand and
  bounded by the plane's read limit.
- The config mirror of reserved schemes gains `skill`; the pre-existing omission of
  `objects` from that mirror is unchanged by this ADR (a separate hygiene fix).
- Stages 2-3 will need their own review rounds for the manifest grammar, fingerprint
  domain wording, generator contract, and SDK surface; this ADR fixes the registry,
  precedence, plane, and propagation semantics they plug into.
