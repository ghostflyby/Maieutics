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
   against the final link target, both at discovery and at every body read. The same rule
   set is exposed as a lazy streaming walk of one region subtree (region root plus
   declared root) that yields each skill directory the moment it is found: a region walk
   yields exactly the descriptors a full-root walk would yield for that subtree, so a
   rescan can difference one region without a second rule set or a whole-root rewalk.

3. **`skill://` is a reserved built-in scheme.** The plane is the whitelist: only files
   discovery accepted are readable, which is exactly what lets user-profile skills
   (outside the workspace) be served without granting the model any broader filesystem
   access. `skill://{name}` is the only accepted URI form (verbatim, like
   `objects://{hash}`); bodies stream fresh from disk on every read, bounded; inert
   skills never enter the plane. The scheme joins both reserved-scheme tables
   (`ResourceRegistry.ReservedSchemeOwners`, `CustomResourceProviderOptions.ReservedSchemes`).
   Containment closes the known reparse-point traps: a symlinked `SKILL.md` resolving
   outside its root is rejected (final-component link resolution at discovery and again
   at every body read), and symlinked or junctioned *directories* are not traversed at
   all — `ResolveLinkTarget` walks only the final component (the `WorkspaceHome`
   canonicalization trap), so ancestor links cannot be checked per-file cheaply; a skill
   set living behind a link is declared by configuring that location as its own root.

4. **Propagation is next-turn, by construction.** The catalog is appended to
   `SystemPrompt` when a run's profile lease is acquired — the same per-run composition
   point the subagents decorator uses. An in-flight run keeps its captured instructions;
   a watcher-driven catalog rebuild reaches the next run. The appended section never
   enters configuration-reload identity because it composes after lease acquisition.
   Subagent child runs replace the system prompt wholesale (existing semantics) and
   therefore carry no catalog. The prompt carries only name, description, source group,
   and `skill://` pointer — bodies are read on demand. Catalog content is untrusted file
   content: scalar values containing control characters are rejected (the one-line-per-
   entry framing is the structural boundary of the section), and the composed section
   carries its own total budget (entry count and characters, truncating with a visible
   omission line) so per-item bounds cannot be multiplied into a hostile half-megabyte
   prompt.

