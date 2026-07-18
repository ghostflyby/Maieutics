# ADR 0007: Runtime Configuration Location and Hot Reload

Status: Accepted

Date: 2026-07-18

## Context

The NativeAOT executable may run from a portable directory, a normal per-user installation, Jupyter's process launcher,
or a container. A notebook working directory is not a trustworthy or stable application configuration root. The Agent
runtime also holds long-lived conversation state, so replacing a model client or limit in the middle of a turn would
break provider continuation and transcript semantics.

## Decision

Maieutics selects exactly one external `maieutics.json` file at startup. Explicit `--config` and `MAIEUTICS_CONFIG`
paths take precedence, followed by an existing file beside the executable and then the platform user application-data
path. The current working directory is used only to resolve an explicitly supplied relative path.

The JSON file is followed by environment and command-line providers. The selected path is fixed for the process
lifetime, while its contents are monitored. Each valid Maieutics subtree is bound and validated as one immutable
candidate. Invalid updates retain the last-known-good snapshot.

Provider clients are owned by reference-counted generations. A run acquires a profile lease, and a changed Provider
configuration is published only after its new client has been constructed successfully. Retired clients are disposed
after their final run lease is released. Agent and Jupyter presentation options are captured at operation boundaries;
the Jupyter connection file remains a startup-only setting.

## Consequences

- NativeAOT deployments may be genuinely portable without making the executable directory the only supported location.
- Notebook directories cannot implicitly replace provider endpoints or credentials.
- Active tool loops remain on one model client and one immutable set of limits.
- Environment variables and command-line arguments do not hot reload.
- Configuration errors are observable through structured logs without terminating a healthy Kernel.
- Adding a Provider extends the executable registry and `Providers` section without changing Agent or Jupyter APIs.
