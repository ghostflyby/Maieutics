/**
 * WebSocket transport over the process-owned IPC endpoint.
 *
 * Unix uses Deno's native WebSocket client with a Unix-domain-socket proxy.
 * Windows uses loopback TCP with a bearer credential issued during process
 * bootstrap (the control channel is the only named-pipe surface).
 */

const CONNECT_TIMEOUT_MS = 5_000;
const MAX_MESSAGE_BYTES = 1024 * 1024;
const PENDING_MESSAGE_CAPACITY = 64;

export interface ConnectIpcWebSocketOptions {
  /**
   * Per-connection message size guard, applied to send validation. Defaults
   * to `MAX_MESSAGE_BYTES` (1 MiB); the REPL output endpoint raises it to its
   * binary buffer ceiling.
   */
  maxMessageBytes?: number;
}

export interface IpcWebSocket {
  readonly isOpen: boolean;
  onMessage: ((data: string | Uint8Array) => void) | undefined;
  onClose: (() => void) | undefined;
  onError: ((error: Error) => void) | undefined;
  send(data: string | Uint8Array): void;
  close(code?: number, reason?: string): void;
}

/** Opens an authenticated-process IPC WebSocket and waits for its HTTP upgrade. */
export function connectIpcWebSocket(
  address: string,
  path: string,
  credential?: string,
  options?: ConnectIpcWebSocketOptions,
): Promise<IpcWebSocket> {
  if (address.length === 0 || !path.startsWith("/")) {
    throw new TypeError("The IPC address and absolute WebSocket path are required.");
  }
  if (Deno.build.os === "windows") {
    return NativeTcpWebSocket.connect(address, path, credential, options);
  }
  return NativeUnixWebSocket.connect(address, path, options);
}

class NativeTcpWebSocket implements IpcWebSocket {
  #onMessage: ((data: string | Uint8Array) => void) | undefined;
  #onClose: (() => void) | undefined;
  #onError: ((error: Error) => void) | undefined;
  readonly #pendingMessages: (string | Uint8Array)[] = [];
  readonly #maxMessageBytes: number;
  #closed = false;
  #terminalError: Error | undefined;

  readonly #socket: WebSocket;

  private constructor(socket: WebSocket, maxMessageBytes: number) {
    this.#socket = socket;
    this.#maxMessageBytes = maxMessageBytes;
    socket.onmessage = (event) => {
      const data = messageData(event.data);
      if (data !== null) {
        if (this.#onMessage === undefined) {
          if (this.#pendingMessages.length >= PENDING_MESSAGE_CAPACITY) {
            this.onError?.(new Error("The IPC WebSocket message queue is full."));
            return;
          }
          this.#pendingMessages.push(data);
        } else {
          this.#onMessage(data);
        }
      } else {
        this.onError?.(new Error("The IPC WebSocket only accepts text or binary messages."));
      }
    };
    socket.onerror = () => this.#fail(new Error("The IPC WebSocket failed."));
    socket.onclose = () => this.#finishClose();
  }

  get onMessage(): ((data: string | Uint8Array) => void) | undefined {
    return this.#onMessage;
  }

