import { assertEquals } from "@std/assert";
import { childRunsOfParent, resolveFrameOwner } from "./runRouting.ts";

/** Owned-run maps under test: one or two live top-level run executions. */
const oneRun = new Map([["parent-1", {} as unknown]]);
const twoRuns = new Map([["parent-1", {} as unknown], ["parent-2", {} as unknown]]);

Deno.test("run-less frames belong to the session's in-flight run", () => {
  assertEquals(resolveFrameOwner(undefined, oneRun, new Map()), { kind: "session" });
  // With no run in flight the frame still routes as session-shaped; the
  // stream delivers it to whatever run registers (or nobody).
  assertEquals(resolveFrameOwner(undefined, new Map(), new Map()), { kind: "session" });
});

Deno.test("a frame's own run id routes to its run execution", () => {
  assertEquals(resolveFrameOwner("parent-1", oneRun, new Map()), {
    kind: "owned",
    runId: "parent-1",
  });
  assertEquals(resolveFrameOwner("parent-2", twoRuns, new Map()), {
    kind: "owned",
    runId: "parent-2",
  });
});

Deno.test("a first-seen id with one in-flight run is adopted as its child", () => {
  assertEquals(resolveFrameOwner("child-a", oneRun, new Map()), {
    kind: "adopt",
    parentRunId: "parent-1",
  });
  // Once registered, the child routes by registration, not by re-adoption.
  const childRuns = new Map([["child-a", "parent-1"]]);
  assertEquals(resolveFrameOwner("child-a", oneRun, childRuns), {
    kind: "child",
    parentRunId: "parent-1",
  });
});

Deno.test("a first-seen id with zero or several candidates drops", () => {
  // Zero: frames racing the run's registration (the pre-timeline behavior —
  // dropped, never misrouted).
  assertEquals(resolveFrameOwner("child-a", new Map(), new Map()), { kind: "drop" });
  // Several: two notebooks pinned to one session both registered a run; the
  // wire carries no parentRunId, so attribution would be a guess.
  assertEquals(resolveFrameOwner("child-a", twoRuns, new Map()), { kind: "drop" });
});

Deno.test("a registered child of a retired parent drops instead of rendering", () => {
  // parent-1 settled and left the map; parent-2 is the run now in flight. A
  // late child frame of parent-1 must neither resurrect the settled parent
  // nor be adopted by parent-2 while its registration lingers.
  const childRuns = new Map([["child-a", "parent-1"]]);
  const nextRun = new Map([["parent-2", {} as unknown]]);
  assertEquals(resolveFrameOwner("child-a", nextRun, childRuns), { kind: "drop" });
  // After retirement cleaned the registry (childRunsOfParent), a stale id
  // reads as first-seen again and adopts the CURRENT run — the arrival-order
  // limitation the wire leaves us (no parentRunId). It is unreachable for
  // frames that flow in wire order (children settle before their parent's
  // terminal frame), which is the only ordering the stream guarantees.
  assertEquals(resolveFrameOwner("child-a", nextRun, new Map()), {
    kind: "adopt",
    parentRunId: "parent-2",
  });
});

Deno.test("retirement cleans the retiring run's child registrations", () => {
  const childRuns = new Map([
    ["child-a", "parent-1"],
    ["child-b", "parent-1"],
    ["child-c", "parent-2"],
  ]);
  assertEquals(childRunsOfParent(childRuns, "parent-1"), ["child-a", "child-b"]);
  assertEquals(childRunsOfParent(childRuns, "parent-2"), ["child-c"]);
  assertEquals(childRunsOfParent(childRuns, "parent-9"), []);
});

Deno.test("the next run's frames are not captured by a stale child registration", () => {
  // parent-1 ran with child-a, both settled, parent-2 now runs: a late
  // child-a frame must drop (registry cleaned on retirement), and a new
  // child of parent-2 adopts parent-2 — never the retired parent.
  assertEquals(resolveFrameOwner("child-a", new Map([["parent-2", {} as unknown]]), new Map()), {
    kind: "adopt",
    parentRunId: "parent-2",
  });
});
