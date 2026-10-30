import { assertEquals } from "@std/assert";
import { SessionTitles } from "./sessionTitles.ts";

Deno.test("title replay cannot replace newer metadata or a manual clear", () => {
  const cache = new SessionTitles();
  const session = {
    id: "a",
    turns: 1,
    persistenceEnabled: true,
    title: "Original",
    titleRevision: 1,
  };
  assertEquals(cache.apply(session), true);
  assertEquals(cache.apply({ ...session, title: "Renamed", titleRevision: 2 }), true);
  assertEquals(cache.apply(session), false);
  assertEquals(cache.get("a")?.title, "Renamed");
  assertEquals(cache.apply({ ...session, title: undefined, titleRevision: 3 }), true);
  assertEquals(cache.apply({ ...session, title: "Renamed", titleRevision: 2 }), false);
  assertEquals(cache.get("a")?.title, undefined);
});

Deno.test("metadata is isolated by session and accepts older servers without revisions", () => {
  const cache = new SessionTitles();
  assertEquals(cache.apply({ id: "a", turns: 1, persistenceEnabled: false, title: "First" }), true);
  assertEquals(
    cache.apply({ id: "b", turns: 1, persistenceEnabled: false, title: "Second" }),
    true,
  );
  assertEquals(cache.get("a")?.title, "First");
  assertEquals(cache.get("b")?.title, "Second");
  assertEquals(
    cache.apply({ id: "a", turns: 1, persistenceEnabled: false, title: "First" }),
    false,
  );
});

Deno.test("mode and lock changes refresh display even when the title text is unchanged", () => {
  const cache = new SessionTitles();
  const session = { id: "a", turns: 1, persistenceEnabled: false, title: "Task", titleRevision: 1 };
  cache.apply(session);
  assertEquals(
    cache.apply({ ...session, titleRevision: 2, titleMode: "manual", titleLocked: true }),
    true,
  );
  assertEquals(cache.apply(session), false);
  assertEquals(cache.get("a")?.titleMode, "manual");
  assertEquals(cache.get("a")?.titleLocked, true);
});
