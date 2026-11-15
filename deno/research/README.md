# Research: REPL × Extension Host on worker-actor

Scratch verification for migrating the Deno REPL under the plugin extension host via worker-actor's
cross-process APIs. **Temporary, on a research branch — not part of the product module graph** (not
embedded, not wired into the workspace `deno.json`).

## Findings

The original experiments used `@ghostflyby/worker-actor` 0.4.0. The scripts now resolve Actor
imports to the product-owned runtime under `../maieutics-plugin-sdk/actor-runtime/`, imported from
upstream 0.7.0 and maintained with internal protocol 2 (ADRs 0052 and 0053). The table records the
original experiments; rerun the scripts against the local runtime when relying on a result.

| Experiment                                                                             | File                  | Result                                                                  |
| -------------------------------------------------------------------------------------- | --------------------- | ----------------------------------------------------------------------- |
| `spawnProcess`/`serveProcess` cross-process RPC, AsyncIterable, dispose                | `proc_basic_*`        | ✅                                                                      |
| Worker-side dedicated channel on a process actor (openChannel + token over RPC)        | `dedicated_channel_*` | ✅ token handoff works; host needs a transport handle to `connectToken` |
| `spawnNode` multi-actor + **host connects the token** → bidirectional custom frames    | `node_actor_*`        | ✅ `connectToken(node.transport, token)` ↔ echo ack                     |
| **Bidirectional refs** across two independent processes (host↔REPL capability sharing) | `ref_bidi_*`          | ✅ both directions acquire & call                                       |

## Key conclusions

- `serveProcess` has **no `onLink`** (Web-Worker-only MessagePort mechanism).
- The library's public `./codec` surface exposes everything needed to build a process-scenario
  dedicated channel: `openChannel`/`connectToken`/
  `getActiveTransport`/`createMux`/`makeRpcHandler`/`createRpcProxy`.
- `spawnNode` exposes `.transport`, so the host can `connectToken` a channel the node opened.
  `spawnProcess` does **not** expose the transport (small library gap for the host-connects-token
  pattern).
- Remote-refs (capability sharing) work across processes in **both directions** (verified with the
  runtime's `test_fixtures/remote_ref/ref_codec.ts` pattern).

## Notes

- The directory is NOT in `deno/deno.json` workspace; run each `*_main.ts` with
  `--allow-read --allow-run --allow-env`.
- Reference codec copied from worker-actor `examples/remote_ref/ref_codec.ts` for self-containment.
