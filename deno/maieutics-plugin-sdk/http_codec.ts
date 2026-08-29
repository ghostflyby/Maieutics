/**
 * Wire codec for the plugin HTTP root (ADR 0021 decision 4): moves fetch
 * `Request`/`Response` values across the actor boundary by transferring the
 * BODY stream natively and rebuilding the standard object around it.
 *
 * Deno streams are transfer-only, not cloneable: a postMessage without the
 * transfer list rejects, while listing the stream moves ownership — the
 * receiver gets a live stream and the sender's copy is spent.
 *
 * `Request.signal` is propagated by projection: `AbortSignal` is neither
 * transferable nor faithfully cloneable, and `new Request()` always mints a
 * fresh never-aborting signal. The codec therefore nests the source signal
 * through the built-in abort-signal codec (a MessageChannel projection that
 * rebuilds a real signal) and shadows it onto the rebuilt Request as an own
 * `signal` property. The source is `req.signal` as served by `Deno.serve`
 * (legacy semantics: aborts at request end), which also guarantees the
 * projection channel is torn down for every request — the abort-signal codec
 * has no GC-based release for the sending side.
 *
 * Dual mode:
 *   - messageport transports (plugin workers are in-process workers of the
 *     host): `ctx.transfer` carries the body; the wire holds it in place.
 *   - Mux transports (fork IPC / WebSocket): no transfer list exists, so the
 *     body degrades to chunked relay through the built-in iterable codec's
 *     channel pump. Expected behavior, not a regression.
 *
 * Byte-stream assumption: bodies are consumed as Uint8Array chunks (fetch
 * spec behavior).
 */

import {
  type Codec,
  CODEC_PLACEHOLDER_KEY,
  type DecodeContext,
  type EncodeContext,
} from "@ghostflyby/worker-actor/codec";

interface HttpPlaceholder {
  [CODEC_PLACEHOLDER_KEY]: "maieutics/http";
  kind: "request" | "response";
  /** Request-only wire fields. */
  url?: string;
  method?: string;
  /** Request-only: the projected source signal (abort-signal codec). */
  signal?: unknown;
  /** Response-only wire fields. */
  status?: number;
  statusText?: string;
  /** Shared wire fields. */
  headers: [string, string][];
  /**
   * messageport mode: the body stream itself (natively transferred in place).
   * Mux mode: an iterable-codec placeholder. null for bodiless values.
   */
  body: unknown;
}

function headerEntries(headers: Headers): [string, string][] {
  return [...headers.entries()];
}

function encodeBody(value: Request | Response, ctx: EncodeContext): unknown {
  const body = value.body;
  if (!body) return null;
  if (ctx.transport.kind === "messageport") {
    // Native ownership transfer: keep the stream at its payload slot and list
    // it for the transfer that this very message is sent with.
    if (!ctx.transfer.includes(body)) ctx.transfer.push(body);
    return body;
  }
  // No native transfer list over Mux: delegate to the iterable pump instead.
  return ctx.registry.encode(body, ctx.transfer, ctx.transport);
}

function toByteStream(
  iterable: AsyncIterable<Uint8Array>,
): ReadableStream<Uint8Array> {
  // Adapter over the rebuilt remote iterable. `new Response(iterable).body`
  // would work for plain reads, but its iterator bridge rejects cancel() on
  // early return; a hand-rolled controller keeps cancellation working end to
  // end (ADR 0021 decision 5, response-side leg).
  const iterator = iterable[Symbol.asyncIterator]();
  return new ReadableStream<Uint8Array>({
    async pull(controller) {
      try {
        const next = await iterator.next();
        if (next.done) controller.close();
        else controller.enqueue(next.value);
      } catch (error) {
        controller.error(error);
      }
    },
    async cancel() {
      try {
        await iterator.return?.(undefined);
      } catch {
        // The producer is already gone; cancellation is best-effort.
      }
    },
  });
}

function decodeBody(
  placeholder: unknown,
  ctx: DecodeContext,
): ReadableStream<Uint8Array> | null {
  if (placeholder === null) return null;
  if (placeholder instanceof ReadableStream) return placeholder;
  const iterable = ctx.registry.decode(
    placeholder,
    ctx.transport,
  ) as AsyncIterable<Uint8Array>;
  return toByteStream(iterable);
}

export const httpCodec: Codec<Request | Response> = {
  tag: "maieutics/http",

  matches(value): value is Request | Response {
    return value instanceof Request || value instanceof Response;
  },

  encode(value, ctx): HttpPlaceholder {
    if (value instanceof Request) {
      return {
        [CODEC_PLACEHOLDER_KEY]: "maieutics/http",
        kind: "request",
        url: value.url,
        method: value.method,
        headers: headerEntries(value.headers),
        signal: ctx.registry.encode(value.signal, ctx.transfer, ctx.transport),
        body: encodeBody(value, ctx),
      };
    }
    return {
      [CODEC_PLACEHOLDER_KEY]: "maieutics/http",
      kind: "response",
      status: value.status,
      statusText: value.statusText,
      headers: headerEntries(value.headers),
      body: encodeBody(value, ctx),
    };
  },

  decode(raw, ctx): Request | Response {
    const placeholder = raw as HttpPlaceholder;
    const body = decodeBody(placeholder.body, ctx);
    if (placeholder.kind === "request") {
      const request = new Request(placeholder.url ?? "https://invalid/", {
        method: placeholder.method ?? "GET",
        headers: placeholder.headers,
        ...(body ? { body } : {}),
      });
      if (placeholder.signal !== undefined) {
        const live = ctx.registry.decode(
          placeholder.signal,
          ctx.transport,
        ) as AbortSignal;
        // Shadow the prototype getter with an own data property so the plugin
        // reads `request.signal` and gets the projected, live signal.
        Object.defineProperty(request, "signal", {
          value: live,
          writable: true,
          enumerable: true,
          configurable: true,
        });
      }
      return request;
    }
    return new Response(body, {
      status: placeholder.status ?? 200,
      statusText: placeholder.statusText ?? "",
      headers: placeholder.headers,
    });
  },
};
