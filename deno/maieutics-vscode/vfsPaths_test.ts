import { assert, assertEquals } from "@std/assert";
import {
  isLensDirectory,
  lensMembers,
  parseSessionEntry,
  type SessionDescriptorLike,
} from "./vfsPaths.ts";

const id = "a1b2c3d4e5f6478890a1b2c3d4e5f6a7";

Deno.test("parseSessionEntry accepts only lens-rooted well-formed entries", () => {
  assertEquals(parseSessionEntry(`/sessions/${id}.maieuticsnb`), {
    lens: "sessions",
    sessionId: id,
  });
  assertEquals(parseSessionEntry(`/workspace/${id}.maieuticsnb`), {
    lens: "workspace",
    sessionId: id,
  });
  assertEquals(parseSessionEntry(`/recent/${id}.maieuticsnb`), {
    lens: "recent",
    sessionId: id,
  });

  assert(parseSessionEntry(`/sessions/${id.slice(0, 12)}.maieuticsnb`) === null);
  assert(parseSessionEntry(`/sessions/${id}.json`) === null);
  assert(parseSessionEntry(`/sessions/junk/${id}.maieuticsnb`) === null);
  assert(parseSessionEntry(`/other/${id}.maieuticsnb`) === null);
  assert(parseSessionEntry(`/${id}.maieuticsnb`) === null);
});

Deno.test("isLensDirectory matches only the root and the three lenses", () => {
  assert(isLensDirectory("/"));
  assert(isLensDirectory("/sessions"));
  assert(isLensDirectory("/workspace"));
  assert(isLensDirectory("/recent"));
  assert(!isLensDirectory("/sessionsx"));
  assert(!isLensDirectory(`/sessions/${id}.maieuticsnb`));
  assert(!isLensDirectory(""));
});

Deno.test("lensMembers slices lenses from one sorted descriptor set", () => {
  const sessions: SessionDescriptorLike[] = [
    { id: "1", lastActivityAt: "2026-01-01T00:00:00Z" },
    { id: "2", lastActivityAt: "2026-09-08T10:00:00Z", workspaceRoot: "/repos/alpha" },
    { id: "3", lastActivityAt: "2026-05-01T00:00:00Z", workspaceRoot: "/repos/alpha/" },
    { id: "4", lastActivityAt: "2026-06-01T00:00:00Z", workspaceRoot: "/repos/other" },
  ];

  const all = lensMembers("sessions", sessions, { root: "/repos/alpha", caseInsensitive: true });
  assertEquals(all.map((session) => session.id), ["2", "4", "3", "1"]);

  const recent = lensMembers("recent", sessions, { caseInsensitive: true });
  assertEquals(recent.map((session) => session.id), ["2", "4", "3", "1"].slice(0, 20));

  const workspace = lensMembers("workspace", sessions, {
    root: "/repos/alpha",
    caseInsensitive: true,
  });
  assertEquals(workspace.map((session) => session.id), ["2", "3"]);

  // No known current root: the workspace lens stays empty rather than leaking
  // an unfiltered list.
  const withoutRoot = lensMembers("workspace", sessions, { caseInsensitive: true });
  assertEquals(withoutRoot, []);
});
