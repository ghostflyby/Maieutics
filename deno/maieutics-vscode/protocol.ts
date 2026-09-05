/**
 * Frontend protocol v1 wire types shared by the Maieutics VSCode extension
 * (docs/web-frontend-protocol.md). Frames are a flat versioned record: only
 * the fields named by `type` are populated and absent fields are omitted.
 * Unknown fields on received frames are tolerated.
 */

export const ProtocolVersion = 1;

export interface Capabilities {
  protocolVersion: number;
  serverVersion: string;
  session: SessionInfo;
}

export interface SessionInfo {
  id: string;
  turns: number;
  persistenceEnabled: boolean;
}

export interface StoredSession {
  id: string;
  turns: number;
  createdAt: string;
  lastActivityAt: string;
}

export interface TranscriptMessagePart {
  kind: "text" | "data" | "tool_call" | "tool_result" | "unknown";
  text?: string;
  callId?: string;
  name?: string;
  value?: unknown;
}

export interface TranscriptMessage {
  role: string;
  parts: TranscriptMessagePart[];
}

export interface TranscriptTurn {
  runId: string;
  truncated: boolean;
  model?: { profileId: string; provider: string; model: string };
  messages: TranscriptMessage[];
}

export interface Transcript {
  sessionId: string;
  version: number;
  turns: TranscriptTurn[];
}

/** Typed protocol error carried by non-2xx REST responses. */
export class FrontendError extends Error {
  constructor(
    readonly code: string,
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "FrontendError";
  }
}

export type EventFrameType =
  | "hello"
  | "run.started"
  | "run.status"
  | "text.delta"
  | "message.completed"
  | "tool.started"
  | "tool.progress"
  | "tool.finished"
  | "turn.truncated"
  | "run.completed"
  | "run.failed"
  | "run.missing"
  | "repl.display"
  | "repl.updateDisplay"
  | "repl.clear"
  | "repl.error"
  | "input.request";

/** One WebSocket event frame. Unknown fields are preserved for forward compatibility. */
export interface EventFrame {
  type: EventFrameType | (string & Record<never, never>);
  runId?: string;
  sequence?: number;
  messageId?: string;
  text?: string;
  callId?: string;
  tool?: string;
  arguments?: unknown;
  content?: { kind: string; text?: string; value?: unknown };
  result?: unknown;
  displayId?: string;
  data?: Record<string, unknown>;
  agentMessage?: TranscriptMessage;
  truncated?: boolean;
  code?: string;
  message?: string;
  state?: "busy" | "idle" | (string & Record<never, never>);
  requestId?: string;
  prompt?: string;
  password?: boolean;
  session?: SessionInfo;
  replayed?: boolean;
}
