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
  /** The server keeps multiple sessions live and serves every session-addressed
   * route directly; absent means the legacy single-active-server semantics. */
  multiSession?: boolean;
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

/** One waiting item of the server-owned session turn queue. `text` and
 * `enqueuedAt` ride only the REST snapshot; `queue.updated` frames carry ids
 * alone. */
export interface QueueItemState {
  id: string;
  text?: string;
  enqueuedAt?: string;
}

/** The server-owned per-session turn queue as one full-state snapshot,
 * replaced wholesale on every `queue.updated` frame and `GET /queue` read.
 * `running` is the item currently executing together with the run id its
 * frames travel under; `items` are the waiting items in run order. */
export interface QueueState {
  sessionId: string;
  running: { itemId: string; runId: string } | null;
  items: QueueItemState[];
  capacity: number;
}

/** The fixed task lifecycle vocabulary (ADR 0028 decision 2, the MCP tasks
 * extension's statuses). Values outside the four are tolerated: unknown task
 * kinds may add meanings later, and the client never gates on the word. */
export type TaskStatus =
  | "working"
  | "complete"
  | "fail"
  | "cancel"
  | (string & Record<never, never>);

/** Terminal one-shot detail of a `task://terminal` snapshot: the registry's
 * one-shot handle as the terminal_* tools address it. */
export interface TerminalTaskDetail {
  agentSessionId: string;
  sessionId: string;
  /** The terminal session's wire state ("running", "completed", …). */
  state: string;
  /** The child's exit code once the one-shot settled; absent before that. */
  exitCode?: number;
}

/** Provider-reported token usage of one settled subagent run. */
export interface SubagentTaskUsage {
  inputTokens?: number;
  outputTokens?: number;
  totalTokens?: number;
}

/** Subagent run detail of a `task://agent` snapshot (ADR 0030). Bounded by
 * design: a truncated report preview and usage counts — never the child
 * transcript, and the UI must not try to fetch one by runId. */
export interface AgentSubagentDetail {
  agentSessionId: string;
  /** The child run identifier; also the live child's cancel handle. */
  runId: string;
  /** The final assistant text once the child completed (preview-truncated). */
  report?: string;
  reportTruncated?: boolean;
  usage?: SubagentTaskUsage;
}

/** One full task snapshot as the task plane serves it: a fresh read of one
 * live resource (children leave the plane when their parent run joins them,
 * so any snapshot can be the last). */
export interface TaskSnapshot {
  uri: string;
  /** The owning authority ("agent", "terminal", …); unknown kinds tolerated. */
  kind: string;
  status: TaskStatus;
  terminal?: TerminalTaskDetail;
  agent?: AgentSubagentDetail;
}

/** One entry of the session's task list. Entries may carry the full snapshot
 * detail or just the catalog identity, so status and details are optional and
 * every consumer degrades gracefully on their absence. */
export interface TaskListEntry {
  uri: string;
  kind: string;
  status?: TaskStatus;
  name?: string;
  description?: string;
  terminal?: TerminalTaskDetail;
  agent?: AgentSubagentDetail;
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
  /** The server-owned session queue's full state, carried by `queue.updated`
   * frames (no sequence number; idempotent replacement). */
  queue?: QueueState;
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

/** One declarative form field of a plugin's `ui` data entry (ADR 0038 stage 4). */
export interface PluginFormField {
  name: string;
  label?: string;
  type: string;
  choices?: { value: string; label: string }[];
  required?: boolean;
  placeholder?: string;
}

/** The form template a plugin declares through its `ui` data entry. */
export interface PluginFormTemplate {
  title?: string;
  fields: PluginFormField[];
  submitLabel?: string;
  cancelLabel?: string;
  values?: Record<string, unknown>;
}

/** One plugin's entry on the plugins surface (ADR 0038 stage 4): identity,
 * approval state, optional declarative form, optional approved gateway page. */
export interface PluginInfo {
  id: string;
  name: string;
  approvalState: string;
  form?: PluginFormTemplate | null;
  formError?: string | null;
  pageUrl?: string | null;
}

/** The plugins surface snapshot: `GET /v1/plugins`. */
export interface PluginsResponse {
  plugins: PluginInfo[];
}
