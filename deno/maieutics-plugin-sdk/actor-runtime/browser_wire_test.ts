import { assertEquals, assertThrows } from "@std/assert";
import { testWireCodec } from "./test_fixtures/browser_wire_codec.ts";

Deno.test("browser conformance wire preserves binary views with ordinary JSON omission rules", async () => {
  const value = {
    value: undefined,
    array: [undefined],
    bytes: new Uint8Array([2, 3, 4]).subarray(1),
  };
  assertEquals(await testWireCodec.decode(testWireCodec.encode(value)), {
    array: [null],
    bytes: value.bytes,
  });
  assertThrows(() => testWireCodec.encode(undefined), TypeError, "JSON frame");
});
