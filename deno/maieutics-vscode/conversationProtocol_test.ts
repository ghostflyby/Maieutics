import { assertEquals } from "@std/assert";
import { readViewRequest, type ViewAddress } from "./conversationProtocol.ts";

Deno.test("conversation requests reject stale views, unsafe ids and unknown capabilities", () => {
  const address: ViewAddress = {
    version: 1,
    sessionId: "session",
    viewId: "view",
    generation: "generation",
  };
  const request = { ...address, id: 1, method: "page" as const, params: { offset: 0, limit: 80 } };
  assertEquals(readViewRequest(request, address), request);
  for (
    const patch of [
      { version: 2 },
      { sessionId: "other" },
      { viewId: "other" },
      { generation: "retired" },
      { id: NaN },
      { id: 0 },
      { id: 1.5 },
      { method: "execute" },
      { params: [] },
      { params: null },
    ]
  ) assertEquals(readViewRequest({ ...request, ...patch }, address), undefined);
});
