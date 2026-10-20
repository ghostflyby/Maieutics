/** Tool-approval planning (ADR 0044).
 *
 * An `input.request` frame carrying `kind: "approval"` asks whether one model
 * tool call may execute. This module turns that frame into a presentation plan
 * and maps the user's pick back to the answer's `action`, so the decision
 * vocabulary lives in one place. Pure: no vscode imports, so planning and
 * mapping are unit-testable. */

export interface ApprovalPlan {
  /** The tool awaiting the decision. */
  readonly toolName: string;
  /** The frame's prompt (already policy-composed server-side). */
  readonly prompt: string;
  /** Why the tool needs approval. */
  readonly reason?: string;
  /** Read-only argument context the server attached, when present. */
  readonly argumentContext?: string;
  /** Whether the request is usable: a plan without a tool name cannot be
   * answered meaningfully, so callers fall back to the plain input box. */
  readonly usable: boolean;
}

/** The four decisions a user can make. `always` is the session-scoped one. */
export type ApprovalAction = "accept" | "always" | "decline" | "cancel";

/** Whether one frame is an approval request. Servers predating the discriminator
 * never send `kind`, so only an explicit `approval` counts — an elicitation must
 * not be misread as an approval. */
export function isApprovalRequest(kind: unknown): boolean {
  return kind === "approval";
}

/** Builds the plan for one approval frame. */
export function planApproval(
  prompt: string,
  toolName: unknown,
  reason: unknown,
  schema: unknown,
): ApprovalPlan {
  const name = typeof toolName === "string" ? toolName.trim() : "";
  return {
    toolName: name,
    prompt,
    reason: typeof reason === "string" && reason.trim().length > 0 ? reason : undefined,
    argumentContext: readArgumentContext(schema),
    usable: name.length > 0,
  };
}

/** The read-only argument context the server puts in the schema's description.
 * Anything that is not a bounded string is ignored rather than rendered. */
function readArgumentContext(schema: unknown): string | undefined {
  if (typeof schema !== "object" || schema === null) return undefined;
  const description = (schema as Record<string, unknown>).description;
  if (typeof description !== "string") return undefined;
  const trimmed = description.trim();
  if (trimmed.length === 0 || trimmed === "{}") return undefined;
  return trimmed.length > 2048 ? `${trimmed.slice(0, 2048)}…` : trimmed;
}

/** The quick-pick rows for one plan, most-permissive first: `always` is the
 * shortcut a user reaches for on a tool they trust, and putting it first makes
 * the destructive default (`decline` on dismissal) a deliberate choice. */
export function approvalChoices(plan: ApprovalPlan): readonly {
  readonly label: string;
  readonly description: string;
  readonly action: ApprovalAction;
}[] {
  return [
    {
      label: "$(check) Allow once",
      description: `Run '${plan.toolName}' this time`,
      action: "accept",
    },
    {
      label: "$(verified) Always allow",
      description: `Run '${plan.toolName}' without asking for the rest of this session`,
      action: "always",
    },
    { label: "$(circle-slash) Deny", description: "Do not run this call", action: "decline" },
    { label: "$(close) Cancel", description: "Dismiss without deciding", action: "cancel" },
  ];
}

/** The title for the approval pick, carrying the reason and argument context so
 * the user decides with the same evidence the model had. */
export function approvalTitle(plan: ApprovalPlan): string {
  const parts = [plan.prompt];
  if (plan.reason !== undefined && !plan.prompt.includes(plan.reason)) parts.push(plan.reason);
  if (plan.argumentContext !== undefined) parts.push(`Arguments: ${plan.argumentContext}`);
  return parts.join("\n\n");
}

/** The answer body for one picked action. A dismissal (`undefined`) and an
 * explicit cancel both deny: the kernel treats every non-accept action as a
 * denial, and a dismissed dialog must not be read as consent. */
export function approvalAnswer(action: ApprovalAction | undefined): {
  readonly value: string;
  readonly action: ApprovalAction;
} {
  return { value: "", action: action ?? "cancel" };
}
