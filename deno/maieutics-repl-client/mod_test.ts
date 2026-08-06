import { assert, assertEquals } from "jsr:@std/assert";
import { connect, events } from "./mod.ts";

Deno.test("the repl client graph imports only the shared control module", async () => {
  const source = await Deno.readTextFile(new URL("./mod.ts", import.meta.url));
  const imports = [...source.matchAll(/from\s+"([^"]+)"/g)].map((match) => match[1]);
  assertEquals(imports.length > 0, true);
  for (const specifier of imports) {
    assert(
      specifier.startsWith("../shared/") || specifier === "./windows_bootstrap.ts",
      `unexpected import '${specifier}' in the repl client`,
    );
  }
});

Deno.test("the shared bus stays inside the control contract", async () => {
  const source = await Deno.readTextFile(new URL("../shared/bus.ts", import.meta.url));
  const imports = [...source.matchAll(/from\s+"([^"]+)"/g)].map((match) => match[1]);
  for (const specifier of imports) {
    assert(
      specifier === "./protocol.ts",
      `unexpected import '${specifier}' in the shared bus`,
    );
  }
});

Deno.test("connect fails without an env address", () => {
  const previous = Deno.env.get("MAIEUTICS_REPL_IPC");
  Deno.env.delete("MAIEUTICS_REPL_IPC");
  try {
    let threw = false;
    try {
      connect();
    } catch {
      threw = true;
    }
    if (!threw) {
      throw new Error("expected connect() to fail without MAIEUTICS_REPL_IPC");
    }
  } finally {
    if (previous !== undefined) {
      Deno.env.set("MAIEUTICS_REPL_IPC", previous);
    }
  }
});

Deno.test("connect uses an explicit address", () => {
  const client = connect({ address: "/tmp/probe.sock" });
  if (client.address !== "/tmp/probe.sock") {
    throw new Error(`unexpected address: ${client.address}`);
  }
});

Deno.test("connect returns a complete client shape", () => {
  const client = connect({ address: "/tmp/probe.sock" });
  if (typeof client.health !== "function") {
    throw new Error("client is missing health");
  }
  if (typeof client.tools.invoke !== "function") {
    throw new Error("client is missing tools.invoke");
  }
  if (!(client.events instanceof EventTarget)) {
    throw new Error("client.events is not an EventTarget");
  }
  if (typeof client.comm.open !== "function") {
    throw new Error("client is missing comm.open");
  }
});

Deno.test("module events is an EventTarget", () => {
  if (!(events instanceof EventTarget)) {
    throw new Error("module events is not an EventTarget");
  }
});
