// Research main: spawnNode gives a { [name]: Remote } surface AND exposes the
// node transport. Verify: named actors, plus a dedicated channel opened on the
// node transport (host connects the token) carrying custom frames both ways.
import type * as NodeModule from "./node_actor_node.ts";
import { spawnNode } from "@maieutics/plugin-sdk/actor";
import { connectToken } from "@maieutics/plugin-sdk/actor/codec";

const node = await spawnNode<typeof NodeModule.actors>(
  new URL("./node_actor_node.ts", import.meta.url).pathname,
  { permissions: { read: true } },
);

try {
  console.log("repl.initialize() =", await node.repl.initialize());
  console.log(
    "repl.execute(x)   =",
    JSON.stringify(await node.repl.execute("1+1")),
  );
  console.log("broker.ping()     =", await node.broker.ping());

  // Dedicated channel: ask the node (worker side) to open one, then connect the
  // token on the HOST using the node's own transport (the same Mux).
  const token = await node.repl.openDedicatedChannel();
  const channel = connectToken(node.transport, token);
  const ack = await new Promise<string>((resolve) => {
    channel.onMessage((message) => resolve(message as string));
    channel.send("__probe");
  });
  console.log("dedicated channel ack =", ack);
  channel.send("__close");
} finally {
  await node.dispose();
}
console.log("node_actor OK");
