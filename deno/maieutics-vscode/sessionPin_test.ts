/// <reference lib="deno.window" />

import { assert, assertEquals, assertRejects } from "@std/assert";
import { resolveSessionPin } from "./sessionPin.ts";
import { ProtocolVersion } from "./protocol.ts";
import { FrontendClient } from "./client.ts";

function startMockSessionServer(
  multiSession = false,
  persistenceEnabled = true,
): Promise<{
  url: string;
  shutdown(): Promise<void>;
  active: { id: string };
  created: string[];
  setResumeResult(kind: "ok" | "not_found" | "persistence_off"): void;
  setTranscriptFailure(status: number): void;
}> {
  const active = { id: "a".repeat(32) };
  let resumeResult: "ok" | "not_found" | "persistence_off" = "ok";
  const created: string[] = [];
  let transcriptFailure: number | undefined;
  const server = Deno.serve({ port: 0, hostname: "127.0.0.1" }, (request) => {
    const url = new URL(request.url);
    if (request.headers.get("Authorization") !== "Bearer test-token") {
      return new Response("{}", { status: 401 });
    }

    if (url.pathname === "/v1/agent/capabilities") {
      return Response.json({
        protocolVersion: ProtocolVersion,
        serverVersion: "0.0.0-test",
        session: { id: active.id, turns: 0, persistenceEnabled },
        ...(multiSession ? { multiSession: true } : {}),
      });
    }

    if (url.pathname === "/v1/agent/sessions" && request.method === "POST") {
      const id = `c${created.length.toString(16)}`.padEnd(32, "0");
      created.push(id);
      active.id = id;
      return Response.json({ id, turns: 0, persistenceEnabled });
    }

    if (url.pathname === "/v1/agent/sessions" && request.method === "GET") {
      return Response.json(
        [{ id: "d".repeat(32), turns: 2, createdAt: "", lastActivityAt: "" }],
      );
    }

    if (url.pathname === "/v1/agent/session") {
      return Response.json({ id: active.id, turns: 0, persistenceEnabled });
    }

    if (url.pathname.match(/^\/v1\/agent\/sessions\/[^/]+\/transcript$/)) {
      if (transcriptFailure !== undefined) {
        return Response.json({ code: "unavailable", message: "Temporary failure" }, {
          status: transcriptFailure,
        });
      }
      const id = url.pathname.split("/")[4];
      if (id === active.id || created.includes(id) || id === "d".repeat(32)) {
        return Response.json({
          sessionId: id,
          version: 0,
          turns: id === "d".repeat(32)
            ? [{ runId: "r1", messages: [] }, { runId: "r2", messages: [] }]
            : [],
        });
      }
      return Response.json({ code: "not_found" }, { status: 404 });
    }

    if (url.pathname.match(/^\/v1\/agent\/sessions\/[^/]+\/resume$/)) {
      if (resumeResult === "not_found") {
        return Response.json({ code: "not_found" }, { status: 404 });
      }
      if (resumeResult === "persistence_off") {
        return Response.json({ code: "invalid_request" }, { status: 400 });
      }

      return Response.json({ id: url.pathname.split("/")[4], turns: 3, persistenceEnabled: true });
    }

    return Response.json({ code: "not_found" }, { status: 404 });
  });

  const url = `http://127.0.0.1:${server.addr.port}`;
  return Promise.resolve({
    url,
    active,
    created,
    setResumeResult: (kind) => (resumeResult = kind),
    setTranscriptFailure: (status) => (transcriptFailure = status),
    shutdown: () => server.shutdown(),
  });
}

function clientAt(url: string): FrontendClient {
  return FrontendClient.fromDiscovery({
    version: ProtocolVersion,
    url,
    token: "test-token",
    pid: 1,
  });
}

Deno.test("no stored id pins the active session", async () => {
  const { url, shutdown } = await startMockSessionServer();
  const client = await clientAt(url);
  try {
    const decision = await resolveSessionPin(undefined, client);
    assertEquals(decision.kind, "pin");
    assertEquals(decision.pinId, "a".repeat(32));
    assertEquals(decision.warning, undefined);
  } finally {
    await shutdown();
  }
});

Deno.test("matching stored id is a no-op", async () => {
  const { url, active, shutdown } = await startMockSessionServer();
  const client = await clientAt(url);
  try {
    const decision = await resolveSessionPin(active.id, client);
    assertEquals(decision.kind, "ok");
    assertEquals(decision.pinId, undefined);
  } finally {
    await shutdown();
  }
});

Deno.test("differing stored id resumes it", async () => {
  const { url, shutdown } = await startMockSessionServer();
  const client = await clientAt(url);
  try {
    const stored = "b".repeat(32);
    const decision = await resolveSessionPin(stored, client);
    assertEquals(decision.kind, "resume");
    assertEquals(decision.session.id, stored);
  } finally {
    await shutdown();
  }
});

