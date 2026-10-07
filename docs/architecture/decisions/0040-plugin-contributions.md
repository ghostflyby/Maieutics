# ADR 0040: Plugin Contribution Framework

Status: Draft

Date: 2026-10-07

Amends: [ADR 0033](0033-plugin-scoped-mcp-data-file.md) (the `mcp` data-entry path and
`McpDiscover` extension entries gain manifest-variable interpolation and optional timeout
overrides), [ADR 0039](0039-skill-discovery-and-instruction-catalog.md) (stage 2b/3's
kind-specific reconcile machinery — four dictionaries, per-kind export-set diff, per-kind
face and retry sets — generalizes into the shared contribution framework; every
differential semantic of stages 2a-3 is preserved verbatim)

Related: [ADR 0037](0037-plugin-declaration-approval.md) (fingerprints unchanged — this
ADR's first hard constraint), [ADR 0038](0038-universal-custom-ui-framework.md) (decision
7's generic fingerprint-domain coverage is promoted to the standing rule for new kinds),
[ADR 0034](0034-mcp-adjustment-chain.md) (the adjustment chain stays a consumer-engine
concern), [ADR 0036](0036-plugin-triggers.md) (not migrated; capacity validated only),
`docs/plugin-contribution-framework-design.md` (full design)

## Context

The kernel hosts two structurally identical plugin contribution mechanisms. MCP server
discovery (ADR 0033/0034) and skill contribution (ADR 0039 stages 2-3) each implement, per
kind: a manifest declaration grammar with its own kind/name catalog and hand-written
diagnostics text; a descriptor snapshot in `PluginHostManager`; synthetic registrations; a
registry-frame export-set diff; an approval-transition face check; forced-reload and retry
sets; a sticky state machine with per-plugin single-flight drain; and a capability publish
branch. Adding one new contribution kind touches roughly eleven such places scattered
across `PluginManifest`, `PluginDeclarationFingerprint` consumers, and `PluginHostManager`
— and the two existing kinds already disagree on configurability: dynamic MCP discovery
fixes all four timeouts (`PluginHostManager.cs:4153-4191`) while the `mcp` data file makes
them per-server options (`McpServerFile.cs:35-41`); skill roots interpolate through the
manifest variable table while MCP declaration entries cannot; skill entry diagnostics are
generated, MCP/Skills kind catalogs and data-name catalogs are hand-written (one of them
listing `mcp` where `ui` is also known).

Every added kind so far also re-decided — or re-derived by analogy — the same differential
disciplines: sticky last-good keyed by (plugin, export), never-succeeded-stays-new retry,
reload-epoch deferred forcing, and publish-only survival. UI forms (ADR 0038) and triggers
(ADR 0036) are the next kinds in line and would repeat the exercise.

## Decision

1. **One kind contract, one catalog, in the executable project.** A new namespace domain
   `Maieutics.Plugins.Contributions` (files under `Maieutics/Plugins/Contributions/`; no
   new assembly) defines a closed `ContributionKindContract` per kind — kind name and
   catalog metadata, declaration parsing (manifest entries → descriptor list, lazy
   per-entry diagnostics), the compute form (invoke request construction + output
   validation), the publish form (capability payload validation), and descriptor identity
   keys — all registered in a static `ContributionKindCatalog` that replaces
   `PluginExtensionKind` and `PluginDataName` consumption points and generates
   unknown-kind/unknown-name diagnostics from the catalog. Descriptors use a non-generic
   abstract base with closed concrete wrappers (`McpServerContribution` around
   `McpServerDefinition`, `SkillEntryContribution` around `SkillDescriptor`): the framework
   never serializes the abstraction, source-generated JSON contexts stay closed-type, and
   consumers pattern-match to their own concrete shape — AOT-safe by construction.

2. **Contribution slots.** One slot per `(pluginId, kind)` with three parts — declared
   (recomputed every pass), generated (sticky last-good keyed by the plugin's
   export/registration), published (wholesale replacement) — assembled and committed
   atomically under the owning gate, with the slot-holder face check and the
   generation-token commit decline preserved. The skills four-dictionary state becomes a
   shared `ContributionSlotTable` (comparers unified to Ordinal — behavior-equivalent); the
   MCP coordinator's per-registration candidate bag is the same three-part shape realized
   behind its own revision gate and is *not* relocated.

3. **One differential coordinator.** The kernel-side event-diff engine is written once: a
   registry frame diffs per-kind export sets; an approval snapshot takes the symmetric diff
   of blocked sets intersected with each kind's face; the forced set is the reload-epoch
   drain (mark before reload frames, drain on epoch advance or regress, full drain for
   epoch-less legacy hosts) plus triggers; kinds with a kernel-side retry set implement
   never-succeeded-stays-new; passes serialize and coalesce per plugin (single-flight
   drain with the finally re-arm; the gate is never held across an await). Per-kind
   deliveries go through an `IContributionDelivery` adapter.

4. **Consumer engines stay kind adapters.** The MCP revision engine (generations, merge
   conflict abort, adjustment-chain folds, commit-on-publish) and the skill catalog
   (per-root watchers, region walks, precedence merge, progressive commit) are the kinds'
   real differences and remain untouched. The framework unifies the contribution and
   coordination layers only.

5. **Configurability parity through kind metadata.** Bounds, stickiness, retry, default
   timeouts, and declaration interpolation become `ContributionKindMetadata`; both kinds
   share the framework knobs. MCP declaration entries gain `${env.*}`/`${var.*}`
   interpolation (data-entry path at collection, extension entries at interpretation) and
   optional timeout overrides whose defaults are byte-identical to today's fixed values.
   Two standing rules protect approvals: interpolated extension entries never enter the
   fingerprint's `mcp` domain (the `extensions` domain hashes their literal form, the
   skills-roots precedent), and a new kind must fit the generic `extensions`/`data`
   fingerprint domains — introducing a new dedicated domain changes the fingerprint bytes
   for every plugin and is out of scope for this framework.

