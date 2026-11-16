/** Browser Actor entry: no default host wire encoding or process-spawn API. */
export { createActorConnection } from "./core/connection.ts";
export type { ActorConnection, ActorConnectionOptions } from "./core/connection.ts";
export { webSocketTransport } from "./core/websocket.ts";
export type { WebSocketTransportOptions, WireCodec } from "./core/websocket.ts";
export { createMux, fromMessagePort, messageTransport } from "./core/transport.ts";
export type {
  MessageTransport,
  MessageTransportOptions,
  Transport,
  TransportOptions,
} from "./core/transport.ts";
export { ActorTransportError } from "./core/lifetime.ts";
export { connectActor } from "./core/connect.ts";
export { CODEC_PLACEHOLDER_KEY, PayloadCodecRegistry } from "./core/codec.ts";
export type { Codec, DecodeContext, EncodeContext } from "./core/codec.ts";
export { connectChannel, connectToken, openChannel } from "./core/channel.ts";
export type { Channel } from "./core/channel.ts";
export { createRpcProxy, makeRpcHandler, requestFrame, sendRpcResult } from "./core/rpc.ts";
export { ActorDiedError, RemoteError } from "./core/protocol.ts";
export { link, spawn } from "./spawn_worker.ts";
export { serveWorker } from "./worker_runtime.ts";
export type { RuntimeHandle } from "./worker_runtime.ts";
export type { Remote } from "./spawn_worker.ts";
export * from "./codecs.ts";
