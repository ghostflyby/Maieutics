/**
 * A library that wraps Web Workers (and, via the Transport abstraction, child
 * processes and WebSocket connections) into type-safe Actors.
 *
 * Default export: the common surface — creation (spawn / spawnProcess /
 * spawnNode / link / actor pool), the runtimes (serveWorker / serveProcess /
 * serveNode), transport adapters (fromMessagePort / fromNodeIpc /
 * fromWebSocket), errors, and the core proxy types. Advanced capabilities
 * live on two orthogonal sub-paths, imported on demand:
 *
 *   ./codec    — the codec-author toolbox: value codecs, channel / RPC /
 *                stream primitives, connectActor, the acquire control plane
 *                and the wire envelope helpers
 *   ./codecs   — the built-in codecs (iterable / error / abort-signal /
 *                callback)
 */
export { attachLazyIterator, link, spawn } from "./spawn.ts";
export { getActorRegistry, spawnNode, spawnProcess } from "./spawn.ts";
export type {
  ActorHandle,
  Remote,
  SpawnedNode,
  SpawnNodeOptions,
  SpawnOptions,
  SpawnProcessOptions,
} from "./spawn.ts";
export { createActorPool } from "./pool.ts";
export type { ActorPool, ActorPoolOptions } from "./pool.ts";
export { serveNode, serveProcess, serveWorker } from "./worker_runtime.ts";
export type { LinkHandle, ServeWorkerOptions, WorkerApi } from "./worker_runtime.ts";
export { connectActor } from "./core/connect.ts";
export type { ConnectedActor } from "./core/connect.ts";
export { fromMessagePort, fromNodeIpc, messageTransport } from "./core/transport.ts";
export type {
  MessageTransport,
  Transport,
  TransportKind,
  TransportMessage,
} from "./core/transport.ts";
export { ActorDiedError, RemoteError } from "./core/protocol.ts";
export type { RemoteCallback } from "./core/codecs/callback.ts";
export type {
  ArgPolicy,
  ArgSlot,
  ReturnPolicy,
  TransferAction,
  TransferArgs,
  TransferArgsConfig,
  TransferReturn,
  TransferReturnConfig,
} from "./core/transfer.ts";

export { fromWebSocket } from "./core/transport_v8.ts";
export { createActorConnection } from "./core/connection.ts";
export type { ActorConnection, ActorConnectionOptions } from "./core/connection.ts";
export { webSocketTransport } from "./core/websocket.ts";
export type { WebSocketTransportOptions, WireCodec } from "./core/websocket.ts";