5. **Filesystem roots are startup-fixed; freshness is event-driven and differential.**
   Stage 1 ships two sources: `<workspace-root>/.agents/skills` and `~/.agents/skills`
   (both created if missing, both overridable via `Maieutics:Skills`, discovery
   disableable). Each root owns a filesystem watcher whose events feed a per-root debounce
   pump that differences by event path, holding each root's state as a skill-directory-
   indexed map. Across the debounce window the pump collects the event paths (both halves
   of a rename); a path inside a known skill directory can only change that one skill (its
   subtree is resources), so exactly that directory is revalidated — and when its
   `SKILL.md` has vanished the claim is released and the directory itself is rewalked as a
   region, re-exposing the deeper skills that were resources a moment ago. Any other path
   becomes its own region root whose subtree walk (decision 2's streaming core) is diffed
   against everything known under it, so a new mid-tree boundary `SKILL.md` preempts the
   skills below it and removing one re-exposes them. Batches apply atomically under the
   catalog gate; a watcher error or a pending-set overflow (events were lost, the
   differential cannot know which regions changed) escalates to one full-root differential
   walk. The initial scan streams: each discovered skill directory commits as one atomic
   update-and-republish, so the catalog goes live per directory instead of waiting for the
   whole root. Roots do not participate in configuration reload; the catalog is not a
   model setting.

6. **Plugin participation is staged as production modes on the same registry.** Each
   plugin owns one contribution slot; modes merge within the slot and the slot joins the
   catalog below both filesystem sources, labeled by source (`Plugin skills`,
   `Plugin-generated`, `Plugin-published`) so plugin context is never presented as human
   policy. Both stage-2 modes ride the existing extension-point machinery:
   - **Stage 2a — declarative interpolated roots (shipped)**: the manifest
     `extensions: { "skills": [ { "roots": [...] } ] }` kind (the lowercase spelling is
     the recommended form; case is not significant at either name catalog, and recorded
     registrations canonicalize); roots expand through the
     single-source manifest variable table (`${env.*}`, `${var.*}` with the ADR 0018 §4
     fixed set wired in production), resolve relative to the plugin root, and a root
     resolving outside it is admitted only when the plugin's own (fingerprinted) read
     grants cover the resolved path — deny-wins, so an env change can redirect a root
     only inside already-approved read scope. The extensions JSON rides the existing
     fingerprint domain (literal patterns), so declaring roots changes the approval.
     Grant values are evaluated as literal paths: `${...}` tokens inside a read grant
     are inert for this gate (fail-closed) — cross-root grants must be spelled
     literally in deno.json.
   - **Stage 2b — worker generator (shipped)**: a worker exporting the `Skills`
     extension point (`defineExtensionPoint("Skills", ...)`) is invoked by the kernel at
     the same reconcile boundaries MCP discovery uses (start, registry frames, approval
     transitions, triggers), targeted per plugin: a registry frame diffs the before/after
     `Skills` export sets of each plugin (an unchanged frame invokes no generator); an
     approval snapshot reconciles only the transitioning plugins that have a skill face —
     a held slot counts, so a revoked publish-only plugin still loses its published part;
     a trigger reconciles exactly the triggered plugin; the start seed reconciles plugins
     progressively. The request carries the live workspace root, never arbitrary
     environment; the returned array is validated under the same bounds a filesystem
     skill satisfies (name charset, clean one-line description, bounded inline body) and
     served through the `skill://` plane as inline content. A failed or empty invoke
     keeps the last good generated contribution (the MCP discovery discipline) while
     declarative roots recompute fresh. Passes serialize per plugin and coalesce (same
     plugin serial, different plugins parallel, no lock held across a generator await; a
     commit declines a generation token cancelled by the stop path); approval gates
     both modes before any contribution or invoke.
   - **Stage 3 — worker publish (shipped)**: the catalogued `skills.publish` capability
     (manifest-declared, deny-by-default like every capability grant) lets a granted
     worker replace its published skill set at runtime — the same entry shape and bounds
     the generator returns, an empty array clears the part — and the recomposed slot
     commits atomically against concurrent reconcile passes. The published part sits
     below the declarative roots and the generated part within the plugin's slot; SDK
     surface `capabilities.skills.publish(...)`.
   In-corpus correction to the earlier design discussion: `SKILL.md` bodies inside a
   plugin root do **not** enter the approval fingerprint. ADR 0037 fingerprints
   declarations, never code; in-root skill files are code-like, and out-of-root reads
   are gated by the fingerprinted read grants. One further production wiring note: the
   manifest variable table now reaches every manifest load (trigger paths included),
   which is the ADR 0018 §4 intent — previously production loads expanded against an
   empty source and any `${...}` token failed the plugin.

   The MCP discovery coordination sync on the same plugin surface is differential in both
   directions too: a coordinator revision carries the full registration set *and* a forced
   re-discovery set. An unchanged registration reuses its published contribution without a
   worker invoke or a manifest re-read; new and never-succeeded registrations run
   discovery; removed registrations drop their contribution. Triggers and watched worker
   reloads publish in the forced form (their exports may return different results over
   identical registration records), while registry updates, approvals, and the start seed
   stay plain-differential — an empty previous set makes the seed a full discovery.

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
- A stage-1 limitation: a watched root deleted at runtime goes inert until process
  restart (the OS watch is gone; recreating the directory does not re-arm it). Watcher
  internal-buffer overflow schedules one recovery rescan, so event loss does not leave
  the catalog permanently stale.
- All three stages are implemented; the registry, precedence, plane, and propagation
  semantics this ADR fixed held through their review rounds unchanged.
