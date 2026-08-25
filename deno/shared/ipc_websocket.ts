/**
 * WebSocket transport over the process-owned IPC endpoint.
 *
 * Unix uses Deno's native WebSocket client with a Unix-domain-socket proxy.
 * Windows opens Kestrel's named pipe through a narrowly scoped kernel32 FFI
 * grant and speaks the HTTP upgrade and WebSocket framing on that byte stream.
 */

const CONNECT_TIMEOUT_MS = 5_000;
const MAX_HANDSHAKE_BYTES = 16 * 1024;
const MAX_MESSAGE_BYTES = 1024 * 1024;
const PENDING_MESSAGE_CAPACITY = 64;
const WEBSOCKET_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

export interface ConnectIpcWebSocketOptions {
  /**
   * Per-connection message size guard, applied to send validation and the
   * Windows named-pipe receive framing. Defaults to `MAX_MESSAGE_BYTES`
   * (1 MiB); the REPL output endpoint raises it to its binary buffer ceiling.
   */
  maxMessageBytes?: number;
}

// Written as one literal: JS bitwise OR coerces both operands to int32, so
// 0x80000000 | 0x40000000 would be -1073741824, which Deno rejects for u32.
const GENERIC_READ_WRITE = 0xC0000000;
const OPEN_EXISTING = 3;
const ERROR_BROKEN_PIPE = 109;
const ERROR_PIPE_BUSY = 231;
const ERROR_NO_DATA = 232;

const WINDOWS_PIPE_SYMBOLS = {
  CreateFileW: {
    parameters: ["buffer", "u32", "u32", "pointer", "u32", "u32", "pointer"],
    result: "pointer",
  },
  WaitNamedPipeW: {
    parameters: ["buffer", "u32"],
    result: "i32",
    nonblocking: true,
  },
  ReadFile: {
    parameters: ["pointer", "buffer", "u32", "buffer", "pointer"],
    result: "i32",
    nonblocking: true,
  },
  WriteFile: {
    parameters: ["pointer", "buffer", "u32", "buffer", "pointer"],
    result: "i32",
    nonblocking: true,
  },
  CloseHandle: {
    parameters: ["pointer"],
    result: "i32",
  },
  GetLastError: {
    parameters: [],
    result: "u32",
  },
} as const;

type Kernel32 = Deno.DynamicLibrary<typeof WINDOWS_PIPE_SYMBOLS>;

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

class WindowsNamedPipeWebSocket implements IpcWebSocket {
  onMessage: ((data: string | Uint8Array) => void) | undefined;
  onClose: (() => void) | undefined;
  onError: ((error: Error) => void) | undefined;

  readonly #handle: Deno.PointerValue;
  readonly #kernel32: Kernel32;
  readonly #reader: NamedPipeReader;
  readonly #maxMessageBytes: number;
  #state = WebSocket.CONNECTING;
  #writes: Promise<void> = Promise.resolve();
  #closed = false;

  private constructor(
    kernel32: Kernel32,
    handle: Deno.PointerValue,
    maxMessageBytes: number,
  ) {
    this.#kernel32 = kernel32;
    this.#handle = handle;
    this.#maxMessageBytes = maxMessageBytes;
    this.#reader = new NamedPipeReader(kernel32, handle);
  }

  get isOpen(): boolean {
    return this.#state === WebSocket.OPEN;
  }

  static async connect(
    pipeName: string,
    path: string,
    options?: ConnectIpcWebSocketOptions,
  ): Promise<WindowsNamedPipeWebSocket> {
    const systemRoot = Deno.env.get("SystemRoot");
    if (systemRoot === undefined || systemRoot.length === 0) {
      throw new Error("SystemRoot is required to resolve kernel32.dll.");
    }
    const kernel32 = Deno.dlopen(
      `${systemRoot}\\System32\\kernel32.dll`,
      WINDOWS_PIPE_SYMBOLS,
    );
    try {
      const handle = await openNamedPipe(kernel32, pipeName);
      const socket = new WindowsNamedPipeWebSocket(
        kernel32,
        handle,
        maxMessageBytesOf(options),
      );
      try {
        await socket.#upgrade(path);
        socket.#state = WebSocket.OPEN;
        socket.#observeReads();
        return socket;
      } catch (error) {
        socket.#finishClose();
        throw error;
      }
    } catch (error) {
      kernel32.close();
      throw error;
    }
  }

