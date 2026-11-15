/**
 * abort_signal codec: bridges AbortSignal over a MessageChannel.
 *
 * Why (verified by probe + spec): structured clone has no serialization step for
 * AbortSignal in the HTML Standard, and Deno 2.9.5's implementation drops the
 * prototype and aborted state (the clone is a plain object). So the signal is
 * rebuilt over a dedicated channel:
 *
 *   Encoding side (holds the source signal): immediately sends a `status` frame
 *     (aborted + reason); if not aborted, listens for the abort event and
 *     forwards `abort` frames.
 *   Decoding side: rebuilds an AbortController, aborts it on frames, returns
 *     controller.signal.
 *
 * Channel protocol:
 *   sender → receiver  { type: "status"; aborted; reason? }   // initial state
 *   sender → receiver  { type: "abort"; reason? }             // abort propagation
 *   receiver → sender  { type: "release" }                    // rebuilt signal was GC'd
 *
 * Semantics:
 *   - On failAll (actor terminated/crashed) the receiver-side rebuilt signal is
 *     aborted too — cancels long-running work within the actor context.
 *   - The aborted state lands asynchronously (within a tick); event-driven
 *     consumers are unaffected.
 *   - GC-based release is NOT applicable to AbortSignal: abort propagation
 *     requires holding the rebuilt AbortController, and an AbortController
 *     strongly references its signal (controller.signal), which would pin the
 *     signal forever and never fire a finalizer. Release is therefore driven by
 *     explicit frames only: abort/status teardown and failAll. A "release"
 *     frame handler is kept defensively for forward compatibility.
 *   - Sender and receiver both self-remove from the codec's bookkeeping once
 *     their channel is done, so ports/listeners don't linger past the signal's
 *     lifetime.
 */

import {
  type Codec,
  CODEC_PLACEHOLDER_KEY,
  type DecodeContext,
  type EncodeContext,
  getCodecState,
} from "../codec.ts";
import { connectChannel, connectToken, openChannel } from "../channel.ts";
import { ActorTransportError } from "../lifetime.ts";
import { frameRecord } from "../validation.ts";

interface AbortFrame {
  type: "status" | "abort" | "release";
  aborted?: boolean;
  reason?: unknown;
}

interface AbortSignalHandle {
  [CODEC_PLACEHOLDER_KEY]: "abort-signal";
  /** Messageport transports: the transferred peer port. */
  port?: MessagePort;
  /** Mux transports: the channel-establishment token. */
  token?: unknown;
}

interface AbortCodecState {
  /** Sender-side cleanup (stop forwarding, remove listener, close the channel). */
  senders: Set<() => void>;
  /** Receiver-side abort on failAll (cancels the rebuilt signal). */
  receivers: Set<() => void>;
}

function matches(v: unknown): v is AbortSignal {
  return v instanceof AbortSignal;
}

function getState(ctx: { codecState: Map<Codec, unknown> }): AbortCodecState {
  return getCodecState<AbortCodecState>(ctx, abortSignalCodec, () => ({
    senders: new Set(),
    receivers: new Set(),
  }));
}

function encode(signal: AbortSignal, ctx: EncodeContext): unknown {
  const { channel, peerPort, token } = openChannel(ctx);
  ctx.registry.registerChannel(channel, ctx.transport);
  const state = getState(ctx);
  let stopped = false;

  const onAbort = (): void => {
    signal.removeEventListener("abort", onAbort);
    channel.send({ type: "abort", reason: signal.reason } satisfies AbortFrame);
  };
  const cleanup = (): void => {
    if (stopped) return;
    stopped = true;
    signal.removeEventListener("abort", onAbort);
    state.senders.delete(cleanup);
    ctx.registry.unregisterChannel(channel);
    channel.close();
  };
  state.senders.add(cleanup);
  channel.onClose(cleanup);
  channel.onMessage((message) => {
    const frame = frameRecord(message);
    if (frame.type === "release") cleanup();
    else if (frame.type === "status" || frame.type === "abort") {
      throw new ActorTransportError("protocol", "Invalid Actor abort sender frame");
    }
  });
  if (!signal.aborted) signal.addEventListener("abort", onAbort, { once: true });
  // Keep the endpoint alive until the receiver acknowledges the terminal status.
  channel.send(
    { type: "status", aborted: signal.aborted, reason: signal.reason } satisfies AbortFrame,
  );
  const handle: AbortSignalHandle = {
    [CODEC_PLACEHOLDER_KEY]: "abort-signal",
    ...(peerPort !== undefined ? { port: peerPort } : { token }),
  };
  return handle;
}

function decode(
  placeholder: AbortSignalHandle,
  ctx: DecodeContext,
): AbortSignal {
  const controller = new AbortController();
  const state = getState(ctx);
  const channel = placeholder.port !== undefined
    ? connectChannel(placeholder.port, {
      onMessageError: () => teardown(true),
    })
    : connectToken(
      ctx.transport,
      placeholder.token as { __mux: "open"; ch: number },
    );
  ctx.registry.registerChannel(channel, ctx.transport);
  let closed = false;

  // teardown is the single idempotent release path; abortLocal decides whether
  // the rebuilt signal is also aborted (failAll / deserialization error) or the
  // channel is just torn down (peer aborted or released).
  const failAllCleanup = (): void => teardown(true);
  function teardown(abortLocal: boolean): void {
    if (closed) return;
    closed = true;
    state.receivers.delete(failAllCleanup);
    ctx.registry.unregisterChannel(channel);
    channel.close();
    if (abortLocal && !controller.signal.aborted) controller.abort();
  }

  state.receivers.add(failAllCleanup);
  channel.onClose(() => teardown(true));
  channel.onMessage((message) => {
    const frame = frameRecord(message);
    if (frame.type === "status" && typeof frame.aborted !== "boolean") {
      throw new ActorTransportError("protocol", "Invalid Actor abort status frame");
    }
    if (frame.type === "abort" || frame.type === "status" && frame.aborted) {
      controller.abort(frame.reason);
      try {
        channel.send({ type: "release" } satisfies AbortFrame);
      } finally {
        teardown(false);
      }
    } else if (frame.type === "release") teardown(false);
  });

  return controller.signal;
}

function onRegistryFail(state: AbortCodecState | undefined): void {
  if (!state) return;
  for (const cleanup of state.senders) cleanup();
  for (const abort of state.receivers) abort();
  state.senders.clear();
  state.receivers.clear();
  // open channels are closed by the registry's failAll() via registerChannel.
}

export const abortSignalCodec: Codec<AbortSignal> = {
  tag: "abort-signal",
  matches,
  encode,
  decode,
  onRegistryFail,
};
