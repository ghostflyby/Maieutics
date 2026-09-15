# ADR 0027: Fixed Workspace Home and Managed Project Links

Status: Implemented

Date: 2026-09-13

## Context

The workspace root is a single directory: configured via `Maieutics:Workspace:Root` or,
unconfigured, the process startup current directory. `%workspace use` re-points the root
mid-process (the snapshot field is named session override, but the workspace is a process
singleton, so the switch is process-wide). Two properties shaped everything above it:

1. **The security model forbids reparse points.** The root cannot be a symbolic link, and
   `workspace://local` resolution rejects a reparse point at every segment; on Unix every
   open re-walks the path with `openat` + `O_NOFOLLOW`, so a hostile workspace cannot make a
   tool read outside the root. `.git` is denied by segment name.
2. **`${var.workspace}` equals the root.** Permission path patterns are expressed against
   it through the variable table, and the Deno REPL child's working directory follows it.

This couples the whole tool surface to wherever the process happened to start, gives the
agent no persistent space of its own (everything it writes lands in the user's project via
the REPL or terminal), allows only one external project at a time, and makes the root — and
therefore every permission grant pattern — depend on launch context.

The workspace mode migrates to a **fixed, product-owned workspace home** with externally
opened disk project directories **linked** into it. Destructive migration is accepted
(user decision, 2026-09-13): persisted transcripts holding pre-0027 `workspace://local`
URIs are rejected with typed errors, and the `%workspace` command grammar may break.

## Decision

### 1. The home is the root, under the application data root

The workspace root becomes `<DataRoot>/workspaces` (`ApplicationPaths.DataRoot`, ADR 0022),
overridable at startup by the same `Maieutics:Workspace:Root` configuration key, which now
means *home override* instead of *root selection*. The home must be a real directory (not a
reparse point) — that validation is unchanged. The home layout:

```
<DataRoot>/workspaces/         ← workspace root (${var.workspace})
  projects/                    ← managed project links
  scratch/                     ← agent-owned persistent space
  .maieutics/                  ← product state, incl. registry.json
```

`.maieutics` joins `.git` in the per-segment deny list (resolution refuses it at any
depth), so the presented root is self-contained without exposing registry state to tools.

### 2. `projects/*` namespace, never flat

External projects mount at `workspace://local/projects/<name>/...`. The top level stays
reserved for product-owned content (`projects/`, `scratch/`, `.maieutics/`), which removes
the class of collisions between external project names and product entries, gives the
agent-owned persistent space a natural place, and makes the managed-traversal rule a single
checkable statement: *the only reparse point a workspace URI may traverse is the segment at
`projects/<name>`, and only when the registry validates it*. A project directory literally
named `projects` cannot exist at the top level because external names only ever live one
level below it.

The layout does not depend on how many projects are open: a single project still mounts
under `projects/`. Opening a second project must never rewrite the first project's URIs —
the transcript is the authoritative history (invariant 1) and cites `workspace://local`
URIs across turns and sessions.

### 3. Managed links: symlink on POSIX, junction on Windows

The physical form is a directory symlink created with `Directory.CreateSymbolicLink` on
POSIX and a **directory junction** on Windows — junction creation needs no privilege, while
symbolic link creation needs `SeCreateSymbolicLinkPrivilege` (elevation or Developer Mode),
which is not a product requirement. .NET has no BCL junction-creation API, so the junction
is built with `DeviceIoControl` + `FSCTL_SET_REPARSE_POINT` (`IO_REPARSE_TAG_MOUNT_POINT`,
substitute name `\??\<target>`), following the existing `LibraryImport` P/Invoke pattern.
Windows junction targets are absolute local paths only; a target on a UNC/network path
fails with a typed error at open time. A symlink fallback for privileged or network cases
is deliberately deferred and is not a default.

Both forms carry the `ReparsePoint` attribute and are resolved with
`ResolveLinkTarget(returnFinalTarget: true)`; one validation rule covers both. Below a
managed hop the no-follow discipline continues *inside* the target: links inside a user's
project still cannot escape.