  send(data: string | Uint8Array): void {
    if (!this.isOpen) throw new DOMException("The IPC WebSocket is not open.", "InvalidStateError");
    const binary = typeof data === "string";
    const payload = binary ? new TextEncoder().encode(data) : data;
    if (payload.length > this.#maxMessageBytes) {
      throw new RangeError(
        `The IPC WebSocket message exceeds ${this.#maxMessageBytes} bytes.`,
      );
    }
    void this.#enqueueWrite(encodeClientWebSocketFrame(binary ? 0x1 : 0x2, payload));
  }

  close(code = 1000, reason = ""): void {
    if (this.#state >= WebSocket.CLOSING) return;
    const reasonBytes = new TextEncoder().encode(reason);
    if (reasonBytes.length > 123) throw new RangeError("The WebSocket close reason is too long.");
    const payload = new Uint8Array(2 + reasonBytes.length);
    new DataView(payload.buffer).setUint16(0, code, false);
    payload.set(reasonBytes, 2);
    this.#state = WebSocket.CLOSING;
    void this.#enqueueWrite(encodeClientWebSocketFrame(0x8, payload))
      .finally(() => this.#finishClose());
  }

  async #upgrade(path: string): Promise<void> {
    const nonce = crypto.getRandomValues(new Uint8Array(16)).toBase64();
    const request = new TextEncoder().encode(
      `GET ${path} HTTP/1.1\r\n` +
        "Host: maieutics.local\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        `Sec-WebSocket-Key: ${nonce}\r\n` +
        "Sec-WebSocket-Version: 13\r\n\r\n",
    );
    await this.#writeRaw(request);
    const response = new TextDecoder().decode(
      await this.#reader.readUntil(new Uint8Array([13, 10, 13, 10]), MAX_HANDSHAKE_BYTES),
    );
    const lines = response.split("\r\n");
    if (!/^HTTP\/1\.[01] 101(?: |$)/.test(lines[0] ?? "")) {
      throw new Error(`The named-pipe WebSocket upgrade failed: ${lines[0] ?? "empty response"}.`);
    }
    const headers = new Map<string, string>();
    for (const line of lines.slice(1)) {
      const separator = line.indexOf(":");
      if (separator > 0) {
        headers.set(
          line.slice(0, separator).trim().toLowerCase(),
          line.slice(separator + 1).trim(),
        );
      }
    }
    const expectedAccept = new Uint8Array(
      await crypto.subtle.digest("SHA-1", new TextEncoder().encode(nonce + WEBSOCKET_GUID)),
    ).toBase64();
    if (headers.get("sec-websocket-accept") !== expectedAccept) {
      throw new Error("The named-pipe WebSocket returned an invalid accept key.");
    }
  }

  #observeReads(): void {
    this.#readLoop().catch((error) => this.#fail(error));
  }

