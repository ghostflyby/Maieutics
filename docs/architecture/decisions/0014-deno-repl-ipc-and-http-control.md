# ADR 0014: Deno REPL Sideband IPC and HTTP Control Channel

Status: Draft

Date: 2026-08-03

## Context

The primary TypeScript REPL is a real `deno jupyter` child process (ADR 0003, ADR 0011). It currently has no channel
for script-facing host APIs: a script calling Maieutics tools, and the comm/widget event bridge identified as the
missing piece for ipywidgets-compatible interaction.

Stock Deno can broadcast widget-facing IOPub messages but cannot deliver frontend comm ingress into script code. Full
interaction needs kernel-side comm ownership plus an event path back to the REPL. ADR 0004 reserves a dedicated
versioned extension protocol over an owned IPC connection, with the first local transport deferred (child stdio; unix
sockets or named pipes may be added behind the same protocol).

The REPL child is spawned with `ClearInheritedEnvironment` plus an allowlisted environment capture.
`LocalJupyterKernelManager` already applies `kernelSpec.Environment` after that capture, so per-spawn channel variables
can be injected without weakening the allowlist. Design review compared localhost TCP HTTP/WS, unix-socket HTTP/WS,
raw framed duplex, fd passing, and whether an external HTTP control surface is a real product requirement.

## Decision

Adopt an HTTP-first sideband control channel hosted by the Maieutics executable, with a transport-neutral message
envelope, process-bound identity, and per-generation lifecycle.

### Transport and hosting

- The executable hosts one ASP.NET Core minimal API server on Kestrel.
- The REPL child channel is a unix domain socket endpoint (`ListenUnixSocket`), pinned to HTTP/1.1 so WebSocket
  upgrades are deterministic. Windows uses a named pipe endpoint (`ListenNamedPipe`) because .NET AF_UNIX sockets on
  Windows are unreliable.
- An external control endpoint is optional and off by default. When enabled it listens on TCP loopback only, with its
  own authentication.
- The child learns the address and a per-spawn secret from environment variables injected through `kernelSpec.Environment`
  (for example `MAIEUTICS_REPL_IPC` and `MAIEUTICS_REPL_IPC_SECRET`). The allowlist capture is unchanged; injection is
  explicit per spawn and never inherited.
- No custom raw framing is required. The child uses `Deno.createHttpClient({ proxy: { transport: "unix", path } })`
  for `fetch`. Full duplex uses `new WebSocket(url, { client, headers })`; the `client` option is experimental and must
  be probed against the pinned Deno version before the comm feature depends on it.

### Identity and permissions

Connection identity is process-bound. The kernel verifies peer credentials at accept against the exact spawned child
PID, then requires the per-spawn secret in the handshake:

- Linux: `SO_PEERCRED` (pid/uid), optionally `/proc/<pid>/exe`.
- macOS: `getpeereid` (uid/gid) plus the secret.
- Windows: `WSAIoctl` with `SIO_AF_UNIX_GETPEERPID`; the output byte count is known to be 0 while the PID value is
  reliable. The named-pipe alternative is `GetNamedPipeClientProcessId`.

The external endpoint uses real authentication (API key or mTLS), rate limiting, and audit. Mutating operations are
disabled by default there; health, status, and event streaming may be enabled read-only.

The transport grants no extra capability to the REPL. Script-invoked tools execute through the same runtime and policy
boundary as model tool calls: schema validation, workspace/network/process/environment policy, approval, and structured
results. Recursive re-entry is rejected: a script cannot invoke the `repl_*` family or agent turn APIs over the
channel, because doing so would deadlock its own generation.

### Message envelope and channel mapping

The envelope is versioned and transport-neutral: initialization and capability negotiation, request/response with
correlation ids, one-way events, binary buffers, cancellation, typed failure, and unknown-field tolerance. Namespaces
are `tool.*`, `comm.*`, `extension.*`, and `admin.*`.

Channel mapping:

| Channel | Use |
|---|---|
| HTTP | Tool calls, REPL and session management, health, status, configuration, input prompts (pending request), artifact transfer (streaming responses) |
| SSE | One-way kernel-to-consumer event streams (turn events, tool activity) with `Last-Event-ID` reconnect semantics |
| WebSocket | Comm/widget bidirectional messages with binary buffers, and kernel-to-script control pushes while a script awaits |

The WebSocket endpoint is opened when the comm feature is implemented, not before.

### Lifecycle

One listener, socket, and secret per REPL generation, created with the generation and torn down with it (same owner as
the generation loop and connection-file cleanup). The socket directory is hardened: mode 0700, owner check, and unlink
plus `lstat` before bind.

`IJupyterKernelManager` exposes the child `ProcessId` as a read-only property so the product IPC host can verify peer
credentials. The reusable Jupyter libraries otherwise remain unchanged.

## Consequences

- HTTP-first removes custom framing work; WebSocket, when opened, contributes binary frames, ping/pong, and the close
  handshake.
- The executable gains an ASP.NET Core minimal API host. This is a real HTTP requirement under `Maieutics/AGENTS.md` and
  requires NativeAOT publish plus process smoke coverage and trimming-safe source-generated JSON contexts.
- Raw-framed duplex becomes the fallback if the pinned Deno version lacks the WebSocket `client` option or the Windows
  unix transport.
- Windows behavior remains pending local verification: Deno unix transport availability, and the named-pipe versus
  AF_UNIX choice on the kernel side.
- The channel does not change Jupyter wire behavior. Extension hooks (ADR 0004) still cannot enqueue tool calls.

## References

- ADR 0003, ADR 0004, ADR 0005, ADR 0011
- Deno: Fetch over a Unix socket (https://docs.deno.com/examples/fetch_unix_socket/)
- Deno PR #30321 and the `WebSocketOptions.client` field (experimental)
- microsoft/WSL#4676: `SIO_AF_UNIX_GETPEERPID` bytes-returned quirk
- ASP.NET Core Kestrel endpoint configuration and WebSockets middleware
