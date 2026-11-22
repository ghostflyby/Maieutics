/// <reference lib="deno.ns" />
import { assertEquals, assertRejects } from "@std/assert";
import { ViewRequests, type ViewRequestScheduler } from "./webview/requests.ts";
import type { ViewAddress, ViewRequest } from "./conversationProtocol.ts";

const address: ViewAddress = {
  version: 1,
  sessionId: "session",
  viewId: "view",
  generation: "document",
};
function fixture() {
  let now = 0;
  const timers = new Set<{ deadline: number; expire: () => void }>();
  const schedule: ViewRequestScheduler = (expire, milliseconds) => {
    const timer = { deadline: now + milliseconds, expire };
    timers.add(timer);
    return () => {
      timers.delete(timer);
    };
  };
  const posted: ViewRequest[] = [];
  const owner = new AbortController();
  const requests = new ViewRequests(
    address,
    (message) => posted.push(message),
    owner.signal,
    schedule,
  );
  return {
    requests,
    posted,
    owner,
    timers,
    advance(milliseconds: number) {
      now += milliseconds;
      for (const timer of [...timers]) {
        if (timer.deadline <= now) {
          timers.delete(timer);
          timer.expire();
        }
      }
    },
  };
}

Deno.test("ready survives slow kernel startup beyond the ordinary request deadline", async () => {
  const f = fixture();
  const ready = f.requests.request("ready");
  try {
    f.advance(60_000);
    assertEquals(
      f.posted.map((message) => message.method),
      ["ready"],
      "The view must not abort extension-owned startup",
    );
    f.requests.receive({ ...address, id: f.posted[0].id, result: "connected" });
    assertEquals(await ready, "connected");
    assertEquals(f.timers.size, 0);
  } finally {
    f.owner.abort();
    await Promise.allSettled([ready]);
  }
});

Deno.test("page and uncertain submission requests retain bounded deadlines without replay", async () => {
  for (const method of ["page", "submit"] as const) {
    const f = fixture();
    try {
      const request = f.requests.request(method);
      const failure = assertRejects(
        () => request,
        Error,
        method === "submit" ? "无法确认提交结果" : "请求超时",
      );
      f.advance(14_999);
      assertEquals(f.posted.map((message) => message.method), [method]);
      f.advance(1);
      await failure;
      assertEquals(f.posted.map((message) => message.method), [method, "abort"]);
      assertEquals(f.posted[1].params?.requestId, f.posted[0].id);
      assertEquals(f.timers.size, 0);
    } finally {
      f.owner.abort();
    }
  }
});

Deno.test("owner and caller cancellation release ready while its startup is still pending", async () => {
  for (const cancelOwner of [true, false]) {
    const f = fixture();
    const caller = new AbortController();
    const ready = f.requests.request("ready", undefined, caller.signal);
    const cancelled = assertRejects(() => ready, DOMException);
    (cancelOwner ? f.owner : caller).abort();
    await cancelled;
    assertEquals(f.posted.map((message) => message.method), ["ready", "abort"]);
    assertEquals(f.timers.size, 0);
    f.owner.abort();
  }
});

Deno.test("a matching reply cancels its timer and a retired document cannot settle it", async () => {
  const f = fixture();
  try {
    const page = f.requests.request("page");
    f.requests.receive({ ...address, generation: "old", id: f.posted[0].id, result: "stale" });
    f.requests.receive({ ...address, id: f.posted[0].id, result: "current" });
    assertEquals(await page, "current");
    f.advance(20_000);
    assertEquals(f.posted.map((message) => message.method), ["page"]);
    assertEquals(f.timers.size, 0);
  } finally {
    f.owner.abort();
  }
});

Deno.test("ready remains capacity bounded and a closed document admits no new requests", async () => {
  const f = fixture();
  const pending = Array.from({ length: 64 }, () => f.requests.request("ready"));
  const outcomes = Promise.allSettled(pending);
  await assertRejects(() => f.requests.request("ready"), Error, "Too many pending");
  f.owner.abort();
  await outcomes;
  const count = f.posted.length;
  await assertRejects(() => f.requests.request("ready"), DOMException);
  assertEquals(f.posted.length, count);
  assertEquals(f.timers.size, 0);
});
