/** The extension owns this loopback-only, one-use capability endpoint; renderer messaging is bootstrap only. */
import { createServer, type Server } from "node:http";
import type { Socket } from "node:net";
import { randomBytes, randomUUID, timingSafeEqual } from "node:crypto";
// @deno-types="@types/ws"
import { type WebSocket as ServerWebSocket, WebSocketServer } from "ws";
import { waitForStartup } from "./startupWait.ts";

interface Lease {
  readonly token: Uint8Array;
  readonly signal: AbortSignal;
  readonly accept: (socket: WebSocket) => void;
  readonly abort: () => void;
  socket?: ServerWebSocket;
  used: boolean;
}
export interface FormActorGatewayOptions {
  /** Resolve the listening loopback base through VS Code's remote port forwarding API. */
  externalUri?(url: string): Promise<string>;
  log(message: string): void;
}

/** Starts lazily, grants no ambient authority, and joins every listener/socket on disposal. */
export class FormActorGateway {
  readonly #leases = new Map<string, Lease>();
  readonly #sockets = new Set<Socket>();
  readonly #lifetime = new AbortController();
  readonly #failures: unknown[] = [];
  #server: Server | undefined;
  #websockets: WebSocketServer | undefined;
  #starting: Promise<string> | undefined;
  #closing: Promise<void> | undefined;
  #stopping: Promise<void> | undefined;
  #closed = false;

  constructor(private readonly options: FormActorGatewayOptions) {}

  /** Resolve the one host-owned connection origin before the embedding document fixes its CSP. */
  async connectionSource(signal: AbortSignal): Promise<string> {
    const endpoint = new URL(await this.base(signal));
    endpoint.protocol = endpoint.protocol === "https:" ? "wss:" : "ws:";
    return endpoint.origin;
  }

