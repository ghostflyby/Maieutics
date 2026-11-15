import { assertEquals } from "@std/assert";
import { testWireCodec } from "./test_fixtures/browser_wire_codec.ts";

Deno.test("browser conformance wire preserves undefined result fields and binary views", async () => {
  const value = {
    value: undefined,
    array: [undefined],
    bytes: new Uint8Array([2, 3, 4]).subarray(1),
  };
  assertEquals(await testWireCodec.decode(testWireCodec.encode(value)), value);
  assertEquals(await testWireCodec.decode(testWireCodec.encode(undefined)), undefined);
});
