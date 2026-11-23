/** View wire v1. This host-local contract contains no Vue or VS Code objects. */
export interface ViewEntry {
  id: string;
  ordinal: number;
  interactionId: string;
  kind:
    | "input"
    | "message"
    | "tool"
    | "reasoning-summary"
    | "display"
    | "diagnostic"
    | "unavailable";
  contentRevision: string;
  preview: string;
  state: "open" | "sealed";
}
export interface ViewPage {
  sessionId: string;
  epoch: string;
  revision: string;
  total: number;
  entries: ViewEntry[];
}
export interface ViewChange {
  epoch: string;
  baseRevision: string;
  revision: string;
  total: number;
  operations: { type: "upsert"; entry: ViewEntry }[];
}
export interface ViewContent {
  entryId: string;
  revision: string;
  text: string;
  presentation?: { version: 1; kind: string; value?: unknown };
}
export type ViewFrame =
  | { type: "view.snapshot"; page: ViewPage; activeRunId?: string }
  | { type: "view.change"; change: ViewChange; activeRunId?: string };

export interface ViewAddress {
  version: 1;
  sessionId: string;
  viewId: string;
  generation: string;
}
export interface ViewRequest extends ViewAddress {
  id: number;
  method:
    | "presentation"
    | "ready"
    | "page"
    | "content"
    | "submit"
    | "cancel"
    | "copy"
    | "draft"
    | "abort"
    | "reload";
  params?: Record<string, unknown>;
}
export interface ViewResponse extends ViewAddress {
  id: number;
  result?: unknown;
  error?: { code: string; message: string };
}
export interface ViewEvent extends ViewAddress {
  event: "frame" | "draft" | "failure" | "presentation";
  value: unknown;
}

/** Bounds and addresses are checked before any host capability is dispatched. */
export function readViewRequest(value: unknown, address: ViewAddress): ViewRequest | undefined {
  if (!value || typeof value !== "object") return;
  const request = value as Partial<ViewRequest>;
  if (
    request.version !== 1 || request.sessionId !== address.sessionId ||
    request.viewId !== address.viewId || request.generation !== address.generation ||
    !Number.isSafeInteger(request.id) || (request.id ?? 0) < 1 ||
    ![
      "presentation",
      "ready",
      "page",
      "content",
      "submit",
      "cancel",
      "copy",
      "draft",
      "abort",
      "reload",
    ].includes(
      request.method ?? "",
    ) ||
    (request.params !== undefined &&
      (typeof request.params !== "object" || request.params === null ||
        Array.isArray(request.params)))
  ) return;
  return request as ViewRequest;
}
