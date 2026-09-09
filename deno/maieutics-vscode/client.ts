/**
 * HTTP + WebSocket client for the Maieutics frontend protocol (version 1).
 * Web platform standards only — `fetch` and `WebSocket` — so the module runs
 * in the VSCode extension host and under `deno test` unchanged.
 *
 * Reconnect semantics: the events socket reconnects with `sinceSequence` set
 * to the last sequence observed for the server's current run; sequenced frames
 * are additionally deduplicated by (runId, sequence) so replay overlap is
 * harmless. Terminal frames may repeat across reconnects and consumers must
 * treat them idempotently.
 */

import { decodeCommEnvelope, encodeCommEnvelope } from "../shared/comm_codec.ts";
import type {
  Capabilities,
  CommFrame,
  CommHello,
  CommMessage,
  EventFrame,
  SessionInfo,
  StoredSession,
  Transcript,
} from "./protocol.ts";
import { FrontendError, ProtocolVersion } from "./protocol.ts";

/** One selectable model profile (read-only listing). */
export interface ModelProfile {
  id: string;
  provider: string;
  model: string;
  selected: boolean;
}

export interface DiscoveryFile {
  version: number;
  url: string;
  token: string;
  pid: number;
}

export type CommandAnswer = {
  kind: "command";
  markdown: string;
  /** The session the notebook should target after the command: its own unless the command moved the foreground. */
  sessionId?: string;
};
export type TurnAnswer = { kind: "turn"; runId: string };
export type SubmitAnswer = CommandAnswer | TurnAnswer;

/** The session comms socket (ADR 0024). The hello carries the live-comm
 * snapshot; `messages` yields sequenced comm frames and typed errors until
 * the socket closes. */
export interface CommSocket {
  hello: CommHello;
  /** Sends an uplink comm frame; the server assigns ordering. */
  send(message: CommMessage): void;
  messages: AsyncGenerator<CommFrame>;
  close(): void;
}

export interface EventsOptions {
  sinceSequence?: number;
  runId?: string;
  signal?: AbortSignal;
}

export class FrontendClient {
  private constructor(
    readonly baseUrl: string,
    private readonly token: string,
  ) {}

  static fromDiscovery(value: unknown): FrontendClient {
    if (typeof value !== "object" || value === null) {
      throw new FrontendError("protocol_error", 0, "The discovery file is not an object.");
    }
    const record = value as Record<string, unknown>;
    if (record.version !== ProtocolVersion) {
      throw new FrontendError(
        "protocol_error",
        0,
        `Unsupported discovery file version ${String(record.version)}.`,
      );
    }
    const url = record.url;
    const token = record.token;
    if (typeof url !== "string" || !url.startsWith("http://127.0.0.1:")) {
      throw new FrontendError("protocol_error", 0, "The discovery URL is not a loopback endpoint.");
    }
    if (typeof token !== "string" || token.length === 0) {
      throw new FrontendError("protocol_error", 0, "The discovery token is missing.");
    }
    return new FrontendClient(url.replace(/\/$/, ""), token);
  }

  async capabilities(signal?: AbortSignal): Promise<Capabilities> {
    return await this.get("/v1/agent/capabilities", signal);
  }

  async session(signal?: AbortSignal): Promise<SessionInfo> {
    return await this.get("/v1/agent/session", signal);
  }

  async listSessions(signal?: AbortSignal): Promise<StoredSession[]> {
    return await this.get("/v1/agent/sessions", signal);
  }

  async newSession(signal?: AbortSignal): Promise<SessionInfo> {
    return await this.post("/v1/agent/sessions", undefined, signal);
  }

  async resumeSession(sessionId: string, signal?: AbortSignal): Promise<SessionInfo> {
    return await this.post(`/v1/agent/sessions/${sessionId}/resume`, undefined, signal);
  }

  /** Sets or clears one stored session's title (not active-session-gated). An
   * empty title clears; the answer carries the stored, normalized title. */
  async renameSession(
    sessionId: string,
    title: string,
    signal?: AbortSignal,
  ): Promise<string | undefined> {
    const body = await this.post<{ id?: string; title?: string }>(
      `/v1/agent/sessions/${sessionId}/rename`,
      { title },
      signal,
    );
    return body.title;
  }