  /** Offer one authenticated connection; abort revokes the URL and terminates its accepted socket. */
  async listen(signal: AbortSignal, accept: (socket: WebSocket) => void): Promise<string> {
    signal.throwIfAborted();
    this.#lifetime.signal.throwIfAborted();
    const joined = AbortSignal.any([signal, this.#lifetime.signal]);
    const base = await this.base(joined);
    joined.throwIfAborted();
    if (this.#leases.size >= 128) throw new Error("Form Actor endpoint admission exceeded");
    const id = randomUUID(), token = randomBytes(32);
    const lease: Lease = {
      token,
      signal: joined,
      accept,
      used: false,
      abort: () => {
        if (this.#leases.get(id) !== lease) return;
        this.#leases.delete(id);
        joined.removeEventListener("abort", lease.abort);
        lease.socket?.terminate();
      },
    };
    this.#leases.set(id, lease);
    joined.addEventListener("abort", lease.abort, { once: true });
    const endpoint = new URL(base);
    endpoint.protocol = endpoint.protocol === "https:" ? "wss:" : "ws:";
    endpoint.pathname = `${endpoint.pathname.replace(/\/$/, "")}/actor/${id}`;
    endpoint.searchParams.set("actorToken", token.toString("hex"));
    return endpoint.href;
  }

  private async base(signal: AbortSignal): Promise<string> {
    signal.throwIfAborted();
    this.#lifetime.signal.throwIfAborted();
    this.#starting ??= this.#start().catch(async (error) => {
      this.#closed = true;
      try {
        await this.#stopResources();
      } catch (cleanup) {
        throw new AggregateError([error, cleanup], "Actor gateway startup cleanup failed", {
          cause: error,
        });
      }
      throw error;
    });
    return await waitForStartup(this.#starting, AbortSignal.any([signal, this.#lifetime.signal]));
  }

  dispose(): Promise<void> {
    this.#closing ??= this.#dispose();
    return this.#closing;
  }

  async #start(): Promise<string> {
    const server = createServer(
      { maxHeaderSize: 8192, requestTimeout: 5000, headersTimeout: 5000 },
      (_request, response) => {
        response.writeHead(404, { "Cache-Control": "no-store", "Connection": "close" });
        response.end();
      },
    );
    const websocket = new WebSocketServer({
      noServer: true,
      clientTracking: false,
      perMessageDeflate: false,
      maxPayload: 1024 * 1024,
    });
    this.#server = server;
    this.#websockets = websocket;
    server.maxConnections = 256;
    server.on("connection", (socket) => {
      this.#sockets.add(socket);
      // Includes connections that never finish their HTTP upgrade.
      socket.setTimeout(5000, () => socket.destroy());
      socket.on("error", () => socket.destroy());
      socket.once("close", () => this.#sockets.delete(socket));
    });
    server.on("clientError", (_error, socket) => socket.destroy());
    server.on("upgrade", (request, socket, head) => {
      let path = "", credential = "";
      try {
        const url = new URL(request.url ?? "", "http://127.0.0.1");
        path = url.pathname;
        credential = url.searchParams.get("actorToken") ?? "";
      } catch { /* Invalid requests receive the same denial as unknown capabilities. */ }
      // Port-forwarding adapters may preserve a routing prefix before the capability suffix.
      const match = /\/actor\/([a-f\d-]{36})$/.exec(path);
      const lease = match ? this.#leases.get(match[1]) : undefined;
      const provided = /^[a-f\d]{64}$/.test(credential)
        ? Buffer.from(credential, "hex")
        : undefined;
      if (
        this.#closed || !lease || lease.used || lease.signal.aborted || !provided ||
        provided.length !== lease.token.length || !timingSafeEqual(provided, lease.token)
      ) {
        socket.end("HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        return;
      }
      // Consume before entering ws callbacks so a simultaneous second upgrade cannot reuse it.
      lease.used = true;
      try {
        websocket.handleUpgrade(request, socket, head, (peer) => {
          if (lease.signal.aborted || this.#closed) {
            peer.terminate();
            return;
          }
          lease.socket = peer;
          peer.binaryType = "arraybuffer";
          if ("setTimeout" in socket && typeof socket.setTimeout === "function") {
            socket.setTimeout(0);
          }
          peer.on("error", () => peer.terminate());
          peer.once("close", () => {
            if (match && this.#leases.get(match[1]) === lease) this.#leases.delete(match[1]);
            lease.signal.removeEventListener("abort", lease.abort);
          });
          try {
            lease.accept(peer as unknown as WebSocket);
          } catch (error) {
            this.#record(error, "Form Actor binary peer admission failed.");
            peer.terminate();
          }
        });
      } catch {
        lease.abort();
        socket.destroy();
      }
    });
    let binding = true;
    server.on("error", (error) => {
      // The startup listener owns bind failures; keep this listener installed
      // across the listening callback and its asynchronous continuation.
      if (binding) return;
      this.#record(error, "Form Actor binary listener failed.");
      this.#closed = true;
      this.#lifetime.abort(new Error("Actor gateway listener failed"));
      // Keep cleanup failure observable through the retained stopping task and dispose().
      void this.#stopResources().catch(() => {});
    });
    const address = await new Promise<string>((resolve, reject) => {
      const failed = (error: Error) => {
        binding = false;
        reject(error);
      };
      server.once("error", failed);
      server.listen(0, "127.0.0.1", () => {
        binding = false;
        server.removeListener("error", failed);
        const address = server.address();
        if (!address || typeof address === "string") {
          reject(new Error("Missing Actor gateway address"));
        } else resolve(`http://127.0.0.1:${address.port}/`);
      });
    });
    this.#lifetime.signal.throwIfAborted();
    const external = this.options.externalUri
      ? this.options.externalUri(address)
      : Promise.resolve(address);
    const resolved = new URL(await waitForStartup(external, this.#lifetime.signal));
    if (
      !["http:", "https:"].includes(resolved.protocol) || resolved.username || resolved.password ||
      resolved.hash
    ) {
      throw new Error("Invalid external Actor gateway address");
    }
    return resolved.href;
  }

  async #dispose(): Promise<void> {
    this.#closed = true;
    this.#lifetime.abort(new Error("Actor gateway disposed"));
    for (const lease of this.#leases.values()) lease.abort();
    await this.#starting?.catch(() => {});
    await this.#stopResources();
  }

  #stopResources(): Promise<void> {
    this.#stopping ??= this.#stopResourcesAsync();
    return this.#stopping;
  }

  async #stopResourcesAsync(): Promise<void> {
    for (const socket of this.#sockets) socket.destroy();
    const errors = this.#failures;
    if (this.#websockets) {
      const websocket = this.#websockets;
      await new Promise<void>((resolve) =>
        websocket.close((error) => {
          if (error) errors.push(error);
          resolve();
        })
      );
    }
    if (this.#server?.listening) {
      const server = this.#server;
      await new Promise<void>((resolve) =>
        server.close((error) => {
          if (error) errors.push(error);
          resolve();
        })
      );
    }
    this.#sockets.clear();
    if (errors.length) throw new AggregateError(errors, "Actor gateway cleanup failed");
  }

  #record(error: unknown, message: string): void {
    if (this.#failures.length < 128) this.#failures.push(error);
    try {
      this.options.log(message);
    } catch (error) {
      if (this.#failures.length < 128) this.#failures.push(error);
    }
  }
}
