/** Real browser conformance uses the same production binary graph codec as notebook Actors. */
import { createBinaryGraphCodec, type WireCodec } from "../browser.ts";
export const testWireCodec = createBinaryGraphCodec();

/** Deliberately plain JSON: success-response undefined fields are omitted by JSON.stringify. */
export const jsonWireCodec: WireCodec = {
  encode(value) {
    const encoded = JSON.stringify(value);
    if (encoded === undefined) throw new TypeError("JSON wire requires a frame");
    return encoded;
  },
  decode(message) {
    if (typeof message !== "string") throw new TypeError("JSON wire requires a text frame");
    return JSON.parse(message);
  },
};
