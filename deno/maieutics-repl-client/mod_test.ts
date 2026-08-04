import { connect } from "./mod.ts";

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
