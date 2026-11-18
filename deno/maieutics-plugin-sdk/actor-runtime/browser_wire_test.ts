import { assertEquals, assertThrows } from "@std/assert";
import { jsonWireCodec, testWireCodec } from "./test_fixtures/browser_wire_codec.ts";

Deno.test("browser conformance uses production binary graphs without JSON value loss", async () => {
  const value = {
    value: undefined,
    array: [undefined],
    bytes: new Uint8Array([2, 3, 4]).subarray(1),
  };
  assertEquals(await testWireCodec.decode(testWireCodec.encode(value)), value);
  assertEquals(await testWireCodec.decode(testWireCodec.encode(undefined)), undefined);
});

Deno.test("the separate plain JSON conformance codec retains ordinary omission rules", async () => {
  assertEquals(
    await jsonWireCodec.decode(jsonWireCodec.encode({ value: undefined, array: [undefined] })),
    { array: [null] },
  );
  assertThrows(() => jsonWireCodec.encode(undefined), TypeError, "JSON wire");
});
