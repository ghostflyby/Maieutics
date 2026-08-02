# ADR 0012: Flat Notebook Command Syntax and Slash Completion

Status: Accepted

Date: 2026-08-02

Supersedes: the notebook control cell syntax examples in ADR 0008

## Context

Notebook control cells used the namespaced `%maieutics <family> <subcommand>` form. The Maieutics Kernel is the only
owner of these commands, so the namespace provides no real collision isolation, while `%` remains the Jupyter-native
signal for control text. Agent-facing tools use slash commands, which are unfamiliar to Jupyter notebooks and collide
with absolute paths and URLs when text starts with `/`.

## Decision

The canonical control cell syntax is flat: `%model ...` and `%workspace ...`. The legacy `%maieutics model|workspace`
forms remain accepted and are deprecated. A leading slash is a completion-only discovery gesture: the Kernel returns
`%`-prefixed command candidates that replace the slash token when accepted, and slash-prefixed cells are never treated
as commands at execution time. Execution detection uses an exact first-token whitelist, so arbitrary text (including
paths) is never intercepted.

## Consequences

- Existing notebooks continue to run through the legacy alias while new input uses the shorter canonical forms.
- Slash discovery works through kernel-provided Jupyter completion on Tab across frontends; automatic pop-up on typing
  `/` depends on the frontend and is not part of the Kernel contract.
- No wire protocol, persisted format, IPC, or public API changes are involved; this is a Kernel control-surface change.
