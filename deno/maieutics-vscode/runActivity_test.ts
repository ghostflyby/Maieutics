/// <reference lib="deno.ns" />
import { assertEquals } from "@std/assert";
import { SessionActivity } from "./runActivity.ts";

Deno.test("activity marks busy and idle with change events", () => {
  const activity = new SessionActivity();
  const events: number[] = [];
  const subscription = activity.onDidChange(() => events.push(events.length));

  assertEquals(activity.markBusy("a".repeat(32)), true);
  assertEquals(activity.isBusy("a".repeat(32)), true);
  assertEquals(activity.markBusy("a".repeat(32)), false, "redundant busy fires nothing");
  assertEquals(activity.busySessions(), ["a".repeat(32)]);

  assertEquals(activity.markIdle("a".repeat(32)), true);
  assertEquals(activity.busySessions(), []);
  assertEquals(activity.markIdle("a".repeat(32)), false, "idle when idle changes nothing");

  subscription.dispose();
  const before = events.length;
  activity.markBusy("b".repeat(32));
  assertEquals(events.length, before, "disposed listeners no longer fire");
});

Deno.test("busySessions is a sorted stable snapshot", () => {
  const activity = new SessionActivity();
  activity.markBusy("c".repeat(32));
  activity.markBusy("a".repeat(32));
  activity.markBusy("b".repeat(32));
  const first = activity.busySessions();
  assertEquals(first, ["a".repeat(32), "b".repeat(32), "c".repeat(32)]);
  assertEquals(activity.busySessions(), first, "snapshots are copies");
  activity.markBusy("a".repeat(32));
  assertEquals(activity.busySessions().length, 3);
});
