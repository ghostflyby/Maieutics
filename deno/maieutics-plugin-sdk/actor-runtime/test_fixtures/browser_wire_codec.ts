/** Conformance-only encoding: JSON metadata plus native binary attachments, never base64. */
import type { WireCodec } from "../browser.ts";
const text = new TextEncoder();
const decodeText = new TextDecoder();
const undefinedValue = Symbol("test wire undefined");
export const testWireCodec: WireCodec = {
  encode(value) {
    const attachments: Uint8Array[] = [];
    const json = JSON.stringify(value, (_key, current) => {
      if (current === undefined) return { __testUndefined: true };
      if (current instanceof Uint8Array || current instanceof ArrayBuffer) {
        const index = attachments.length;
        const bytes = current instanceof Uint8Array ? current : new Uint8Array(current);
        attachments.push(bytes);
        return {
          __testBinary: index,
          length: bytes.byteLength,
          buffer: current instanceof ArrayBuffer,
        };
      }
      if (current instanceof Error || current instanceof DOMException) {
        return { __testError: true, name: current.name, message: current.message };
      }
      return current;
    });
    const header = text.encode(json);
    const output = new Uint8Array(
      4 + header.length + attachments.reduce((sum, bytes) => sum + bytes.length, 0),
    );
    new DataView(output.buffer).setUint32(0, header.length, true);
    output.set(header, 4);
    let offset = 4 + header.length;
    for (const bytes of attachments) {
      output.set(bytes, offset);
      offset += bytes.length;
    }
    return output;
  },
  async decode(raw) {
    const bytes = raw instanceof Blob
      ? new Uint8Array(await raw.arrayBuffer())
      : raw instanceof ArrayBuffer
      ? new Uint8Array(raw)
      : raw;
    if (!(bytes instanceof Uint8Array) || bytes.length < 4) {
      throw new Error("Test wire requires a binary frame");
    }
    const length = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(
      0,
      true,
    );
    if (length > bytes.length - 4) throw new Error("Truncated test wire metadata");
    let offset = 4 + length;
    const value = JSON.parse(decodeText.decode(bytes.subarray(4, offset)), (_key, current) => {
      if (current && typeof current === "object" && current.__testUndefined) return undefinedValue;
      if (current && typeof current === "object") {
        for (const key of Object.keys(current)) {
          if (current[key] === undefinedValue) current[key] = undefined;
        }
      }
      if (current && typeof current === "object" && "__testBinary" in current) {
        if (
          !Number.isSafeInteger(current.length) || current.length < 0 ||
          offset + current.length > bytes.length
        ) throw new Error("Truncated test wire bytes");
        const value = bytes.slice(offset, offset + current.length);
        offset += current.length;
        return current.buffer ? value.buffer : value;
      }
      if (current && typeof current === "object" && current.__testError) {
        return new Error(current.message);
      }
      return current;
    });
    if (offset !== bytes.length) throw new Error("Unexpected test wire bytes");
    return value === undefinedValue ? undefined : value;
  },
};
