/**
 * Shared RPC machinery for every channel type (main thread ↔ worker, and
 * worker ↔ worker links). A channel is just an adapter: the request/response
 * protocol, pending-map correlation, codec round-trips and error serialization
 * are identical everywhere, so they live here once.
 *
 *   - makeRpcHandler(api, registry): given an API surface, returns a function
 *     that processes one request frame (lookup → decode args → await →
 *     encode result). The caller owns the transport (postMessage or
 *     channel.send) and only sends the returned result frame.
 *   - createRpcProxy(registry, send, opts): pending-map + id correlation +
 *     codec encoding for the calling side. The caller owns the Proxy shape
 *     (spawn adds dispose()/ActorHandle; a link adds the peer-rpc proxy).
 */

import {
  ActorDiedError,
  RemoteError,
  type RequestFrame,
  type ResponseFrame,
  type SerializedError,
  serializeError,
} from "./protocol.ts";
import type { PayloadCodecRegistry } from "./codec.ts";
import {
  actionFor,
  argPolicyFor,
  collectMoveArgs,
  collectMoveReturn,
  type TransferArgs,
  type TransferReturn,
} from "./transfer.ts";
import type { Transport } from "./transport.ts";
import { ActorTransportError, positiveLimit } from "./lifetime.ts";
import type { TransformCallbacks } from "./type-utils.ts";

// The RPC boundary is inherently dynamic; concrete method types are derived by
// Remote<T>/PeerRpc on the calling side. The any here only serves shape
// matching — it never leaks into the user-visible proxy types.
/** One RPC method signature (the boundary's dynamic shape). */
// deno-lint-ignore no-explicit-any
export type RpcMethod = (...args: any[]) => any;

/** An RPC surface: named methods with arbitrary signatures. */
export type RpcApi = { [method: string]: RpcMethod };

/**
 * Proxy type for calling a peer worker's RPC surface over a link. The peer
 * type cannot be derived across modules (no import relationship between
 * workers), so declare the contract explicitly:
 *   link.rpc as PeerRpc<{ echo(s: string): Promise<string> }>
 *
 * Projection rules mirror Remote<T> (spawn.ts) with one deliberate
 * difference: a bare AsyncIterable return stays wrapped in the call promise
 * (Promise<AsyncIterable<E>>) — link calls do not attach lazy iteration to
 * the promise, so callers await first, then iterate. Parameters project
 * through TransformCallbacks like Remote<T> (callbacks work over links too).
 */
export type PeerRpc<T> = {
  [K in keyof T]: T[K] extends (...args: infer A) => infer R
    ? (...args: TransformCallbacks<A>) => Promise<Awaited<R>>
    : never;
};

export interface RpcResultOk {
  ok: true;
  id: number;
  value: unknown;
  /** Transfer list for the result message (e.g. channel ports created while encoding). */
  transfer: Transferable[];
}

export interface RpcResultError {
  ok: false;
  id: number;
  error: SerializedError;
}

export type RpcResult = RpcResultOk | RpcResultError;

/** One request frame (channel-agnostic: id/method/args). */
export interface RpcRequest {
  id: number;
  method: string;
  args: unknown[];
}

/** One result frame (channel-agnostic). */
export type RpcResponse =
  | { id: number; ok: true; value?: unknown }
  | { id: number; ok: false; error: SerializedError };

/** Build the wire `request` frame for one RPC call (the shared envelope). */
export function requestFrame(request: RpcRequest): RequestFrame {
  return {
    type: "request",
    id: request.id,
    method: request.method,
    args: request.args,
  };
}

/**
 * Send one handler result as a `response` frame. Every runtime shares the
 * same ok/error send branch, so it lives here once: the main channel
 * (createRuntime), node actor channels (serveNode), worker links and
 * callback channels all route their `makeRpcHandler` results through this.
 */
export function sendRpcResult(
  res: RpcResult,
  send: (
    frame: ResponseFrame | RequestFrame,
    transfer?: Transferable[],
  ) => void,
): void {
  if (res.ok) {
    send(
      { type: "response", id: res.id, ok: true, value: res.value },
      res.transfer,
    );
  } else {
    send({ type: "response", id: res.id, ok: false, error: res.error });
  }
}

