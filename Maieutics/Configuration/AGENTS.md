# Maieutics.Configuration instructions

Use `.agents/skills/maieutics-agent-runtime/SKILL.md` for run-profile semantics and
`.agents/skills/maieutics-structured-concurrency/SKILL.md` for generation/reload lifetimes.

## Ownership

This folder owns executable configuration discovery, binding, validation, reload, named model source/profile catalogs,
session-level profile selection, and reference-counted provider generation lifetimes.

## Configuration rules

- Select exactly one active `maieutics.json`: `--config`, then `MAIEUTICS_CONFIG`, then an existing file beside the
  executable, otherwise the platform user application-data path. Never implicitly load the notebook working directory.
- Precedence is defaults, active JSON, shortcut environment variables, standard .NET hierarchical environment
  variables, then command line.
- The active path is startup-only. JSON contents may reload; environment and command-line sources do not hot reload.
- Bind with the configuration source generator and validate the complete `Maieutics` subtree.
- Invalid JSON, options, references, unknown provider fields, or provider construction retain the last-known-good
  snapshot. A later valid file must recover without restart.
- Do not log secrets or complete candidate configuration.

## Catalog and lifetime rules

- Source and profile IDs are case-insensitive and validated. Sources own credentials, endpoint, and API flavor;
  profiles reference a source and model ID.
- Legacy and new configuration may be normalized only under the documented compatibility rule; mixed structures fail.
- Construct every changed profile generation before atomic publication. If any construction fails, dispose new clients
  and keep the previous catalog unchanged.
- Reuse unchanged generations. Replaced or removed generations retire only after their last run lease ends.
- `Acquire()` returns one immutable run-local client/options/identity/capability lease.
- A manual session override affects the next run, survives default changes while its profile exists, and resets to the
  new default if that profile is removed. It is not persisted.
- Connection-file changes are restart-required. Agent and Jupyter presentation settings apply at the next run or
  execution boundary, never midway through an active operation.
- Reload notification coalescing may drop duplicate signals, but never a published configuration or run profile.