Deno.test("gone stored session pins active with a warning", async () => {
  const { url, active, setResumeResult, shutdown } = await startMockSessionServer();
  const client = await clientAt(url);
  try {
    setResumeResult("not_found");
    const decision = await resolveSessionPin("b".repeat(32), client);
    assertEquals(decision.kind, "pin");
    assertEquals(decision.pinId, active.id);
    assert(decision.warning !== undefined);
    assert(decision.warning.includes("could not be resumed"));
  } finally {
    await shutdown();
  }
});

Deno.test("persistence-off resume pins active with a warning", async () => {
  const { url, active, setResumeResult, shutdown } = await startMockSessionServer();
  const client = await clientAt(url);
  try {
    setResumeResult("persistence_off");
    const decision = await resolveSessionPin("b".repeat(32), client);
    assertEquals(decision.kind, "pin");
    assertEquals(decision.pinId, active.id);
    assert(decision.warning !== undefined);
    assert(decision.warning.includes("could not be resumed"));
  } finally {
    await shutdown();
  }
});

Deno.test("multi-session: an unpinned notebook creates its own session", async () => {
  const { url, created, shutdown } = await startMockSessionServer(true);
  const client = await clientAt(url);
  try {
    const decision = await resolveSessionPin(undefined, client);
    assertEquals(decision.kind, "pin");
    assertEquals(decision.pinId, decision.session.id);
    assertEquals(created.length, 1);
  } finally {
    await shutdown();
  }
});

Deno.test("multi-session: a pinned known session is targeted without a resume", async () => {
  const { url, shutdown } = await startMockSessionServer(true);
  const client = await clientAt(url);
  try {
    const decision = await resolveSessionPin("d".repeat(32), client);
    assertEquals(decision.kind, "ok");
    assertEquals(decision.session.id, "d".repeat(32));
    assertEquals(decision.pinId, undefined);
  } finally {
    await shutdown();
  }
});

Deno.test("multi-session: an unknown pin continues with a fresh session and warns", async () => {
  const { url, created, shutdown } = await startMockSessionServer(true);
  const client = await clientAt(url);
  try {
    const decision = await resolveSessionPin("b".repeat(32), client);
    assertEquals(decision.kind, "pin");
    assertEquals(decision.pinId, decision.session.id);
    assertEquals(decision.warning !== undefined, true);
    assertEquals(created.length, 1);
  } finally {
    await shutdown();
  }
});

Deno.test("multi-session with persistence off replaces only a missing pin", async () => {
  const { url, active, created, shutdown } = await startMockSessionServer(true, false);
  const client = await clientAt(url);
  try {
    const decision = await resolveSessionPin("b".repeat(32), client);
    assertEquals(decision.kind, "pin");
    assertEquals(decision.session.id, active.id);
    assertEquals(decision.pinId, active.id);
    assertEquals(decision.warning !== undefined, true);
    assertEquals(created.length, 1);
  } finally {
    await shutdown();
  }
});

Deno.test("multi-session: a newly created zero-turn session is executable before being listed", async () => {
  const { url, created, shutdown } = await startMockSessionServer(true);
  const client = clientAt(url);
  try {
    const session = await client.newSession();
    assertEquals(
      await client.listSessions().then((sessions) =>
        sessions.some((entry) => entry.id === session.id)
      ),
      false,
    );
    const decision = await resolveSessionPin(session.id, client);
    assertEquals(decision.session.id, session.id);
    assertEquals(decision.kind, "ok");
    assertEquals(decision.warning, undefined);
    assertEquals(created.length, 1, "Execution must not replace a live zero-turn session");
  } finally {
    await shutdown();
  }
});

for (const persistence of [true, false]) {
  Deno.test(`multi-session: another foreground does not invalidate a new live pin (persistence=${persistence})`, async () => {
    const { url, created, shutdown } = await startMockSessionServer(true, persistence);
    const client = clientAt(url);
    try {
      const first = await client.newSession();
      await client.newSession();
      const decision = await resolveSessionPin(first.id, client);
      assertEquals(decision.session.id, first.id);
      assertEquals(decision.warning, undefined);
      assertEquals(
        created.length,
        2,
        "A live pin must not be replaced by the foreground or a third session",
      );
    } finally {
      await shutdown();
    }
  });
}

Deno.test("multi-session: a transient lookup failure cannot replace the pinned session", async () => {
  const { url, created, shutdown, setTranscriptFailure } = await startMockSessionServer(true);
  const client = clientAt(url);
  try {
    const first = await client.newSession();
    await client.newSession();
    setTranscriptFailure(503);
    await assertRejects(() => resolveSessionPin(first.id, client));
    assertEquals(
      created.length,
      2,
      "Only an authoritative not_found permits creating a replacement",
    );
  } finally {
    await shutdown();
  }
});
