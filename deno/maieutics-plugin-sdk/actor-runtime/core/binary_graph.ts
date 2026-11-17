/** Versioned binary value graphs. References are table indices; binary is never encoded as text. */
import { ActorTransportError, positiveLimit } from "./lifetime.ts";
import type { WireCodec } from "./websocket.ts";

export const BINARY_GRAPH_CODEC = "maieutics-binary-graph/1";
const MAGIC = 0x4742414d; // MABG, little endian
const VERSION = 1;
const enum Value {
  Undefined,
  Null,
  False,
  True,
  Number,
  String,
  BigInt,
  Reference,
}
const enum Kind {
  Object,
  NullObject,
  Array,
  Map,
  Set,
  Buffer,
  View,
  Date,
  RegExp,
  Error,
  DOMException,
}
const errorTypes = [Error, EvalError, RangeError, ReferenceError, SyntaxError, TypeError, URIError];
const views = [
  Uint8Array,
  Int8Array,
  Uint8ClampedArray,
  Uint16Array,
  Int16Array,
  Uint32Array,
  Int32Array,
  Float32Array,
  Float64Array,
  BigInt64Array,
  BigUint64Array,
] as const;
const viewNames = [...views.map((type) => type.name), "DataView"];
type Span = { start: number; end: number };
type Node =
  | {
    kind: Kind.Object | Kind.NullObject | Kind.Array;
    entries: [string, unknown][];
    length: number;
  }
  | { kind: Kind.Map; entries: [unknown, unknown][] }
  | { kind: Kind.Set; entries: unknown[] }
  | { kind: Kind.Buffer; value: ArrayBuffer; whole: boolean; spans: Span[] }
  | { kind: Kind.View; type: number; buffer: ArrayBuffer; offset: number; length: number }
  | { kind: Kind.Date; time: number }
  | { kind: Kind.RegExp; source: string; flags: string }
  | { kind: Kind.Error; type: number; entries: [string, unknown][]; enumerable: Set<string> }
  | { kind: Kind.DOMException; name: string; message: string; entries: [string, unknown][] };

export interface BinaryGraphOptions {
  /** Encoded bytes and total reconstructed ArrayBuffer storage; defaults to one MiB. */
  maxBytes?: number;
  /** Graph object definitions; defaults to 16,384. */
  maxNodes?: number;
  /** Total values, property entries and allocated array slots; defaults to 65,536. */
  maxItems?: number;
  /** First-visit graph depth; defaults to 64. Back references do not increase depth. */
  maxDepth?: number;
}
export interface BinaryGraphCodec extends WireCodec {
  readonly id: typeof BINARY_GRAPH_CODEC;
  encode(value: unknown): Uint8Array<ArrayBuffer>;
}

function invalid(message: string): never {
  throw new ActorTransportError("decode", `Invalid binary Actor graph: ${message}`);
}
function capacity(): never {
  throw new ActorTransportError("capacity", "Binary Actor graph budget exceeded");
}

