/// <reference lib="deno.window" />

import { assert, assertEquals } from "@std/assert";
import { resolveSessionPin } from "./sessionPin.ts";
import { ProtocolVersion } from "./protocol.ts";
import { FrontendClient } from "./client.ts";

function startMockSessionServer(): Promise<{
  url: string;
  shutdown(): Promise<void>;
  active: { id: string };
  setResumeResult(kind: "ok" | "not_found" | "persistence_off"): void;
}> {
  const active = { id: "a".repeat(32) };
  let resumeResult: "ok" | "not_found" | "persistence_off" = "ok";
  const server = Deno.serve({ port: 0, hostname: "127.0.0.1" }, (request) => {
    const url = new URL(request.url);
    if (request.headers.get("Authorization") !== "Bearer test-token") {
      return new Response("{}", { status: 401 });
    }

    if (url.pathname === "/v1/agent/session") {
      return Response.json({ id: active.id, turns: 0, persistenceEnabled: true });
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
    setResumeResult: (kind) => (resumeResult = kind),
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
