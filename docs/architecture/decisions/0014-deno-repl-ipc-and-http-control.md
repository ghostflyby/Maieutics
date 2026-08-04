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

- The application is a single `WebApplication`. The main host became the ASP.NET host: the control channel is mapped
  onto the same app through `ReplControlHost.MapEndpoints` (auth middleware, `/health`, `/ws`), and the unix socket
  endpoint is configured in the same `ConfigureKestrel`. There is exactly one host for the whole process; the previous
  Generic Host + nested WebApplication split is removed. The server is stateless: session state stays in
  `ReplControlSessionRegistry` (child process id to session id) and is looked up per request, never baked into the
  server instance.
- The REPL child channel is a single unix domain socket endpoint (`ListenUnixSocket`), pinned to HTTP/1.1 so WebSocket
  upgrades are deterministic. All REPL children share one socket; requests are attributed to a session through the
  peer process identity resolved at accept time (Linux `SO_PEERCRED`; macOS falls back to same-user because no peer
  PID is exposed). Windows is unsupported until a named-pipe bootstrap milestone; the factory throws
  `PlatformNotSupportedException` explicitly instead of degrading silently.
- An external control endpoint is optional and off by default. When enabled it listens on TCP loopback only, with its
  own authentication.
- The child learns only the address from `MAIEUTICS_REPL_IPC`, injected through `kernelSpec.Environment`. No secret is
  passed over any channel on Unix; process identity replaces bearer credentials. The allowlist capture is unchanged;
  injection is explicit per spawn and never inherited.
- No custom raw framing is required. The child uses `Deno.createHttpClient({ proxy: { transport: "unix", path } })`
  for `fetch`. Full duplex uses `new WebSocket(url, { client, headers })`; the `client` option is experimental and must
  be probed against the pinned Deno version before the comm feature depends on it. It is confirmed working on the
  current local Deno.
- The underlying accepted socket is reached through `IConnectionSocketFeature` (net10 does not expose
  `ITransportSocketsFeature`). `DOTNET_USE_POLLING_FILE_WATCHER=1` is set before builder creation because the default
  reload-on-change JSON configuration blocks in constrained sandboxes; this matches the executable's existing polling
  configuration provider.

### Identity and permissions

Connection identity is process-bound. The kernel verifies peer credentials at accept against the exact spawned child
PID. No secret handshake exists on Unix:

- Linux: `SO_PEERCRED` (pid/uid), optionally `/proc/<pid>/exe`.
- macOS: `getpeereid` (uid/gid) only; same-user acceptance is the accepted strength because macOS exposes no peer PID.
- Windows: `WSAIoctl` with `SIO_AF_UNIX_GETPEERPID`; the output byte count is known to be 0 while the PID value is
  reliable. The named-pipe alternative is `GetNamedPipeClientProcessId` and is the planned bootstrap that issues a
  credential for the loopback control channel.

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

The socket directory is hardened: mode 0700, owner check, and unlink before bind. `DenoReplSessionFactory` injects the
single socket path through `kernelSpec.Environment` and starts the manager. `DenoReplSession` registers the spawned
child PID in `ReplControlSessionRegistry`, unregisters it on close, and replaces the mapping when a Jupyter restart
spawns a new child process.

`IJupyterKernelManager` exposes the child `ProcessId` as a read-only nullable property so the product IPC host can
verify peer credentials and detect Jupyter restarts. The reusable Jupyter libraries otherwise remain unchanged.

## Implementation status

- Landed: ASP.NET Core framework reference, single merged `WebApplication` host with the control channel mapped onto
  it (`ReplControlHost.MapEndpoints`: HTTP `/health`, WebSocket `/ws` echo, peer identity middleware),
  `ReplControlSessionRegistry`, peer-credential interop, factory wiring with `MAIEUTICS_REPL_IPC`, session registration
  and rebinding, in-process and real-Deno-child tests, and the `deno/maieutics-repl-client` module scaffold
  (transport bootstrap and health; tool and widget APIs pending).
- Pending: message envelope and API surface (not decided), tool and comm routing, WebSocket usage by the comm feature,
  external loopback control endpoint, and the Windows named-pipe bootstrap.

## Consequences

- HTTP-first removes custom framing work; WebSocket, when opened, contributes binary frames, ping/pong, and the close
  handshake.
- The executable gains an ASP.NET Core minimal API host. This is a real HTTP requirement under `Maieutics/AGENTS.md` and
  requires NativeAOT publish plus process smoke coverage and trimming-safe source-generated JSON contexts.
- Raw-framed duplex becomes the fallback if the pinned Deno version lacks the WebSocket `client` option.
- Windows fails explicitly at REPL start until the named-pipe bootstrap milestone, per the accepted decision.
- The channel does not change Jupyter wire behavior. Extension hooks (ADR 0004) still cannot enqueue tool calls.

## References

- ADR 0003, ADR 0004, ADR 0005, ADR 0011
- Deno: Fetch over a Unix socket (https://docs.deno.com/examples/fetch_unix_socket/)
- Deno PR #30321 and the `WebSocketOptions.client` field (experimental)
- microsoft/WSL#4676: `SIO_AF_UNIX_GETPEERPID` bytes-returned quirk
- ASP.NET Core Kestrel endpoint configuration and WebSockets middleware
