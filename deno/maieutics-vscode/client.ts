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

import type {
  Capabilities,
  EventFrame,
  SessionInfo,
  StoredSession,
  Transcript,
} from "./protocol.ts";
import { FrontendError, ProtocolVersion } from "./protocol.ts";

export interface DiscoveryFile {
  version: number;
  url: string;
  token: string;
  pid: number;
}

export type CommandAnswer = { kind: "command"; markdown: string };
export type TurnAnswer = { kind: "turn"; runId: string };
export type SubmitAnswer = CommandAnswer | TurnAnswer;

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
      const body = await response.json() as { markdown?: string };
      return { kind: "command", markdown: typeof body.markdown === "string" ? body.markdown : "" };
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