  async #readLoop(): Promise<void> {
    let fragments: Uint8Array[] = [];
    let fragmentBytes = 0;
    let fragmentOpcode: number | undefined;
    while (this.#state < WebSocket.CLOSED) {
      const first = await this.#reader.readExact(2);
      const final = (first[0] & 0x80) !== 0;
      const opcode = first[0] & 0x0f;
      if ((first[1] & 0x80) !== 0) throw new Error("The server sent a masked WebSocket frame.");
      let length = first[1] & 0x7f;
      if (length === 126) {
        length = new DataView((await this.#reader.readExact(2)).buffer).getUint16(0, false);
      } else if (length === 127) {
        const extended = new DataView((await this.#reader.readExact(8)).buffer).getBigUint64(
          0,
          false,
        );
        if (extended > BigInt(this.#maxMessageBytes)) {
          throw new RangeError(
            `The IPC WebSocket message exceeds ${this.#maxMessageBytes} bytes.`,
          );
        }
        length = Number(extended);
      }
      if (length > this.#maxMessageBytes) {
        throw new RangeError(`The IPC WebSocket frame exceeds ${this.#maxMessageBytes} bytes.`);
      }
      const payload = await this.#reader.readExact(length);
      if (opcode >= 0x8) {
        if (!final || payload.length > 125) {
          throw new Error("The WebSocket control frame is invalid.");
        }
        if (opcode === 0x8) {
          await this.#handleRemoteClose(payload);
          return;
        }
        if (opcode === 0x9) {
          await this.#enqueueWrite(encodeClientWebSocketFrame(0xA, payload));
        } else if (opcode !== 0xA) {
          throw new Error(`Unsupported WebSocket control opcode ${opcode}.`);
        }
        continue;
      }
      if (opcode === 0x1 || opcode === 0x2) {
        if (fragmentOpcode !== undefined) {
          throw new Error("A fragmented WebSocket message is already active.");
        }
        fragmentOpcode = opcode;
      } else if (opcode !== 0x0 || fragmentOpcode === undefined) {
        throw new Error(`Unexpected WebSocket data opcode ${opcode}.`);
      }
      fragments.push(payload);
      fragmentBytes += payload.length;
      if (fragmentBytes > this.#maxMessageBytes) {
        throw new RangeError(`The IPC WebSocket message exceeds ${this.#maxMessageBytes} bytes.`);
      }
      if (!final) continue;
      const message = new Uint8Array(fragmentBytes);
      let offset = 0;
      for (const fragment of fragments) {
        message.set(fragment, offset);
        offset += fragment.length;
      }
      fragments = [];
      fragmentBytes = 0;
      const dataOpcode = fragmentOpcode;
      fragmentOpcode = undefined;
      if (dataOpcode === 0x1) {
        this.onMessage?.(new TextDecoder("utf-8", { fatal: true }).decode(message));
      } else {
        this.onMessage?.(message);
      }
    }
  }

  async #handleRemoteClose(payload: Uint8Array): Promise<void> {
    if (payload.length === 1) throw new Error("The WebSocket close frame is invalid.");
    if (this.#state === WebSocket.OPEN) {
      this.#state = WebSocket.CLOSING;
      await this.#enqueueWrite(encodeClientWebSocketFrame(0x8, payload));
    }
    this.#finishClose();
  }

  #enqueueWrite(bytes: Uint8Array): Promise<void> {
    const operation = this.#writes.then(() => this.#writeRaw(bytes));
    this.#writes = operation.catch((error) => this.#fail(error));
    return operation;
  }

  async #writeRaw(bytes: Uint8Array): Promise<void> {
    let offset = 0;
    while (offset < bytes.length) {
      const written = new Uint32Array(1);
      const chunk = bytes.subarray(offset);
      const ok = await this.#kernel32.symbols.WriteFile(
        this.#handle,
        chunk,
        chunk.length,
        written,
        null,
      );
      if (ok === 0 || written[0] === 0) {
        throw windowsError(this.#kernel32, "write the named pipe");
      }
      offset += written[0];
    }
  }

  #fail(reason: unknown): void {
    if (this.#closed) return;
    const error = reason instanceof Error ? reason : new Error(String(reason));
    try {
      this.onError?.(error);
    } finally {
      this.#finishClose();
    }
  }

  #finishClose(): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#state = WebSocket.CLOSED;
    this.#kernel32.symbols.CloseHandle(this.#handle);
    this.#kernel32.close();
    this.onClose?.();
  }
}

class NamedPipeReader {
  readonly #handle: Deno.PointerValue;
  readonly #kernel32: Kernel32;
  #buffer = new Uint8Array();

  constructor(kernel32: Kernel32, handle: Deno.PointerValue) {
    this.#kernel32 = kernel32;
    this.#handle = handle;
  }

