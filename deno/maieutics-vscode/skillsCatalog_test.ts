import { assertEquals } from "@std/assert";
import { SkillsCatalog } from "./skillsCatalog.ts";

Deno.test("all() fetches once and caches; failures degrade to empty without caching", async () => {
  let calls = 0;
  let fail = false;
  const catalog = new SkillsCatalog(() => {
    calls++;
    if (fail) return Promise.reject(new Error("down"));
    return Promise.resolve([
      { name: "alpha", description: "a", source: "Workspace" },
    ]);
  });

  assertEquals(await catalog.all(), [
    { name: "alpha", description: "a", source: "Workspace" },
  ]);
  assertEquals(await catalog.all().then((skills) => skills.length), 1);
  assertEquals(calls, 1);

  catalog.invalidate();
  fail = true;
  assertEquals(await catalog.all(), []);
  assertEquals(calls, 2);

  fail = false;
  catalog.invalidate();
  assertEquals(await catalog.all().then((skills) => skills.length), 1);
  assertEquals(calls, 3);
});

Deno.test("query filters by name prefix against the cached catalog", async () => {
  const catalog = new SkillsCatalog(() =>
    Promise.resolve([
      { name: "code-review", description: "r", source: "Workspace" },
      { name: "code-format", description: "f", source: "User" },
      { name: "tdd", description: "t", source: "PluginGenerated" },
    ])
  );

  assertEquals((await catalog.all()).length, 3);
  assertEquals(catalog.query("code-").map((skill) => skill.name), [
    "code-review",
    "code-format",
  ]);
  assertEquals(catalog.query("").length, 3);
  assertEquals(catalog.query("nope"), []);
});