/** Process one request frame against an API surface; the caller sends the result. */
export function makeRpcHandler(
  api: RpcApi,
  registry: PayloadCodecRegistry,
  transport?: Transport,
  transferReturn?: TransferReturn,
): (request: RpcRequest) => Promise<RpcResult> {
  return async (request) => {
    const fn = Object.hasOwn(api, request.method) ? api[request.method] : undefined;
    if (typeof fn !== "function") {
      return {
        ok: false,
        id: request.id,
        error: serializeError(
          new Error(`No such RPC method: "${request.method}"`),
        ),
      };
    }
    try {
      const args = registry.decode(request.args, transport) as unknown[];
      const value = await fn.apply(api, args);
      const transfer: Transferable[] = [];
      // Return-side move: the worker encodes the return value; if a move
      // policy names this method, keep the value in the payload and list it in
      // the transfer list (sender detaches, receiver gets it in place).
      collectMoveReturn(
        value,
        actionFor(transferReturn, request.method),
        transfer,
        transport,
      );
      return {
        ok: true,
        id: request.id,
        value: registry.encode(value, transfer, transport),
        transfer,
      };
    } catch (e) {
      // Errors must be returned, not thrown: the caller is an async callback
      // where an exception would be silently lost.
      return { ok: false, id: request.id, error: serializeError(e) };
    }
  };
}

export interface RpcProxyOptions {
  /** Maximum simultaneous calls owned by this proxy. */
  maxPendingCalls?: number;
  /** Encode request args and send the request frame over the channel. */
  send: (request: RpcRequest, transfer: Transferable[]) => void;
  /** Returns true once the channel is dead; calls then reject with deadReason(). */
  isDead?: () => boolean;
  /** Rejection reason for calls made after death (default ActorDiedError). */
  deadReason?: () => Error;
  /** The transport this proxy encodes/decodes over (Mux-aware codec values). */
  transport?: Transport;
  /** Argument-side move policy (spawn's transferArgs or a derived view's). */
  transferArgs?: TransferArgs;
}

export interface RpcProxy {
  /** Invoke a method on the remote side; resolves with the decoded result. */
  call(method: string, args: unknown[]): Promise<unknown>;
  /** Feed a result frame back; routes to the matching pending call. */
  deliver(response: RpcResponse): void;
  /** Reject every in-flight call (channel closed / actor killed). */
  rejectAll(reason: unknown): void;
}

/** Calling side: pending-map correlation + codec encoding for one channel. */
export function createRpcProxy(
  registry: PayloadCodecRegistry,
  options: RpcProxyOptions,
): RpcProxy {
  const pending = new Map<number, {
    resolve: (value: unknown) => void;
    reject: (reason: unknown) => void;
  }>();
  let nextId = 1;
  const maxPending = positiveLimit(options.maxPendingCalls, 1024);
  const deadReason = options.deadReason ?? (() => new ActorDiedError());

  return {
    call(method, args): Promise<unknown> {
      if (options.isDead?.()) return Promise.reject(deadReason());
      if (pending.size >= maxPending) {
        return Promise.reject(
          new ActorTransportError("capacity", "Actor pending call budget exceeded"),
        );
      }
      return new Promise((resolve, reject) => {
        const id = nextId++;
        const transfer: Transferable[] = [];
        try {
          // Collect before publishing the pending call so an invalid move
          // request rejects locally without leaving an unreachable entry.
          collectMoveArgs(
            args,
            argPolicyFor(options.transferArgs, method),
            transfer,
            options.transport,
          );
          const encodedArgs = registry.encode(
            args,
            transfer,
            options.transport,
          ) as unknown[];
          pending.set(id, { resolve, reject });
          options.send(
            {
              id,
              method,
              args: encodedArgs,
            },
            transfer,
          );
        } catch (e) {
          pending.delete(id);
          reject(e);
        }
      });
    },
    deliver(response): void {
      const call = pending.get(response.id);
      if (!call) return; // unknown id: possibly a late response after close
      pending.delete(response.id);
      try {
        if (response.ok) {
          call.resolve(
            registry.decode(
              Object.hasOwn(response, "value") ? response.value : undefined,
              options.transport,
            ),
          );
        } else call.reject(new RemoteError(response.error));
      } catch (error) {
        call.reject(error);
        throw error;
      }
    },
    rejectAll(reason): void {
      for (const call of pending.values()) call.reject(reason);
      pending.clear();
    },
  };
}