6. **Registration convergence.** After the migration, adding a contribution kind touches
   exactly four places: the kernel-side kind contract (one closed class + one catalog
   registration), the Deno SDK symbols (if the kind has a worker form), the wire catalogs
   (`ReplExtensionPointName`/`ReplCapabilityName` constants), and the consumer adapter.
   Fingerprint coverage is free via the generic domains; snapshot dictionaries, synthetic
   registrations, frame diffs, faces, retry sets, and publish branches are absorbed by the
   contract, the slot table, and the coordinator.

7. **Migration in three behavior-preserving phases.** A: extract the shared engine
   (descriptors, slot table, coordinator) and rewire skills onto it, the MCP coordinator
   file untouched. B: unify the grammar and catalog under `ContributionKindCatalog` with a
   unified parse entry that feeds `PluginDescriptor` byte-identical values. C: wire the
   metadata knobs and MCP interpolation/timeout parity. Fingerprint golden fixtures are
   pinned before phase A and must not move through all phases; the existing suites
   (`PluginMcpCoordinatorTests`, `PluginSkillsContributionTests`,
   `PluginDeclarativeExtensionsTests`, `PluginHostInvokeTests`, and the broader plugin
   integration tests) must stay green with unchanged semantics in every phase. The one
   deliberate observable change is in phase B: catalog-generated unknown-data-entry
   diagnostics list both `mcp` and `ui` (today's text names only `mcp` while `IsKnown`
   accepts both).

8. **Non-goals.** UI forms and triggers are not migrated; this ADR only records that the
   contract's optional forms (no compute form, no publish form, declared-only slots,
   pre-existing dedicated fingerprint domains, empty export sets) can express them, which
   the design doc validates against both as future kinds. No host frame changes, no new
   assemblies, no new protocol surface.

## Consequences

- New contribution kinds stop multiplying per-kind copies of the differential machinery;
  the two existing mechanisms become two adapters over one contract, one slot model, and
  one coordinator.
- Existing approvals survive the whole migration byte-for-byte: the fingerprint's domain
  order and every domain's input values are frozen; relocations of parsing never change
  what is hashed. A golden-fixture test makes regression observable.
- The configurability asymmetries close: dynamic MCP discovery can opt into timeouts, MCP
  declarations interpolate like skills roots and triggers, and defaults reproduce today's
  values so existing generation keys (and therefore live connections) are unaffected.
- Known gaps are recorded, not silently fixed: the transient `unknown_manifest_plugin`
  window, the missing root containment for data-file stdio `workingDirectory`, the missing
  plugin-plane aggregate budget on skill snapshots, and the coordinator's by-design
  one-beat delay of forced re-runs under a concurrent plain publish each remain separate,
  explicit decisions.
- The skills/mcp split of retry responsibility is now explicit metadata: kinds whose
  reconcile passes run kernel-side carry the retry set; kinds with their own revision
  engine (MCP) keep the retry inside it.
- Wire, IPC, and persisted formats are unchanged; the approvals file format is unchanged;
  NativeAOT compatibility is preserved by the closed-class descriptor design and verified
  by the RID publish check at each phase boundary.
