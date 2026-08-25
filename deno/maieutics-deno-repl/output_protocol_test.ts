import { assertEquals, assertThrows } from "@std/assert";
import {
  decodeOutputFrame,
  encodeOutputFrame,
  OutputProtocolError,
  REPL_OUTPUT_MAX_MESSAGE_BYTES,
  REPL_OUTPUT_PROTOCOL_VERSION,
  REPL_OUTPUT_WEBSOCKET_PATH,
} from "./output_protocol.ts";

Deno.test("the REPL output protocol exposes its domain path and ceiling", () => {
  assertEquals(REPL_OUTPUT_WEBSOCKET_PATH, "/v1/repl/output/ws");
  assertEquals(REPL_OUTPUT_PROTOCOL_VERSION, 1);
  assertEquals(REPL_OUTPUT_MAX_MESSAGE_BYTES, 64 * 1024 * 1024);
  assertEquals(REPL_OUTPUT_MAX_MESSAGE_BYTES > 1024 * 1024, true);
});

Deno.test("stdout and stderr frames round-trip", () => {
  const stdout = { type: 0 as const, seq: 7, executionId: "execution-1", text: "hello\n" };
  assertEquals(decodeOutputFrame(encodeOutputFrame(stdout)), stdout);

  const stderr = { type: 1 as const, seq: 8, executionId: "execution-2", text: "boom\n" };
  assertEquals(decodeOutputFrame(encodeOutputFrame(stderr)), stderr);
});

Deno.test("display frames carry string MIME values verbatim", () => {
  const frame = {
    type: 2 as const,
    seq: 3,
    executionId: "execution-1",
    data: {
      "text/plain": "1 + 1 = 2",
      "text/html": "<b>2</b>",
      "application/vnd.vega.v5+json": { width: 200 },
    },
    metadata: { isolated: false },
    displayId: "display-1",
    isUpdate: false,
  };
  assertEquals(decodeOutputFrame(encodeOutputFrame(frame)), frame);
});

Deno.test("display frames carry binary buffers as native bytes via placeholders", () => {
  const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x01, 0x02]);
  const jpeg = new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0xff, 0xd9]);
  const frame = {
    type: 2 as const,
    seq: 4,
    executionId: "execution-2",
    data: {
      "image/png": png,
      "image/jpeg": jpeg,
      "text/plain": "two images",
    },
    isUpdate: true,
  };
  const encoded = encodeOutputFrame(frame);
  // The binary values must not leak into the JSON bundle as base64 text: the
  // bundle section only holds `{"$buffer": index}` placeholders.
  const decoded = decodeOutputFrame(encoded) as Extract<
    ReturnType<typeof decodeOutputFrame>,
    { type: 2 }
  >;
  assertEquals(decoded.type, 2);
  assertEquals(decoded.isUpdate, true);
  assertEquals(decoded.executionId, "execution-2");
  assertEquals(decoded.data["image/png"], png);
  assertEquals(decoded.data["image/jpeg"], jpeg);
  assertEquals(decoded.data["text/plain"], "two images");
});

Deno.test("display frames tolerate an empty display id and empty metadata", () => {
  const frame = {
    type: 2 as const,
    seq: 9,
    executionId: "execution-3",
    data: { "text/plain": "solo" },
    isUpdate: true,
  };
  assertEquals(decodeOutputFrame(encodeOutputFrame(frame)), frame);
});

Deno.test("clearOutput frames round-trip both wait values", () => {
  const wait = { type: 3 as const, seq: 2, executionId: "execution-1", wait: true };
  assertEquals(decodeOutputFrame(encodeOutputFrame(wait)), wait);

  const noWait = { type: 3 as const, seq: 3, executionId: "execution-1", wait: false };
  assertEquals(decodeOutputFrame(encodeOutputFrame(noWait)), noWait);
});

Deno.test("the frame sequence must be a positive safe integer", () => {
  for (const seq of [0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1]) {
    const error = assertThrows(
      () => encodeOutputFrame({ type: 0, seq, executionId: "e", text: "x" }),
      OutputProtocolError,
    );
    assertEquals(error.code, "invalid_sequence");
  }
});

Deno.test("the frame execution id must be non-empty", () => {
  const error = assertThrows(
    () => encodeOutputFrame({ type: 0, seq: 1, executionId: "", text: "x" }),
    OutputProtocolError,
  );
  assertEquals(error.code, "invalid_execution_id");
});

Deno.test("truncated frames fail with a typed length error", () => {
  const frame = encodeOutputFrame({
    type: 2,
    seq: 1,
    executionId: "execution-1",
    data: { "image/png": new Uint8Array([1, 2, 3]) },
    isUpdate: false,
  });
  for (const length of [0, 10, frame.length - 1]) {
    const error = assertThrows(
      () => decodeOutputFrame(frame.subarray(0, length)),
      OutputProtocolError,
    );
    assertEquals(error.code, "invalid_frame");
  }
});

Deno.test("unknown frame types fail with a typed protocol error", () => {
  const frame = encodeOutputFrame({ type: 0, seq: 1, executionId: "e", text: "x" });
  frame[0] = 4;
  const error = assertThrows(() => decodeOutputFrame(frame), OutputProtocolError);
  assertEquals(error.code, "unknown_frame_type");
});

Deno.test("out-of-range buffer references fail with a typed protocol error", () => {
  // Hand-crafted display frame whose bundle references buffer index 1 while
  // only one buffer is present: header + bundle + metadata + displayId + flags.
  const executionId = new TextEncoder().encode("execution-1");
  const bundle = new TextEncoder().encode('{"image/png":{"$buffer":1}}');
  const metadata = new TextEncoder().encode("{}");
  const buffer = new Uint8Array([1, 2, 3]);
  const total = 1 + 8 + 2 + executionId.length + 4 + bundle.length + 4 +
    metadata.length + 2 + 2 + 1 + 4 + buffer.length;
  const frame = new Uint8Array(total);
  const view = new DataView(frame.buffer);
  let offset = 0;
  frame[offset++] = 2;
  view.setBigUint64(offset, 1n, false);
  offset += 8;
  view.setUint16(offset, executionId.length, false);
  offset += 2;
  frame.set(executionId, offset);
  offset += executionId.length;
  view.setUint32(offset, bundle.length, false);
  offset += 4;
  frame.set(bundle, offset);
  offset += bundle.length;
  view.setUint32(offset, metadata.length, false);
  offset += 4;
  frame.set(metadata, offset);
  offset += metadata.length;
  view.setUint16(offset, 0, false); // empty display id
  offset += 2;
  frame[offset++] = 0; // isUpdate = display
  view.setUint16(offset, 1, false); // one trailing buffer
  offset += 2;
  view.setUint32(offset, buffer.length, false);
  offset += 4;
  frame.set(buffer, offset);

  const error = assertThrows(() => decodeOutputFrame(frame), OutputProtocolError);
  assertEquals(error.code, "invalid_buffer_reference");
});

Deno.test("frames above the 64 MiB guard fail with a typed protocol error", () => {
  const oversized = new Uint8Array(REPL_OUTPUT_MAX_MESSAGE_BYTES + 1);
  const error = assertThrows(
    () =>
      encodeOutputFrame({
        type: 2,
        seq: 1,
        executionId: "execution-1",
        data: { "image/png": oversized },
        isUpdate: false,
      }),
    OutputProtocolError,
  );
  assertEquals(error.code, "message_too_large");
});