  /** Forks a stored session at a committed turn and makes the fork the active
   * session (not active-session-gated). The fork keeps the source's turns
   * before the referenced run and re-runs it as its own first turn; the answer
   * carries the new head's id and stored title. An optional `profileId`
   * switches the model profile first (regenerate-with-model). */
  async forkSession(
    sessionId: string,
    body: { runId?: string; seq?: number; profileId?: string },
    signal?: AbortSignal,
  ): Promise<{ id: string; title?: string }> {
    return await this.post(
      `/v1/agent/sessions/${sessionId}/fork`,
      body,
      signal,
    );
  }

  /** Lists the server's selectable model profiles (read-only; absent on older
   * servers, where the call fails with a typed error). */
  async modelProfiles(signal?: AbortSignal): Promise<ModelProfile[]> {
    return await this.get("/v1/model/profiles", signal);
  }

  /** Prunes unreferenced objects of the given session (grace in hours). */
  async pruneObjects(
    sessionId: string,
    graceHours = 24,
    signal?: AbortSignal,
  ): Promise<string> {
    return await this.commandAnswer(
      "POST",
      `/v1/agent/sessions/${sessionId}/gc?graceHours=${graceHours}`,
      signal,
    );
  }

  /** Rebuilds the derived object view of the given session. */
  async repairObjectView(sessionId: string, signal?: AbortSignal): Promise<string> {
    return await this.commandAnswer(
      "POST",
      `/v1/agent/sessions/${sessionId}/repair`,
      signal,
    );
  }

  async statusMarkdown(signal?: AbortSignal): Promise<string> {
    const body = await this.get<{ markdown: string }>("/v1/status", signal);
    return body.markdown;
  }

  async transcript(sessionId: string, signal?: AbortSignal): Promise<Transcript> {
    return await this.get(`/v1/agent/sessions/${sessionId}/transcript`, signal);
  }

  /**
   * Submits one cell. Command cells answer inline with markdown; agent cells
   * start a run and answer with its identifier.
   */
  async submitTurn(sessionId: string, text: string, signal?: AbortSignal): Promise<SubmitAnswer> {
    const response = await this.fetchJson(
      "POST",
      `/v1/agent/sessions/${sessionId}/turns`,
      { text },
      signal,
    );
    if (response.status === 200) {
      const body = await response.json() as { markdown?: string; sessionId?: string };
      return {
        kind: "command",
        markdown: typeof body.markdown === "string" ? body.markdown : "",
        sessionId: typeof body.sessionId === "string" ? body.sessionId : undefined,
      };
    }
    if (response.status === 202) {
      const body = await response.json() as { runId?: string };
      if (typeof body.runId !== "string") {
        throw new FrontendError("protocol_error", 202, "The turn response carries no runId.");
      }
      return { kind: "turn", runId: body.runId };
    }
    throw await this.errorOf(response);
  }

  /** Fetches an immutable display object at its relative URL. The URL is
   * content-addressed (same URL = same bytes forever), so callers may cache
   * aggressively and share entries across displays, runs, and notebooks. */
  async fetchObject(relativeUrl: string, signal?: AbortSignal): Promise<Uint8Array> {
    const response = await fetch(`${this.baseUrl}${relativeUrl}`, {
      headers: { "Authorization": `Bearer ${this.token}` },
      signal,
    });
    if (!response.ok) throw await this.errorOf(response);
    return new Uint8Array(await response.arrayBuffer());
  }

  /** Answers a pending input request announced by an `input.request` frame. */
  async submitInput(requestId: string, value: string, signal?: AbortSignal): Promise<void> {
    const response = await this.fetchJson(
      "POST",
      `/v1/agent/inputs/${requestId}`,
      { value },
      signal,
    );
    if (!response.ok) throw await this.errorOf(response);
    await response.body?.cancel();
  }

  async cancelRun(runId: string, signal?: AbortSignal): Promise<void> {
    const response = await this.fetchJson(
      "POST",
      `/v1/agent/runs/${runId}/cancel`,
      undefined,
      signal,
    );
    if (!response.ok) throw await this.errorOf(response);
    await response.body?.cancel();
  }

  async executeCommand(text: string, signal?: AbortSignal): Promise<string> {
    const response = await this.fetchJson("POST", "/v1/agent/commands", { text }, signal);
    if (!response.ok) throw await this.errorOf(response);
    const body = await response.json() as { markdown?: string };
    return typeof body.markdown === "string" ? body.markdown : "";
  }