### 4. The registry is authoritative; links are derived state

`.maieutics/registry.json` (version 1, camelCase, unknown fields tolerated, source-generated
serialization) maps link names to canonical realpaths. Records:

- `links`: open entries — `name`, canonical `target` (final resolved target), optional
  `alias`, `openedUtc`;
- `retired`: closed name↔target tombstones.

A link name is bound to its target **forever**: after a close, the name is never reused for
a different path (the newcomer gets a suffix). Otherwise old transcripts citing
`workspace://local/projects/<name>/...` would silently read a different project. Existing
entries are never renamed or re-pointed. Opening the same canonical target twice is
idempotent and returns the existing entry.

Physical links are repaired at startup against the registry (recreated when missing or
mismatched) and deleted on close. A `projects/<name>` entry that exists but is *not* a
reparse point is never touched — it may be user data; it simply does not participate in
managed traversal. An unregistered reparse point under `projects/` is inert: resolution
rejects it like any other link.

Startup remounts every open entry whose target exists. A missing target keeps its registry
entry; using the link fails with a typed error until the target reappears.

### 5. Naming

The default link name is the target directory's basename, verbatim (no case folding),
sanitized once: characters invalid on Windows (`< > : " / \ | ? *`, control characters)
become `_`, trailing dots and spaces are stripped, Windows reserved device names and an
empty result are rejected with a typed error asking for an explicit alias. Names are unique
case-insensitively (APFS and NTFS are case-insensitive by default) while preserving case
for display.

On collision with a *different* canonical target, the name becomes
`<name>-<hash>`, where hash is the leading 6 hex characters of the SHA-256 of the canonical
target (extended character by character on the astronomically unlikely suffix collision).
A path-derived hash keeps the assignment order-independent: the same project gets the same
name regardless of when or where it was opened, unlike ordinals. An explicit alias given at
open time is the only way to choose a name in the presence of a collision.

### 6. Logical URIs, physical paths

`workspace://local` URIs are **logical**: rooted at the home, always spelled through the
`projects/<name>` hop, even though every path past the hop is physically outside the root.
Resolution returns the physical path for I/O and the logical URI for output; URIs are built
from logical segments and never re-derived from physical paths beyond the hop (the old
`Path.GetRelativePath(RootPath, ...)` escape check would reject exactly those paths).
Directory cursors keep binding to the workspace version, which now also increments when a
link opens or closes.

### 7. Permission variables

`${var.workspace}` keeps its name and now denotes the home root — a deliberate breaking
change of meaning. Each open link contributes `${var.project.<name>}` (name matched
case-insensitively, unknown names fail expansion as today) resolving to the canonical
realpath, so permission configs can grant a project without literal duplicated paths.
Granting the home root does **not** grant link targets: Deno resolves real paths when
checking `--allow-read`, so the registry realpaths must be granted explicitly — opening a
project and granting it are deliberately separate acts.

### 8. Command surface (breaking)

- `%workspace` / `%workspace current` — render the home root and open links;
- `%workspace open <path>` — open an external project; optional trailing ` as <alias>`;
- `%workspace close <name>` — close and remove a link;
- `%workspace use|reset` are removed. The root never changes mid-process; the snapshot's
  session-override flag and the `%status` "session override" workspace wording go with it.

## Consequences and follow-ups

- Terminal and Deno REPL children do not automatically gain grants on link realpaths;
  permission configs should use `${var.project.*}` until (if) auto-granting lands.
- Search and list skip `.maieutics` alongside `.git`, count unregistered reparse points as
  skipped symbolic links, and descend only through registered hops.
- Windows deletion semantics, pinned by integration tests: .NET's recursive delete does
  not follow a junction — it throws the moment it meets one — so product cleanup only ever
  removes links non-recursively (`Directory.Delete(link, recursive: false)` removes the
  reparse point itself), and link-aware test cleanup sweeps links before recursive
  deletion. Enumeration does not follow reparse points.