  set onMessage(handler: ((data: string | Uint8Array) => void) | undefined) {
    this.#onMessage = handler;
    if (handler !== undefined) {
      for (const message of this.#pendingMessages.splice(0)) handler(message);
    }
  }

  get onClose(): (() => void) | undefined {
    return this.#onClose;
  }

  set onClose(handler: (() => void) | undefined) {
    this.#onClose = handler;
    if (handler !== undefined && this.#closed) handler();
  }

  get onError(): ((error: Error) => void) | undefined {
    return this.#onError;
  }

  set onError(handler: ((error: Error) => void) | undefined) {
    this.#onError = handler;
    if (handler !== undefined && this.#terminalError !== undefined) handler(this.#terminalError);
  }

  get isOpen(): boolean {
    return this.#socket.readyState === WebSocket.OPEN;
  }

  #fail(reason: unknown): void {
    if (this.#terminalError === undefined) {
      this.#terminalError = reason instanceof Error ? reason : new Error(String(reason));
      this.#onError?.(this.#terminalError);
    }
    this.#finishClose();
  }

  #finishClose(): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#onClose?.();
  }

  static async connect(
    address: string,
    path: string,
    credential?: string,
    options?: ConnectIpcWebSocketOptions,
  ): Promise<NativeTcpWebSocket> {
    const parsed = new URL(`http://${address}`);
    if (parsed.hostname !== "127.0.0.1" && parsed.hostname !== "localhost") {
      throw new Error("The Windows REPL endpoint must be loopback-only.");
    }
    if (parsed.port.length === 0) {
      throw new Error("The Windows REPL endpoint must include a port.");
    }
    const headers = credential === undefined
      ? undefined
      : { Authorization: `Bearer ${credential}` };
    const socket = new WebSocket(`ws://${address}${path}`, { headers });
    await waitForNativeOpen(socket);
    return new NativeTcpWebSocket(socket, maxMessageBytesOf(options));
  }

  send(data: string | Uint8Array): void {
    validateSendPayload(data, this.#maxMessageBytes);
    this.#socket.send(data);
  }

  close(code?: number, reason?: string): void {
    this.#socket.close(code, reason);
  }
}

class NativeUnixWebSocket implements IpcWebSocket {
  #onMessage: ((data: string | Uint8Array) => void) | undefined;
  #onClose: (() => void) | undefined;
  #onError: ((error: Error) => void) | undefined;
  readonly #pendingMessages: (string | Uint8Array)[] = [];
  readonly #maxMessageBytes: number;
  #closed = false;
  #terminalError: Error | undefined;

  readonly #http: ReturnType<typeof Deno.createHttpClient>;
  readonly #socket: WebSocket;
  #transportClosed = false;

  private constructor(
    http: ReturnType<typeof Deno.createHttpClient>,
    socket: WebSocket,
    maxMessageBytes: number,
  ) {
    this.#http = http;
    this.#socket = socket;
    this.#maxMessageBytes = maxMessageBytes;
    socket.onmessage = (event) => {
      const data = messageData(event.data);
      if (data !== null) {
        if (this.#onMessage === undefined) {
          if (this.#pendingMessages.length >= PENDING_MESSAGE_CAPACITY) {
            this.onError?.(new Error("The IPC WebSocket message queue is full."));
            return;
          }
          this.#pendingMessages.push(data);
        } else {
          this.#onMessage(data);
        }
      } else {
        this.onError?.(new Error("The IPC WebSocket only accepts text or binary messages."));
      }
    };
    socket.onerror = () => this.#fail(new Error("The IPC WebSocket failed."));
    socket.onclose = () => {
      this.#closeTransport();
      this.#finishClose();
    };
  }

  get onMessage(): ((data: string | Uint8Array) => void) | undefined {
    return this.#onMessage;
  }

  set onMessage(handler: ((data: string | Uint8Array) => void) | undefined) {
    this.#onMessage = handler;
    if (handler !== undefined) {
      for (const message of this.#pendingMessages.splice(0)) handler(message);
    }
  }

  get onClose(): (() => void) | undefined {
    return this.#onClose;
  }

  set onClose(handler: (() => void) | undefined) {
    this.#onClose = handler;
    if (handler !== undefined && this.#closed) handler();
  }

  get onError(): ((error: Error) => void) | undefined {
    return this.#onError;
  }

  set onError(handler: ((error: Error) => void) | undefined) {
    this.#onError = handler;
    if (handler !== undefined && this.#terminalError !== undefined) handler(this.#terminalError);
  }

  get isOpen(): boolean {
    return this.#socket.readyState === WebSocket.OPEN;
  }

  #fail(reason: unknown): void {
    if (this.#terminalError === undefined) {
      this.#terminalError = reason instanceof Error ? reason : new Error(String(reason));
      this.#onError?.(this.#terminalError);
    }
    this.#closeTransport();
    this.#finishClose();
  }

  #finishClose(): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#onClose?.();
  }

  static async connect(
    address: string,
    path: string,
    options?: ConnectIpcWebSocketOptions,
  ): Promise<NativeUnixWebSocket> {
    const http = Deno.createHttpClient({
      proxy: { transport: "unix", path: address },
    });
    const socket = new WebSocket(`ws://localhost${path}`, { client: http });
    const owner = new NativeUnixWebSocket(http, socket, maxMessageBytesOf(options));
    try {
      await waitForNativeOpen(socket);
      return owner;
    } catch (error) {
      owner.#closeTransport();
      throw error;
    }
  }

  send(data: string | Uint8Array): void {
    validateSendPayload(data, this.#maxMessageBytes);
    this.#socket.send(data);
  }

  close(code?: number, reason?: string): void {
    this.#socket.close(code, reason);
  }

  #closeTransport(): void {
    if (this.#transportClosed) return;
    this.#transportClosed = true;
    this.#http.close();
  }
}

function messageData(data: unknown): string | Uint8Array | null {
  if (typeof data === "string") return data;
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  if (ArrayBuffer.isView(data)) {
    return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  }
  return null;
}

async function waitForNativeOpen(socket: WebSocket): Promise<void> {
  if (socket.readyState === WebSocket.OPEN) return;
  if (socket.readyState === WebSocket.CLOSING || socket.readyState === WebSocket.CLOSED) {
    throw new Error("The IPC WebSocket closed before opening.");
  }

  let cleanup = (): void => {};
  const opened = new Promise<void>((resolve, reject) => {
    const onOpen = (): void => {
      cleanup();
      resolve();
    };
    const onError = (): void => {
      cleanup();
      reject(new Error("The IPC WebSocket failed to open."));
    };
    const onClose = (): void => {
      cleanup();
      reject(new Error("The IPC WebSocket closed before opening."));
    };
    cleanup = (): void => {
      socket.removeEventListener("open", onOpen);
      socket.removeEventListener("error", onError);
      socket.removeEventListener("close", onClose);
    };

    socket.addEventListener("open", onOpen, { once: true });
    socket.addEventListener("error", onError, { once: true });
    socket.addEventListener("close", onClose, { once: true });

    if (socket.readyState === WebSocket.OPEN) {
      onOpen();
    } else if (
      socket.readyState === WebSocket.CLOSING ||
      socket.readyState === WebSocket.CLOSED
    ) {
      onClose();
    }
  });
  await withTimeout(opened, CONNECT_TIMEOUT_MS, "Timed out opening the IPC WebSocket.");
}

async function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<T>((_resolve, reject) => {
    timer = setTimeout(() => reject(new Error(message)), timeoutMs);
  });
  try {
    return await Promise.race([promise, timeout]);
  } finally {
    clearTimeout(timer);
  }
}

function validateSendPayload(data: string | Uint8Array, maxMessageBytes: number): void {
  const length = typeof data === "string"
    ? new TextEncoder().encode(data).byteLength
    : data.byteLength;
  if (length > maxMessageBytes) {
    throw new RangeError(`The IPC WebSocket message exceeds ${maxMessageBytes} bytes.`);
  }
}

function maxMessageBytesOf(options: ConnectIpcWebSocketOptions | undefined): number {
  return options?.maxMessageBytes ?? MAX_MESSAGE_BYTES;
}