  async complete(text: string, cursor: number, signal?: AbortSignal): Promise<string[]> {
    const response = await this.fetchJson("POST", "/v1/agent/complete", { text, cursor }, signal);
    if (!response.ok) throw await this.errorOf(response);
    const body = await response.json() as { matches?: string[] };
    return Array.isArray(body.matches) ? body.matches : [];
  }

  /**
   * Opens the session events socket and yields frames until aborted or closed
   * by the server. Reconnects with exponential backoff while the signal lives;
   * every connected frame sequence is delivered in order and deduplicated.
   */
  async *events(sessionId: string, options: EventsOptions = {}): AsyncGenerator<EventFrame> {
    const seen = new Set<string>();
    let sinceSequence = options.sinceSequence ?? 0;
    let backoffMs = 250;
    while (!options.signal?.aborted) {
      const handle = this.openSocket(sessionId, sinceSequence, options.signal);

      try {
        for await (const frame of handle.messages) {
          if (frame.sequence !== undefined && typeof frame.sequence === "number") {
            const key = `${frame.runId ?? ""}:${frame.sequence}`;
            if (seen.has(key)) continue;
            seen.add(key);
            if (frame.runId !== undefined) sinceSequence = Math.max(sinceSequence, frame.sequence);
          }
          yield frame;
        }
      } finally {
        try {
          handle.socket.close();
        } catch {
          // Already closed.
        }
      }

      if (options.signal?.aborted) return;
      await sleep(backoffMs);
      backoffMs = Math.min(backoffMs * 2, 8000);
    }
  }

  /**
   * Opens the session comms socket (ADR 0024): a full-duplex channel carrying
   * widget comm frames both ways after a JSON hello. Binary frames decode
   * through the shared codec with the envelope sequence; the uplink sends
   * sequence 0 because the server assigns ordering. There is no automatic
   * reconnect — the caller resumes with `sinceSeq` after a close, mirroring
   * the events discipline.
   */
  async commSocket(
    sessionId: string,
    options: { sinceSeq?: number; signal?: AbortSignal } = {},
  ): Promise<CommSocket> {
    const url = `${this.baseUrl}/v1/agent/sessions/${sessionId}/comms` +
      `?sinceSeq=${options.sinceSeq ?? 0}&token=${encodeURIComponent(this.token)}`;
    const socket = new WebSocket(url.replace("http://", "ws://"));
    // The default binary type is "blob"; the codec decodes raw bytes.
    socket.binaryType = "arraybuffer";

    let hello: CommHello | undefined;
    let helloArrived: (() => void) | undefined;
    const helloPromise = new Promise<void>((resolve) => {
      helloArrived = resolve;
    });
    const queue: CommFrame[] = [];
    let wake: (() => void) | null = null;
    const enqueue = (frame: CommFrame) => {
      queue.push(frame);
      wake?.();
      wake = null;
    };
    socket.onmessage = (event) => {
      if (typeof event.data === "string") {
        // The first text frame is the hello; later text frames are typed
        // comm.error answers and stay on the socket.
        let parsed: Partial<CommHello> & { code?: string; commId?: string };
        try {
          parsed = JSON.parse(event.data);
        } catch {
          return; // A malformed text frame is dropped; the server never sends one.
        }
        if (hello === undefined && Array.isArray(parsed.live)) {
          hello = parsed as CommHello;
          helloArrived?.();
          return;
        }

        enqueue({ kind: "error", code: parsed.code ?? "unknown", commId: parsed.commId ?? "" });
        return;
      }

      try {
        const { sequence, message } = decodeCommEnvelope(new Uint8Array(event.data));
        enqueue({ kind: "comm", sequence, message });
      } catch {
        // A malformed binary frame is dropped; the server never sends one.
      }
    };
    const closed = new Promise<void>((resolve) => {
      socket.onclose = () => {
        wake?.();
        wake = null;
        resolve();
      };
    });
    options.signal?.addEventListener("abort", () => {
      try {
        socket.close();
      } catch {
        // Already closed.
      }
    }, { once: true });

    const opened = new Promise<void>((resolve, reject) => {
      socket.onopen = () => resolve();
      socket.onerror = () => {
        reject(new FrontendError("unreachable", 0, "The comms socket failed to open."));
      };
    });
    await opened;
    await Promise.race([
      helloPromise,
      closed.then(() => {
        throw new FrontendError("unreachable", 0, "The comms socket closed before the hello.");
      }),
    ]);
    if (hello === undefined) {
      throw new FrontendError("protocol_error", 0, "The comms hello is missing or malformed.");
    }

    // Iterating does not own the socket: a consumer that stops early keeps the
    // connection (and its other direction) alive; close() is the only close path.
    const messages: AsyncGenerator<CommFrame> = (async function* () {
      while (true) {
        const item = queue.shift();
        if (item !== undefined) {
          yield item;
          continue;
        }
        if (socket.readyState !== WebSocket.OPEN) return;
        await new Promise<void>((resolve) => wake = resolve);
      }
    })();

    return {
      hello,
      send: (message) => socket.send(encodeCommEnvelope(0, message)),
      messages,
      close: () => socket.close(),
    };
  }

