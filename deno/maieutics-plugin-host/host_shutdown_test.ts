import { assertEquals, assertRejects, assertStrictEquals } from "@std/assert";
import { PluginHost } from "./host.ts";
import { HttpGateway } from "./http.ts";
import { PluginStoragePool } from "./storage_pool.ts";

for (const failing of [[], ["storage"], ["http"], ["storage", "http"]]) {
  Deno.test(`host shutdown preserves all owned failures: ${failing.join("+") || "none"}`, async () => {
    const storageError = new Error("storage shutdown failed");
    const httpError = new Error("HTTP shutdown failed");
    const calls: string[] = [];
    const storage = new (class extends PluginStoragePool {
      override shutdown(): Promise<void> {
        calls.push("storage");
        return failing.includes("storage") ? Promise.reject(storageError) : Promise.resolve();
      }
    })({ readDirs: [], writeDirs: [] });
    const gateway = new (class extends HttpGateway {
      override dispose(): Promise<void> {
        calls.push("http");
        return super.dispose().then(() => {
          if (failing.includes("http")) throw httpError;
        });
      }
    })();
    const host = new PluginHost({
      sdkUrl: new URL("../maieutics-plugin-sdk/entry.ts", import.meta.url).href,
      workerEntryUrl: new URL("./worker_entry.ts", import.meta.url).href,
      plugins: [],
    }, { storage, http: gateway });
    try {
      if (failing.length === 2) {
        const error = await assertRejects(() => host.shutdown(), AggregateError);
        assertStrictEquals(error.errors[0], storageError);
        assertStrictEquals(error.errors[1], httpError);
      } else if (failing.length === 1) {
        const error = await assertRejects(() => host.shutdown());
        assertStrictEquals(error, failing[0] === "storage" ? storageError : httpError);
      } else await host.shutdown();
      assertEquals(calls, ["storage", "http"]);
      host.dispose();
      assertEquals(calls, ["storage", "http"], "Repeated disposal must not start another teardown");
    } finally {
      host.dispose();
    }
  });
}
