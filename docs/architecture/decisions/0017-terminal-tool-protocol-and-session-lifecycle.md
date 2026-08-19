# ADR 0017: Terminal Tool Protocol and Session Lifecycle

Status: Accepted

Date: 2026-08-15

Refines: ADR 0003, ADR 0005, and ADR 0011

## Context

The Agent needs a durable interactive terminal to run full-screen programs (vim, less, readline REPLs) whose state
lives in a terminal emulator, not in line-oriented output. The input surface is a sequence of typed characters and key
sequences; the output surface is a two-dimensional character matrix or its diff. The executable must start and supervise
a real PTY child, render its VT output into a screen buffer, and present bounded snapshots to the model. This is the
first concrete shell-like tool and follows the execution-worker boundary reserved by ADR 0005 only in the sense that it
is implemented as executable-owned tool state; it does not introduce a worker process.

## Decision

The executable exposes `terminal_input`, `terminal_run`, `terminal_snapshot`, `terminal_paste`, `terminal_list`,
`terminal_close`, and `terminal_interrupt` as ordinary Microsoft.Extensions.AI functions, gated per
run by the hosted `Shell` capability (ADR 0005 capability registry). Omitting `sessionId` selects a reserved lazy
default session that starts on the first write call (`terminal_input` or `terminal_paste`), running the configured
`Maieutics:Terminal:Shell`; reads and control calls never start a session and fail with `terminal_session_not_found`
when none exists. Sessions are scoped to one Agent session, capture the workspace root as their working directory at
creation, and never share one PTY child.

`terminal_run` starts a PTY session running a caller-selected executable with arguments. Without `timeout` it creates a
persistent interactive session and returns its first frame; with a wall-clock deadline it runs the program as a
one-shot command, returning `completed` with the exit code and final frame when the child exits in time, or `running`
with the session as a live task handle when it is still running at the deadline. The handle is polled and operated
through the same `terminal_snapshot`/`terminal_input`/`terminal_interrupt`/`terminal_close` tools; a one-shot session
never occupies the lazy default slot and counts against `MaxSessionsPerAgent`.

The terminal is composed of three deterministic parts: a PTY child (`Ghostflyby.Pty`, NuGet), a headless VT emulator
(`XTerm.NET` `Terminal`), and an exclusive output pump that decodes the PTY byte stream into the emulator. The pump
owns the emulator; the emulator is the only writer of the screen buffer and is not thread-safe.

## Input protocol

`terminal_input` takes one `input` string whose lines are each one of:

```text
t <text>
k <keys>
```

- `t <text>` writes the remainder of the line as raw UTF-8 with no escape processing. A payload must not contain any
  C0 control character other than tab; a newline is the line boundary and never part of a payload. A line that is
  neither `t ` nor `k ` fails the whole batch before any input is sent. Lines are separated by `\n` only; a trailing
  newline at the end of `input` is tolerated, but `\r\n` separators are rejected because `\r` is a control byte.
- `k <keys>` writes a sequence of key tokens, each `[count]<name>` with an optional repeat count from 1 through 10000,
  where `name` is a key from the closed vim-notation table (`<CR>`, `<Esc>`, `<Tab>`, `<S-Tab>`, `<BS>`, `<Del>`,
  `<Space>`, `<Up>`, `<Down>`, `<Left>`, `<Right>`, `<Home>`, `<End>`, `<PageUp>`, `<PageDown>`, `<F1>`-`<F12>`), a
  `C-<letter|[ \ ] ^ _ @]>` C0 control token, or an `M-<letter>`/`A-<letter>` meta token. Unknown tokens fail the whole
  batch. Special keys accept `C-`, `A-`, and `S-` modifiers in any combination.
- The whole input is statically parsed and validated before anything is written, so a syntax error never leaves a
  partially-sent batch. During execution a write failure (child closed) stops at the failing line and reports how many
  lines were sent.

Key sequences are encoded by the emulator's input generator, which tracks terminal mode, so function keys follow
application-cursor-key and keypad modes and `<C-x>` maps to the C0 control byte. A key token's bytes are written
atomically in one write; the ESC prefix of a meta token is never split from its payload so programs with a short
`ttimeoutlen` do not misparse it.

## Output protocol

Each execute, paste, interrupt, and snapshot call returns a screen frame. A frame carries the emulator version, the
matrix dimensions, the cursor, whether the alternate screen is active, and the visible rows. Unless the caller asks
for a full frame, the tool returns only the rows that changed since the last frame the session delivered. Frames are
bounded: the tool enforces a character budget and reports truncation.

After sending input, the tool waits a bounded settle window for the emulator to stop changing before returning the
frame. A child that keeps producing output (for example an interactive full-screen program) returns the current frame
when the window ends and reports that it did not settle. The settle window is a tool parameter, not a hidden constant.

## Termination and failure

- `terminal_interrupt` writes the `\x03` byte through the PTY, which the tty driver turns into SIGINT for the foreground
  process group.
- `terminal_close` and session disposal use the graceful-close ladder: `RequestClose` (SIGHUP / CTRL_CLOSE_EVENT), then
  force kill after a configured grace window, then block until the PTY reaper collects the child.
- The output pump is the child-exit observer: an unexpected end of the PTY stream marks the session faulted; an
  expected close does not. `Exited` on the shared reaper thread only signals the pump watcher and never blocks.
- A faulted session requires an explicit restart, which is not yet exposed; recovery is explicit and no input is ever
  replayed automatically.

## Security

The shell is privileged code execution, not an untrusted-code sandbox: the same position ADR 0011 takes for Deno. The
child starts with an allowlisted environment that excludes model-provider credentials (`InheritParentEnvironment =
false` on the PTY), `TERM=xterm-256color` is set, and the pty environment never inherits the host's secrets. Tool
arguments and model text are untrusted; every C0 control byte outside `t` is rejected before it reaches the child.
Sessions are bounded per Agent session and frames are size-bounded.

## Failure codes

`terminal_*` tools report expected failures as `AgentToolException` with these codes, surfaced to the model as
`{"status":"error","code":"...","message":"..."}`:

| Code | Meaning |
|---|---|
| `terminal_invalid_arguments` | A required argument is missing or a range check fails (for example `maxCharacters` outside 1..1048576). |
| `terminal_invalid_input` | The `input` batch fails static parsing: an unknown line prefix, a forbidden control byte, an unknown key name, or a count outside 1..10000. Nothing was sent. |
| `terminal_invalid_paste` | Pasted text contains an escape byte or a control character other than tab and newlines. |
| `terminal_session_limit` | The Agent session already owns the configured maximum number of terminal sessions. |
| `terminal_session_not_found` | The named session does not exist; reads and control calls never start a session. |
| `terminal_start_failed` | The PTY child could not be started. |
| `terminal_close_failed` | The session could not be closed cleanly. |
| `terminal_faulted` | The session is faulted (child exited unexpectedly) and requires an explicit restart or close. |

## Consequences

- The executable becomes a consumer of two third-party libraries: `Ghostflyby.Pty` (Apache-2.0) and `XTerm.NET`
  (MIT). Both are pure managed code plus P/Invoke on the PTY side; the PTY declares AOT compatibility.
- `terminal_*` tools are hidden unless the selected endpoint resolves the hosted `Shell` capability, so adding the tools
  does not change existing behavior for endpoints that do not claim them.
- The terminal screen is model-visible and never pushed to the notebook; a future presentation path may render a
  screen or diff into the notebook like the Deno REPL presentation router does, but that is separate work.
- The terminal tooling remains executable-owned product code; it does not move into `Maieutics.Agent` or the Jupyter
  libraries.