  async readExact(count: number): Promise<Uint8Array> {
    while (this.#buffer.length < count) await this.#readMore();
    const result = this.#buffer.slice(0, count);
    this.#buffer = this.#buffer.slice(count);
    return result;
  }

  async readUntil(marker: Uint8Array, limit: number): Promise<Uint8Array> {
    while (true) {
      const index = indexOfBytes(this.#buffer, marker);
      if (index >= 0) {
        const result = this.#buffer.slice(0, index);
        this.#buffer = this.#buffer.slice(index + marker.length);
        return result;
      }
      if (this.#buffer.length >= limit) throw new RangeError("The IPC handshake is too large.");
      await this.#readMore();
    }
  }

  async #readMore(): Promise<void> {
    const chunk = new Uint8Array(16 * 1024);
    const read = new Uint32Array(1);
    const ok = await this.#kernel32.symbols.ReadFile(
      this.#handle,
      chunk,
      chunk.length,
      read,
      null,
    );
    if (ok === 0 || read[0] === 0) {
      const code = this.#kernel32.symbols.GetLastError();
      if (code === ERROR_BROKEN_PIPE || code === ERROR_NO_DATA) {
        throw new Error("The named-pipe WebSocket closed.");
      }
      throw windowsError(this.#kernel32, "read the named pipe", code);
    }
    const combined = new Uint8Array(this.#buffer.length + read[0]);
    combined.set(this.#buffer);
    combined.set(chunk.subarray(0, read[0]), this.#buffer.length);
    this.#buffer = combined;
  }
}

async function openNamedPipe(kernel32: Kernel32, pipeName: string): Promise<Deno.PointerValue> {
  const path = wideString(`\\\\.\\pipe\\${pipeName}`);
  for (let attempt = 0; attempt < 2; attempt++) {
    const handle = kernel32.symbols.CreateFileW(
      path,
      GENERIC_READ_WRITE,
      0,
      null,
      OPEN_EXISTING,
      0,
      null,
    );
    if (!isInvalidHandle(handle)) return handle;
    const error = kernel32.symbols.GetLastError();
    if (error !== ERROR_PIPE_BUSY || attempt > 0) {
      throw windowsError(kernel32, "open the named pipe", error);
    }
    const ready = await kernel32.symbols.WaitNamedPipeW(path, CONNECT_TIMEOUT_MS);
    if (ready === 0) throw windowsError(kernel32, "wait for the named pipe");
  }
  throw new Error("The named pipe could not be opened.");
}

function isInvalidHandle(handle: Deno.PointerValue): boolean {
  return handle === null || Deno.UnsafePointer.value(handle) === 0xffff_ffff_ffff_ffffn;
}

function wideString(value: string): Uint16Array {
  const result = new Uint16Array(value.length + 1);
  for (let index = 0; index < value.length; index++) result[index] = value.charCodeAt(index);
  return result;
}

function windowsError(kernel32: Kernel32, action: string, code?: number): Error {
  return new Error(`Failed to ${action}: GetLastError=${code ?? kernel32.symbols.GetLastError()}.`);
}

/** @internal Encodes a masked client frame; exported for deterministic protocol tests. */
export function encodeClientWebSocketFrame(opcode: number, payload: Uint8Array): Uint8Array {
  const extended = payload.length < 126 ? 0 : payload.length <= 0xffff ? 2 : 8;
  const frame = new Uint8Array(2 + extended + 4 + payload.length);
  frame[0] = 0x80 | opcode;
  frame[1] = 0x80 | (extended === 0 ? payload.length : extended === 2 ? 126 : 127);
  let offset = 2;
  if (extended === 2) {
    new DataView(frame.buffer).setUint16(offset, payload.length, false);
    offset += 2;
  } else if (extended === 8) {
    new DataView(frame.buffer).setBigUint64(offset, BigInt(payload.length), false);
    offset += 8;
  }
  const mask = crypto.getRandomValues(frame.subarray(offset, offset + 4));
  offset += 4;
  for (let index = 0; index < payload.length; index++) {
    frame[offset + index] = payload[index] ^ mask[index % 4];
  }
  return frame;
}

function indexOfBytes(source: Uint8Array, target: Uint8Array): number {
  outer:
  for (let index = 0; index <= source.length - target.length; index++) {
    for (let part = 0; part < target.length; part++) {
      if (source[index + part] !== target[part]) continue outer;
    }
    return index;
  }
  return -1;
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
