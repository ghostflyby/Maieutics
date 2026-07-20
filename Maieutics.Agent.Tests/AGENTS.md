# Maieutics.Agent.Tests instructions

Use `.agents/skills/maieutics-dotnet-testing/SKILL.md` for shared xUnit, assertion, deadline, and verification rules.

## Ownership

This project owns deterministic unit tests for `Maieutics.Agent`. It does not own Jupyter adapter, socket, executable,
or real-provider integration tests.

## Local coverage

- Use deterministic fake `IChatClient` implementations and fake tools. This project must not access external model
  services, Jupyter sockets, or the network.
- Cover IDs, event sequence, single-consumer bounded streams, backpressure cancellation, one-run enforcement, repeated
  cancellation/disposal, and release of the session before completion.
- Cover run-local profiles, capability checks, model identity attribution, transcript versions, cross-provider replay,
  and complete-turn history eviction.
- Cover Framework staging and atomic commit: early stream disposal, cancellation, empty or unsupported responses,
  provider failures, output limits, conversation-ID conflicts, and framework upgrade characterization.
- Cover tool event order, serial invocation, complete intermediate transcript messages, recoverable failures, malformed
  calls, unexpected exceptions, all configured budgets, and rollback.
- Assert that a failed or partially consumed run leaves committed transcript state unchanged and that the session can be
  used again.
