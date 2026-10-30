/// <reference lib="deno.ns" />
import { assertEquals, assertRejects, assertThrows } from "@std/assert";
import {
  dirtySaveTargets,
  type SaveableDocument,
  saveWorkspaceDocuments,
} from "./documentSaving.ts";

class Document implements SaveableDocument {
  version = 1;
  isDirty = true;
  isClosed = false;
  saves = 0;
  constructor(readonly uri: string, readonly kind: "text" | "notebook" = "text") {}
  action: () => Promise<boolean> = () => {
    this.isDirty = false;
    return Promise.resolve(true);
  };
  save(): Promise<boolean> {
    this.saves++;
    return this.action();
  }
}

Deno.test("save barrier saves every text/notebook target and accepts normal save-participant edits", async () => {
  const a = new Document("file:///one/a.ts");
  const b = new Document("file:///two/a.maieuticsnb", "notebook");
  a.action = () => {
    a.version++;
    a.isDirty = false;
    return Promise.resolve(true);
  };
  const all = [a, b];
  assertEquals(await saveWorkspaceDocuments(dirtySaveTargets(all), () => all), {
    status: "saved",
    saved: [a.uri, b.uri],
  });
  assertEquals(all.map((document) => document.saves), [1, 1]);
});

Deno.test("save barrier rejects any stale target or newly dirty document before saving anything", async () => {
  const a = new Document("a");
  const b = new Document("b");
  const expected = dirtySaveTargets([a]);
  const answer = await saveWorkspaceDocuments(expected, () => [a, b]);
  assertEquals(answer.status, "failed");
  assertEquals(answer.reason, "conflict");
  assertEquals(a.saves + b.saves, 0);
  a.version++;
  assertEquals((await saveWorkspaceDocuments(expected, () => [a])).reason, "conflict");
  assertEquals(a.saves, 0);
});

Deno.test("failed save reports partial persistence and never attempts pending targets", async () => {
  const a = new Document("a"), b = new Document("b"), c = new Document("c");
  b.action = () => Promise.resolve(false);
  const all = [a, b, c];
  assertEquals(await saveWorkspaceDocuments(dirtySaveTargets(all), () => all), {
    status: "failed",
    saved: [a.uri],
    failedUri: b.uri,
    reason: "save_failed",
    pending: [b.uri, c.uri],
  });
  assertEquals(all.map((document) => document.saves), [1, 1, 0]);
});

Deno.test("save participant changing a later buffer cannot silently authorize a stale build", async () => {
  const a = new Document("a"), b = new Document("b");
  a.action = () => {
    a.isDirty = false;
    b.version++;
    return Promise.resolve(true);
  };
  const all = [a, b];
  const answer = await saveWorkspaceDocuments(dirtySaveTargets(all), () => all);
  assertEquals(answer.reason, "conflict");
  assertEquals(answer.saved, [a.uri]);
  assertEquals(b.saves, 0);
});

Deno.test("a successful save followed by a user edit is counted but cannot pass the barrier", async () => {
  const document = new Document("a");
  document.action = () => {
    document.version++;
    return Promise.resolve(true);
  };
  assertEquals(await saveWorkspaceDocuments(dirtySaveTargets([document]), () => [document]), {
    status: "failed",
    saved: [document.uri],
    failedUri: document.uri,
    reason: "conflict",
    pending: [document.uri],
  });
});

Deno.test("redirtied documents fail the final save barrier", async () => {
  const a = new Document("a"), b = new Document("b");
  b.action = () => {
    b.isDirty = false;
    a.isDirty = true;
    return Promise.resolve(true);
  };
  const all = [a, b];
  assertEquals((await saveWorkspaceDocuments(dirtySaveTargets(all), () => all)).reason, "conflict");
});

Deno.test("a newly dirty file during saving requires another permission preflight", async () => {
  const a = new Document("a"), b = new Document("b");
  b.isDirty = false;
  a.action = () => {
    a.isDirty = false;
    b.isDirty = true;
    return Promise.resolve(true);
  };
  const all = [a, b];
  assertEquals(await saveWorkspaceDocuments(dirtySaveTargets(all), () => all), {
    status: "failed",
    saved: [a.uri],
    failedUri: b.uri,
    reason: "conflict",
    pending: [b.uri],
  });
  assertEquals(b.saves, 0);
});

Deno.test("save cancellation stops before any later document is saved", async () => {
  const a = new Document("a"), b = new Document("b");
  const lifetime = new AbortController();
  a.action = () => {
    a.isDirty = false;
    lifetime.abort(new Error("cancelled"));
    return Promise.resolve(true);
  };
  const all = [a, b];
  await assertRejects(
    () => saveWorkspaceDocuments(dirtySaveTargets(all), () => all, lifetime.signal),
    Error,
    "cancelled",
  );
  assertEquals(b.saves, 0);
});

Deno.test("save enumeration never silently drops dirty documents beyond the bound", () => {
  assertThrows(
    () => dirtySaveTargets(Array.from({ length: 257 }, (_, i) => new Document(String(i)))),
    Error,
    "256",
  );
});
