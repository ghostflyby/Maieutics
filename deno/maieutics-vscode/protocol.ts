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
  /** The executable's workspace root; absent on older servers or when unknown. */
  workspaceRoot?: string;
  comm?: { version: number; maxMessageBytes: number };
}

export interface SessionInfo {
  id: string;
  turns: number;
  persistenceEnabled: boolean;
  /** The user-set title; absent when never renamed (older servers omit it too). */
  title?: string;
}

export interface StoredSession {
  id: string;
  turns: number;
  createdAt: string;
  lastActivityAt: string;
  /** The user-set title; absent when never renamed. */
  title?: string;
  /** The first committed user message, truncated for display; absent before the
   * first committed turn. */
  preview?: string;
  /** The workspace root stamped when the session's row was created; absent for
   * rows written before the column existed. */
  workspaceRoot?: string;
  /** The session this one forked from; absent for root sessions. The fork's
   * history is the parent's first `forkPointSeq` turns plus its own. */
  parentSessionId?: string;
  /** How many of the parent's turns the fork keeps as its prefix; absent for
   * root sessions. */
  forkPointSeq?: number;
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

/** Token usage a provider reported for one run. */
export interface UsageSummary {
  inputTokens: number;
  outputTokens: number;
  totalTokens?: number;
}

/** The configured model identity that produced a run. */
export interface ModelIdentity {
  profileId: string;
  provider: string;
  model: string;
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
  commId?: string;
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
  /** Terminal run metadata: the model identity and provider-reported usage
   * (additive, absent on older servers and on failures). */
  model?: ModelIdentity;
  usage?: UsageSummary;
}

/** Comm types are re-exported from the shared codec so protocol consumers have
 * one import surface (ADR 0024). */
import type { CommMessage } from "../shared/comm_codec.ts";

export { CommKind, type CommMessage } from "../shared/comm_codec.ts";

/** One live comm announced in the comms hello. */
export interface CommDescriptor {
  commId: string;
  targetName?: string;
}

/** The comms WebSocket hello: live identities plus replay status. */
export interface CommHello {
  live: CommDescriptor[];
  replayed: boolean;
  truncated: boolean;
}

/** One decoded comms-socket frame: a sequenced comm message or a typed error. */
export type CommFrame =
  | { kind: "comm"; sequence: number; message: CommMessage }
  | { kind: "error"; code: string; commId: string };
