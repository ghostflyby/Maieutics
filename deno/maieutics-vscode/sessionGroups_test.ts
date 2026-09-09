import { assert, assertEquals } from "@std/assert";
import {
  groupSessions,
  matchesWorkspace,
  orderWithLineage,
  sessionLabel,
  type SessionLike,
  workspaceGroupLabel,
} from "./sessionGroups.ts";

function session(overrides: Partial<SessionLike> = {}): SessionLike {
  return {
    id: "a1b2c3d4e5f6478890a1b2c3d4e5f6a7",
    turns: 3,
    lastActivityAt: "2026-09-08T10:00:00Z",
    ...overrides,
  };
}

Deno.test("sessionLabel falls back title, preview, then id prefix", () => {
  assertEquals(sessionLabel(session({ title: "Refactor vfs" })), "Refactor vfs");
  assertEquals(sessionLabel(session({ preview: "First question" })), "First question");
  assertEquals(sessionLabel(session()), "a1b2c3d4e5f6");
  const long = "x".repeat(80);
  assertEquals(
    sessionLabel(session({ preview: long })),
    `${"x".repeat(60)}…`,
  );
  // A title wins even when empty-ish preview exists; empty title falls through.
  assertEquals(sessionLabel(session({ title: "", preview: "preview text" })), "preview text");
  // Titles are stored verbatim and may carry newlines; labels stay single-line.
  assertEquals(sessionLabel(session({ title: "Fix the\nkernel  bug" })), "Fix the kernel bug");
  assertEquals(sessionLabel(session({ title: "  \n " })), "a1b2c3d4e5f6");
});

Deno.test("workspaceGroupLabel reduces a root to its basename", () => {
  assertEquals(workspaceGroupLabel("/Users/dev/repos/alpha"), "alpha");
  assertEquals(workspaceGroupLabel("C:\\dev\\repos\\beta\\"), "beta");
  assertEquals(workspaceGroupLabel("/"), "/");
});

Deno.test("matchesWorkspace normalizes separators and case", () => {
  assert(matchesWorkspace("/repos/alpha/", "/repos/alpha", false));
  assert(matchesWorkspace("/Repos/Alpha", "/repos/alpha", true));
  assert(!matchesWorkspace("/Repos/Alpha", "/repos/alpha", false));
  assert(!matchesWorkspace(undefined, "/repos/alpha", false));
});

Deno.test("groupSessions orders current first, then others by recency, anonymous last", () => {
  const sessions: SessionLike[] = [
    session({ id: "1", workspaceRoot: "/repos/old", lastActivityAt: "2026-01-01T00:00:00Z" }),
    session({ id: "2", workspaceRoot: "/repos/current", lastActivityAt: "2026-09-08T09:00:00Z" }),
    session({ id: "3", lastActivityAt: "2026-05-01T00:00:00Z" }),
    session({ id: "4", workspaceRoot: "/repos/current", lastActivityAt: "2026-09-08T10:00:00Z" }),
    session({ id: "5", workspaceRoot: "/repos/other", lastActivityAt: "2026-06-01T00:00:00Z" }),
  ];

  const groups = groupSessions(sessions, "/repos/current/", false);

  assertEquals(groups.map((group) => group.label), [
    "current",
    "other",
    "old",
    "Without workspace",
  ]);
  assert(groups[0].current);
  assertEquals(
    groups[0].sessions.map((entry) => entry.id),
    ["4", "2"],
  );
});

Deno.test("groupSessions currentOnly keeps only the current group", () => {
  const sessions: SessionLike[] = [
    session({ id: "1", workspaceRoot: "/repos/current" }),
    session({ id: "2", workspaceRoot: "/repos/other" }),
    session({ id: "3" }),
  ];

  const groups = groupSessions(sessions, "/repos/current", false, true);
  assertEquals(groups.length, 1);
  assertEquals(groups[0].sessions.map((entry) => entry.id), ["1"]);

  // No match at all → empty list rather than an unfiltered fallback.
  const none = groupSessions(sessions, "/repos/missing", false, true);
  assertEquals(none, []);
});

Deno.test("groupSessions renders fork branches under their root", () => {
  const root = session({
    id: "root00000000000000000000000001",
    lastActivityAt: "2026-09-01T10:00:00Z",
  });
  const branchOld = session({
    id: "brav0000000000000000000000000001",
    parentSessionId: root.id,
    forkPointSeq: 2,
    title: `${root.id.slice(0, 12)} · branch @ turn 3`,
    lastActivityAt: "2026-09-02T10:00:00Z",
  });
  const branchNew = session({
    id: "brav0000000000000000000000000002",
    parentSessionId: root.id,
    forkPointSeq: 1,
    lastActivityAt: "2026-09-05T10:00:00Z",
  });
  const unrelated = session({
    id: "zzzz00000000000000000000000001",
    lastActivityAt: "2026-09-04T10:00:00Z",
  });

  const groups = groupSessions([branchNew, unrelated, branchOld, root], undefined, false);
  assertEquals(groups.length, 1);
  // Depth-first lineage adjacency: each root leads its branch block (roots
  // order by recency, so the unrelated session — newer than the root — comes
  // first), with the branch's most recent descendant directly after it.
  assertEquals(groups[0].sessions.map((entry) => entry.id), [
    unrelated.id,
    root.id,
    branchNew.id,
    branchOld.id,
  ]);
});

Deno.test("orderWithLineage tolerates dangling parents", () => {
  const orphan = session({ id: "orphan000000000000000000000001", parentSessionId: "missing" });
  const ordered = orderWithLineage([orphan]);
  assertEquals(ordered.map((entry) => entry.id), [orphan.id]);
});