  private openSocket(
    sessionId: string,
    sinceSequence: number,
    signal?: AbortSignal,
  ): {
    socket: WebSocket;
    opened: Promise<void>;
    messages: AsyncGenerator<EventFrame>;
  } {
    // The standard WebSocket API cannot set headers, so the events endpoint accepts the
    // bearer token as a query parameter (loopback-only internal API).
    const url = `${this.baseUrl}/v1/agent/sessions/${sessionId}/events` +
      `?sinceSequence=${sinceSequence}&token=${encodeURIComponent(this.token)}`;
    const socket = new WebSocket(url.replace("http://", "ws://"));

    // Frames can arrive in the same network turn as the handshake response, so every
    // handler attaches synchronously at construction — an onmessage attached after
    // `await open` drops that first message (observed on Linux).
    const queue: (EventFrame | "end")[] = [];
    let wake: (() => void) | null = null;
    const enqueue = () => {
      queue.push("end");
      wake?.();
      wake = null;
    };
    socket.onmessage = (event) => {
      try {
        queue.push(JSON.parse(String(event.data)) as EventFrame);
      } catch {
        // A malformed frame is dropped; the server never sends one.
      }
      wake?.();
      wake = null;
    };
    socket.onclose = () => enqueue();
    socket.onerror = () => enqueue();
    signal?.addEventListener("abort", () => {
      try {
        socket.close();
      } catch {
        // Already closed.
      }
      enqueue();
    }, { once: true });

    const messages: AsyncGenerator<EventFrame> = (async function* () {
      while (true) {
        while (queue.length > 0) {
          const item = queue.shift()!;
          if (item === "end") return;
          yield item;
        }

        await new Promise<void>((resolve) => wake = resolve);
      }
    })();

    const opened = new Promise<void>((resolve, reject) => {
      socket.onopen = () => resolve();
      socket.onerror = () => {
        reject(new FrontendError("unreachable", 0, "The events socket failed to open."));
      };
    });
    return { socket, opened, messages };
  }

  private async get<T>(path: string, signal?: AbortSignal): Promise<T> {
    const response = await this.fetchJson("GET", path, undefined, signal);
    if (!response.ok) throw await this.errorOf(response);
    return await response.json() as T;
  }

  /** POSTs without a body and unwraps the `{markdown}` answer the gc/repair
   * endpoints reuse. */
  private async commandAnswer(
    method: "POST",
    path: string,
    signal?: AbortSignal,
  ): Promise<string> {
    const response = await this.fetchJson(method, path, undefined, signal);
    if (!response.ok) throw await this.errorOf(response);
    const body = await response.json() as { markdown?: string };
    return typeof body.markdown === "string" ? body.markdown : "";
  }

  private async post<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
    const response = await this.fetchJson("POST", path, body, signal);
    if (!response.ok) throw await this.errorOf(response);
    return await response.json() as T;
  }

  private fetchJson(
    method: "GET" | "POST",
    path: string,
    body: unknown,
    signal?: AbortSignal,
  ): Promise<Response> {
    return fetch(`${this.baseUrl}${path}`, {
      method,
      headers: {
        "Authorization": `Bearer ${this.token}`,
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
  }

  private async errorOf(response: Response): Promise<FrontendError> {
    let code = "protocol_error";
    let message = `The server answered ${response.status}.`;
    try {
      const body = await response.json() as { code?: string; message?: string };
      if (typeof body.code === "string") code = body.code;
      if (typeof body.message === "string") message = body.message;
    } catch {
      // A non-JSON error body keeps the generic message.
    }
    return new FrontendError(code, response.status, message);
  }
}
function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