class Writer {
  #bytes = new Uint8Array(256);
  #length = 0;
  constructor(readonly max: number) {}
  #reserve(length: number): number {
    if (length < 0 || this.#length + length > this.max) capacity();
    if (this.#length + length > this.#bytes.length) {
      const next = new Uint8Array(
        Math.min(this.max, Math.max(this.#length + length, this.#bytes.length * 2)),
      );
      next.set(this.#bytes.subarray(0, this.#length));
      this.#bytes = next;
    }
    const offset = this.#length;
    this.#length += length;
    return offset;
  }
  u8(value: number): void {
    const offset = this.#reserve(1);
    this.#bytes[offset] = value;
  }
  u32(value: number): void {
    if (!Number.isSafeInteger(value) || value < 0 || value > 0xffffffff) capacity();
    const offset = this.#reserve(4);
    new DataView(this.#bytes.buffer).setUint32(offset, value, true);
  }
  f64(value: number): void {
    const offset = this.#reserve(8);
    new DataView(this.#bytes.buffer).setFloat64(offset, value, true);
  }
  text(value: string): void {
    // UTF-16 code units preserve lone surrogates as well as ordinary JavaScript strings.
    this.u32(value.length);
    const offset = this.#reserve(value.length * 2);
    const view = new DataView(this.#bytes.buffer);
    for (let i = 0; i < value.length; i++) {
      view.setUint16(offset + i * 2, value.charCodeAt(i), true);
    }
  }
  raw(value: Uint8Array): void {
    const offset = this.#reserve(value.byteLength);
    this.#bytes.set(value, offset);
  }
  finish(): Uint8Array<ArrayBuffer> {
    return this.#bytes.slice(0, this.#length);
  }
}

class Reader {
  #offset = 0;
  readonly #view: DataView;
  constructor(readonly bytes: Uint8Array) {
    this.#view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }
  #take(length: number): number {
    if (length < 0 || this.#offset + length > this.bytes.byteLength) invalid("truncated frame");
    const offset = this.#offset;
    this.#offset += length;
    return offset;
  }
  u8(): number {
    return this.#view.getUint8(this.#take(1));
  }
  u32(): number {
    return this.#view.getUint32(this.#take(4), true);
  }
  f64(): number {
    return this.#view.getFloat64(this.#take(8), true);
  }
  text(): string {
    const length = this.u32(), offset = this.#take(length * 2);
    let output = "";
    for (let start = 0; start < length; start += 8192) {
      const chars: number[] = [];
      for (let i = start; i < Math.min(length, start + 8192); i++) {
        chars.push(this.#view.getUint16(offset + i * 2, true));
      }
      output += String.fromCharCode(...chars);
    }
    return output;
  }
  raw(length: number): Uint8Array {
    const offset = this.#take(length);
    return this.bytes.subarray(offset, offset + length);
  }
  get ended(): boolean {
    return this.#offset === this.bytes.byteLength;
  }
  get remaining(): number {
    return this.bytes.byteLength - this.#offset;
  }
}

/** Portable WireCodec with independent, bounded object tables for every frame. */
export function createBinaryGraphCodec(options: BinaryGraphOptions = {}): BinaryGraphCodec {
  const maxBytes = positiveLimit(options.maxBytes, 1024 * 1024);
  const maxNodes = positiveLimit(options.maxNodes, 16384);
  const maxItems = positiveLimit(options.maxItems, 65536);
  const maxDepth = positiveLimit(options.maxDepth, 64);
  const encode = (root: unknown): Uint8Array<ArrayBuffer> => {
    const ids = new Map<object, number>();
    const nodes: Node[] = [];
    let items = 0, storage = 0;
    const account = (count: number) => {
      items += count;
      if (items > maxItems) capacity();
    };
    const properties = (value: object): [string, unknown][] => {
      const result: [string, unknown][] = [];
      for (const key in value) {
        if (!Object.hasOwn(value, key)) continue;
        account(1);
        const descriptor = Object.getOwnPropertyDescriptor(value, key);
        if (!descriptor || !("value" in descriptor)) {
          throw new TypeError("Binary Actor graphs require data properties");
        }
        result.push([key, descriptor.value]);
      }
      return result;
    };
    const visit = (value: unknown, depth: number, whole = true): void => {
      account(1);
      if (value === null || typeof value !== "object") {
        if (
          !["undefined", "boolean", "number", "string", "bigint"].includes(typeof value) &&
          value !== null
        ) {
          throw new TypeError(
            "Binary Actor graphs do not serialize functions or symbols; use a payload codec",
          );
        }
        return;
      }
      const previous = ids.get(value);
      if (previous !== undefined) {
        const node = nodes[previous];
        if (node.kind === Kind.Buffer && whole) node.whole = true;
        return;
      }
      if (depth > maxDepth || nodes.length >= maxNodes) capacity();
      ids.set(value, nodes.length);
      let node: Node;
      if (value instanceof ArrayBuffer) {
        storage += value.byteLength;
        if (storage > maxBytes) capacity();
        node = { kind: Kind.Buffer, value, whole, spans: [] };
      } else if (ArrayBuffer.isView(value)) {
        if (!(value.buffer instanceof ArrayBuffer)) {
          throw new TypeError(
            "SharedArrayBuffer requires an explicit snapshot before transmission",
          );
        }
        const type = viewNames.indexOf(Object.prototype.toString.call(value).slice(8, -1));
        if (type < 0) throw new TypeError("Unsupported binary Actor array view");
        node = {
          kind: Kind.View,
          type,
          buffer: value.buffer,
          offset: value.byteOffset,
          length: value.byteLength,
        };
      } else if (Array.isArray(value)) {
        account(value.length);
        node = { kind: Kind.Array, length: value.length, entries: properties(value) };
      } else if (value instanceof Map) {
        account(value.size);
        node = { kind: Kind.Map, entries: [...value] };
      } else if (value instanceof Set) {
        account(value.size);
        node = { kind: Kind.Set, entries: [...value] };
      } else if (value instanceof Date) node = { kind: Kind.Date, time: value.getTime() };
      else if (value instanceof RegExp) {
        node = { kind: Kind.RegExp, source: value.source, flags: value.flags };
      } else if (value instanceof Error && !(value instanceof DOMException)) {
        const type = value.constructor === AggregateError
          ? errorTypes.length
          : errorTypes.indexOf(value.constructor as ErrorConstructor);
        if (type < 0) throw new TypeError("Custom errors require an Actor payload codec");
        const entries: [string, unknown][] = [], enumerable = new Set<string>();
        for (const key of Object.getOwnPropertyNames(value)) {
          account(1);
          const descriptor = Object.getOwnPropertyDescriptor(value, key);
          if (!descriptor || (!("value" in descriptor) && key !== "stack")) {
            throw new TypeError("Binary Actor errors require data properties");
          }
          // V8 exposes native lazy stack formatting through an own accessor.
          entries.push([key, "value" in descriptor ? descriptor.value : value.stack]);
          if (descriptor.enumerable) enumerable.add(key);
        }
        node = { kind: Kind.Error, type, entries, enumerable };
      } else if (value instanceof DOMException) {
        node = {
          kind: Kind.DOMException,
          name: value.name,
          message: value.message,
          entries: properties(value),
        };
      } else {
        const proto = Object.getPrototypeOf(value);
        if (proto !== null && proto !== Object.prototype) {
          throw new TypeError("Unsupported binary Actor prototype; use a payload codec");
        }
        node = {
          kind: proto === null ? Kind.NullObject : Kind.Object,
          length: 0,
          entries: properties(value),
        };
      }
      nodes.push(node);
      if (node.kind === Kind.View) {
        visit(node.buffer, depth + 1, false);
        const id = ids.get(node.buffer);
        const buffer = id === undefined ? undefined : nodes[id];
        if (buffer?.kind !== Kind.Buffer) {
          throw new TypeError("Invalid binary Actor backing buffer");
        }
        buffer.spans.push({ start: node.offset, end: node.offset + node.length });
      } else if (node.kind === Kind.Map) {
        for (const [key, value] of node.entries) {
          visit(key, depth + 1);
          visit(value, depth + 1);
        }
      } else if (node.kind === Kind.Set) {
        for (const value of node.entries) visit(value, depth + 1);
      } else if (
        node.kind === Kind.Object || node.kind === Kind.NullObject || node.kind === Kind.Array ||
        node.kind === Kind.Error || node.kind === Kind.DOMException
      ) {
        for (const [, value] of node.entries) visit(value, depth + 1);
      }
    };
    visit(root, 0);
    const writer = new Writer(maxBytes);
    const value = (current: unknown): void => {
      if (current === undefined) writer.u8(Value.Undefined);
      else if (current === null) writer.u8(Value.Null);
      else if (typeof current === "boolean") writer.u8(current ? Value.True : Value.False);
      else if (typeof current === "number") {
        writer.u8(Value.Number);
        writer.f64(current);
      } else if (typeof current === "string") {
        writer.u8(Value.String);
        writer.text(current);
      } else if (typeof current === "bigint") {
        writer.u8(Value.BigInt);
        writer.u8(current < 0n ? 1 : 0);
        const bytes: number[] = [];
        let magnitude = current < 0n ? -current : current;
        while (magnitude) {
          if (bytes.length >= 1024) capacity();
          bytes.push(Number(magnitude & 255n));
          magnitude >>= 8n;
        }
        writer.u32(bytes.length);
        writer.raw(Uint8Array.from(bytes));
      } else {
        const id = ids.get(current as object);
        if (id === undefined) throw new TypeError("Unregistered binary Actor graph value");
        writer.u8(Value.Reference);
        writer.u32(id);
      }
    };
    writer.u32(MAGIC);
    writer.u8(VERSION);
    writer.u32(nodes.length);
    value(root);
    for (const node of nodes) {
      writer.u8(node.kind);
      switch (node.kind) {
        case Kind.Array:
          writer.u32(node.length); // falls through to enumerable data properties
        case Kind.Object:
        case Kind.NullObject:
          writer.u32(node.entries.length);
          for (const [key, item] of node.entries) {
            writer.text(key);
            value(item);
          }
          break;
        case Kind.Map:
          writer.u32(node.entries.length);
          for (const [key, item] of node.entries) {
            value(key);
            value(item);
          }
          break;
        case Kind.Set:
          writer.u32(node.entries.length);
          for (const item of node.entries) value(item);
          break;
        case Kind.Buffer: {
          writer.u32(node.value.byteLength);
          const spans: Span[] = [];
          for (
            const span of node.whole
              ? [{ start: 0, end: node.value.byteLength }]
              : node.spans.sort((a, b) => a.start - b.start)
          ) {
            const last = spans.at(-1);
            if (last && span.start <= last.end) last.end = Math.max(last.end, span.end);
            else spans.push({ ...span });
          }
          writer.u32(spans.length);
          for (const span of spans) {
            writer.u32(span.start);
            writer.u32(span.end - span.start);
            writer.raw(new Uint8Array(node.value, span.start, span.end - span.start));
          }
          break;
        }
        case Kind.View: {
          const id = ids.get(node.buffer);
          if (id === undefined) throw new TypeError("Unregistered binary Actor buffer");
          writer.u8(node.type);
          writer.u32(id);
          writer.u32(node.offset);
          writer.u32(node.length);
          break;
        }
        case Kind.Date:
          writer.f64(node.time);
          break;
        case Kind.RegExp:
          writer.text(node.source);
          writer.text(node.flags);
          break;
        case Kind.Error:
          writer.u8(node.type);
          writer.u32(node.entries.length);
          for (const [key, item] of node.entries) {
            writer.text(key);
            writer.u8(node.enumerable.has(key) ? 1 : 0);
            value(item);
          }
          break;
        case Kind.DOMException:
          writer.text(node.message);
          writer.text(node.name);
          writer.u32(node.entries.length);
          for (const [key, item] of node.entries) {
            writer.text(key);
            value(item);
          }
          break;
      }
    }
    return writer.finish();
  };

  const decode = (bytes: Uint8Array): unknown => {
    if (bytes.byteLength > maxBytes) capacity();
    const reader = new Reader(bytes);
    if (reader.u32() !== MAGIC || reader.u8() !== VERSION) invalid("encoding or version mismatch");
    const count = reader.u32();
    if (count > maxNodes) capacity();
    let items = 0, storage = 0;
    const account = (length: number) => {
      items += length;
      if (items > maxItems) capacity();
      return length;
    };
    type Ref = { id: number };
    type Atom = { literal: unknown } | Ref;
    const value = (): Atom => {
      account(1);
      switch (reader.u8()) {
        case Value.Undefined:
          return { literal: undefined };
        case Value.Null:
          return { literal: null };
        case Value.False:
          return { literal: false };
        case Value.True:
          return { literal: true };
        case Value.Number:
          return { literal: reader.f64() };
        case Value.String:
          return { literal: reader.text() };
        case Value.BigInt: {
          const sign = reader.u8(), size = reader.u32();
          if (sign > 1 || size > 1024) invalid("invalid bigint");
          const data = reader.raw(size);
          let result = 0n;
          for (let i = data.length - 1; i >= 0; i--) result = (result << 8n) | BigInt(data[i]);
          return { literal: sign ? -result : result };
        }
        case Value.Reference: {
          const id = reader.u32();
          if (id >= count) invalid("reference outside object table");
          return { id };
        }
        default:
          return invalid("unknown value tag");
      }
    };
    const root = value();
    if (count > Math.floor(reader.remaining / 5)) invalid("truncated object table");
    const objects: unknown[] = new Array(count);
    const fills: Array<
      { id: number; kind: Kind; entries: Array<[string | Atom, Atom]>; enumerable?: Set<string> }
    > = [];
    const viewNodes: Array<
      { id: number; type: number; buffer: number; offset: number; length: number }
    > = [];
    for (let id = 0; id < count; id++) {
      const kind: Kind = reader.u8();
      switch (kind) {
        case Kind.Object:
        case Kind.NullObject:
        case Kind.Array: {
          const length = kind === Kind.Array ? account(reader.u32()) : 0;
          objects[id] = kind === Kind.Array
            ? new Array(length)
            : kind === Kind.Object
            ? {}
            : Object.create(null);
          const size = account(reader.u32()), keys = new Set<string>();
          const entries: Array<[string, Atom]> = [];
          for (let i = 0; i < size; i++) {
            const key = reader.text();
            if (
              keys.has(key) ||
              (kind === Kind.Array &&
                (key === "length" ||
                  (/^(0|[1-9]\d*)$/.test(key) && Number(key) < 0xffffffff &&
                    Number(key) >= length)))
            ) invalid("duplicate or invalid property");
            keys.add(key);
            entries.push([key, value()]);
          }
          fills.push({ id, kind, entries });
          break;
        }
        case Kind.Map:
        case Kind.Set: {
          objects[id] = kind === Kind.Map ? new Map() : new Set();
          const size = account(reader.u32()), entries: Array<[Atom, Atom]> = [];
          for (let i = 0; i < size; i++) {
            const key = value();
            entries.push([key, kind === Kind.Map ? value() : key]);
          }
          fills.push({ id, kind, entries });
          break;
        }
        case Kind.Buffer: {
          const length = reader.u32();
          storage += length;
          if (storage > maxBytes) capacity();
          const buffer = new ArrayBuffer(length), data = new Uint8Array(buffer);
          const spans = account(reader.u32());
          let end = 0;
          for (let i = 0; i < spans; i++) {
            const start = reader.u32(), size = reader.u32();
            if (start < end || start + size > length) invalid("invalid binary span");
            data.set(reader.raw(size), start);
            end = start + size;
          }
          objects[id] = buffer;
          break;
        }
        case Kind.View:
          viewNodes.push({
            id,
            type: reader.u8(),
            buffer: reader.u32(),
            offset: reader.u32(),
            length: reader.u32(),
          });
          break;
        case Kind.Date:
          objects[id] = new Date(reader.f64());
          break;
        case Kind.RegExp:
          objects[id] = new RegExp(reader.text(), reader.text());
          break;
        case Kind.Error:
        case Kind.DOMException: {
          if (kind === Kind.Error) {
            const type = reader.u8(), constructor = errorTypes[type];
            if (type === errorTypes.length) objects[id] = new AggregateError([]);
            else if (constructor) objects[id] = new constructor();
            else return invalid("unknown error type");
            Reflect.deleteProperty(objects[id] as object, "stack");
          } else objects[id] = new DOMException(reader.text(), reader.text());
          const size = account(reader.u32()), entries: Array<[string, Atom]> = [];
          const keys = new Set<string>(), enumerable = new Set<string>();
          for (let i = 0; i < size; i++) {
            const key = reader.text();
            if (keys.has(key)) invalid("duplicate error property");
            keys.add(key);
            const flag = kind === Kind.Error ? reader.u8() : 1;
            if (flag > 1) invalid("invalid error property flags");
            if (flag) enumerable.add(key);
            entries.push([key, value()]);
          }
          fills.push({ id, kind, entries, enumerable });
          break;
        }
        default:
          invalid("unknown object tag");
      }
    }
    if (!reader.ended) invalid("trailing bytes");
    for (const node of viewNodes) {
      const buffer = objects[node.buffer], type = views[node.type];
      if (
        !(buffer instanceof ArrayBuffer) || node.type > views.length ||
        node.offset + node.length > buffer.byteLength
      ) invalid("invalid array view");
      if (node.type === views.length) {
        objects[node.id] = new DataView(buffer, node.offset, node.length);
      } else {
        if (!type) return invalid("unknown array view");
        const width = type.BYTES_PER_ELEMENT;
        if (node.offset % width || node.length % width) invalid("misaligned array view");
        objects[node.id] = new type(buffer, node.offset, node.length / width);
      }
    }
    const resolve = (atom: Atom): unknown => "id" in atom ? objects[atom.id] : atom.literal;
    for (const fill of fills) {
      const target = objects[fill.id];
      for (const [key, atom] of fill.entries) {
        if (target instanceof Map) target.set(resolve(key as Atom), resolve(atom));
        else if (target instanceof Set) target.add(resolve(atom));
        else {Object.defineProperty(target, key as string, {
            value: resolve(atom),
            writable: true,
            configurable: true,
            enumerable: fill.enumerable ? fill.enumerable.has(key as string) : true,
          });}
      }
    }
    // Match the Actor registry's first-visit traversal budget without following cycles forever.
    const seen = new Set<object>();
    const check = (current: unknown, depth: number): void => {
      if (current === null || typeof current !== "object" || seen.has(current)) return;
      if (depth > maxDepth) capacity();
      seen.add(current);
      if (current instanceof Map) {
        for (const [k, v] of current) {
          check(k, depth + 1);
          check(v, depth + 1);
        }
      } else if (current instanceof Set) { for (const v of current) check(v, depth + 1); }
      else if (ArrayBuffer.isView(current)) check(current.buffer, depth + 1);
      else if (
        !(current instanceof ArrayBuffer) && !(current instanceof Date) &&
        !(current instanceof RegExp)
      ) {
        for (
          const key of current instanceof Error
            ? Object.getOwnPropertyNames(current)
            : Object.keys(current)
        ) {
          check((current as Record<string, unknown>)[key], depth + 1);
        }
      }
    };
    const result = resolve(root);
    check(result, 0);
    if (seen.size !== count) invalid("unreachable object definitions");
    return result;
  };
  const read = (bytes: Uint8Array, signal?: AbortSignal): unknown => {
    signal?.throwIfAborted();
    try {
      return decode(bytes);
    } catch (error) {
      if (error instanceof ActorTransportError) throw error;
      throw new ActorTransportError("decode", "Invalid binary Actor graph", { cause: error });
    }
  };
  return {
    id: BINARY_GRAPH_CODEC,
    encode(value) {
      try {
        return encode(value);
      } catch (error) {
        if (error instanceof ActorTransportError) throw error;
        throw new ActorTransportError("send", "Cannot encode binary Actor graph", { cause: error });
      }
    },
    decode(message, signal) {
      signal?.throwIfAborted();
      if (typeof message === "string") return invalid("binary frame required");
      if (message instanceof Blob) {
        if (message.size > maxBytes) capacity();
        return message.arrayBuffer().then((bytes) => read(new Uint8Array(bytes), signal));
      }
      return read(message instanceof ArrayBuffer ? new Uint8Array(message) : message, signal);
    },
  };
}
