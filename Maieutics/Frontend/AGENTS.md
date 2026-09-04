# AGENTS.md — Maieutics.Frontend

## Ownership

This domain is the executable's only frontend surface: the discovery file, the bearer-auth middleware for the
frontend paths, the versioned REST endpoints, and the per-session event WebSocket. It is the consumer of
`Maieutics.Commands` (command language, execution, status) and `Maieutics.Agent` (sessions, runs, transcript).

## Constraints

- Every frontend request is authenticated by the per-process bearer token compared in constant time; the events
  WebSocket may take the token as a query parameter because the browser-standard WebSocket API cannot set headers.
- Events are half-duplex (executable to frontend). The run stream never drops frames: backpressure closes the
  socket, and the client resumes with `sinceSequence` from its retained buffer.
- Turns are served only for the active session; concurrent turns fail with the typed `agent_busy` code. The
  protocol does not queue turns (invariant 4).
- The wire is provider-neutral: no Jupyter type and no Microsoft.Extensions.AI type crosses it. Convert in
  `FrontendTranscriptMapper` / the presentation sink.
- Wire shapes live in `FrontendWireModels.cs` with source-generated JSON only (NativeAOT).

## Verification

- `dotnet test` — `FrontendApiIntegrationTests` boots the composition root with the frontend enabled and drives
  turns, replay, cancel, commands, and auth; `FrontendDenoReplPresentationTests` covers the presentation sink.