- The REPL child's working directory is now the home root; a session starts in its own
  persistent space and reaches projects through `projects/<name>`.
- Deleting a *dangling* physical link — one whose target has vanished — needs a fallback:
  `Directory.Delete` stats through the link, reports the whole path missing, and would
  crash remount; `ManagedWorkspaceLink.Delete` falls back to unlinking the reparse point
  itself.

## Amendment (2026-09-15): rename-surviving file identity

Status: Implemented (amends §4's name binding and the remount rules)

The path-only identity of §4 breaks in two ways that matter in practice. Renaming an
external project directory leaves the link dangling and the name effectively stranded: the
only recovery re-opens the project under a new suffixed name, invalidating every persisted
`projects/<name>` URI. Worse, a *different* directory placed at the registered path is
silently adopted — remount only checked that the path exists, so transcripts would read the
replacement without notice, exactly the silent drift §4 exists to prevent.

### Decision

1. **Records carry an optional fingerprint.** `WorkspaceLinkRecord.Identity` is the
   `(device, inode, birth time)` triple captured at open through a no-follow open:
   `fstat` on macOS, `statx` on Linux (which also yields the creation time, closing the
   inode-reuse hole on ext4), `GetFileInformationByHandle` on Windows (a zero file index,
   as on FAT, degrades to no fingerprint). Every capture failure degrades to null, and a
   fingerprint-less record keeps exactly the pre-amendment path-only semantics. The
   registry v1 format is extended additively; unknown fields stay tolerated in both
   directions.
2. **Matching is by object, not by path.** Fingerprints compare on device and inode, with
   birth time required to match only when both sides report one. A rename keeps the
   identity on every local file system; a cross-volume move changes it and is treated as a
   different project.
3. **Open reconciles by identity.** Opening the registered path of an existing record
   whose fingerprint no longer matches fails with a typed error instructing an explicit
   close. Opening a path where the *same object* now lives re-points the existing record
   (same name, alias, opening time) instead of creating a duplicate, so transcripts keep
   citing `projects/<name>`.
4. **Remount re-locates or withholds.** When the registered path is gone, the record's
   fingerprint is searched for in the target's *former parent directory* — bounded on
   purpose, covering the common rename-inside-its-folder case without volume scans. A
   match re-points the record (state "relocated") after re-running the target validation.
   When the registered path holds a different object and no relocation is allowed, the
   physical link is removed and the state is "identity changed": the entry and its name
   stay reserved for the registered object, and use fails loudly. The relocation search
   never follows reparse points.
5. **§4 amendment.** The forever binding is a name↔*object* binding, not name↔path. The
   recorded path may follow the same object when the fingerprint proves it; it is never
   re-pointed to a different object. Retired-name reuse requires the same fingerprint when
   both tombstone and candidate carry one (path-only reuse remains for legacy
   fingerprint-less tombstones) — otherwise replacing a closed project's directory would
   silently inherit its transcript names.
6. **Health is runtime state, not registry state.** Remount outcomes (verified,
   relocated, target missing, identity changed, failed) are rebuilt at startup and open,
   rendered by `%workspace`, and never persisted.

### Consequences

- Re-pointing updates `${var.project.<name>}` for future renders only; children already
  launched keep their earlier grants, consistent with §7's separation of opening and
  granting.
- Identity capture requires `fstat` (glibc ≥ 2.33 on Linux distributions without `statx`
  wrappers degrade to path-only), and file systems with unstable or absent identities
  (network file systems, FAT-family) degrade to path-only the same way — the feature
  sharpens identity where the platform supports it and changes nothing where it does not.
- The silent-follow behavior on same-path replacement is gone: a replaced directory is a
  typed "identity changed" state requiring an explicit close-and-re-open. That is a
  deliberate behavior change; the old behavior was the defect.
