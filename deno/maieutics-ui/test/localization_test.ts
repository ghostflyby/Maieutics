import { assertEquals } from "@std/assert";
import { historyErrorMessageKey } from "../localization.ts";

Deno.test("history chooses a diagnostic by error code across bundle boundaries", () => {
  for (
    const [code, expected] of [
      ["selection_capacity", "selectionCapacity"],
      ["index_capacity", "indexCapacity"],
      ["range_capacity", "rangeCapacity"],
      ["stream_wait_timeout", "streamTimeout"],
      ["unknown", "readFailure"],
    ]
  ) {
    assertEquals(historyErrorMessageKey({ code }), expected);
  }
  assertEquals(historyErrorMessageKey(new Error("read failed")), "readFailure");
});
