import * as vscode from "vscode";
import type { FrontendClient } from "./client.ts";
import { type EventFrame, FrontendError } from "./protocol.ts";
import {
  approvalAnswer,
  approvalChoices,
  approvalTitle,
  isApprovalRequest,
  planApproval,
} from "./approval.ts";
import { coerceField, type ElicitField, planElicitation } from "./elicitation.ts";

// Notebook and conversation views can observe the same request. One native dialog owns it.
const pending = new Map<string, Promise<void>>();
const answered = new Set<string>();

/** Presents a pending kernel request through the same native controls for every frontend. */
export function presentFrontendInput(
  frame: EventFrame,
  client: FrontendClient,
  signal?: AbortSignal,
): Promise<void> {
  const id = frame.requestId;
  if (!id || answered.has(id) || signal?.aborted) return Promise.resolve();
  const existing = pending.get(id);
  if (existing) {
    return existing.catch((error) => {
      if (error instanceof DOMException && error.name === "AbortError" && !signal?.aborted) {
        return presentFrontendInput(frame, client, signal);
      }
      throw error;
    });
  }
  if (pending.size >= 64) return Promise.reject(new Error("Too many pending input dialogs"));
  const task = present(frame, id, client, signal).catch((error) => {
    if (!(error instanceof FrontendError && error.status === 404)) throw error;
  }).then(() => {
    if (signal?.aborted) return;
    answered.add(id);
    if (answered.size > 2048) answered.delete(answered.values().next().value!);
  }).finally(() => pending.delete(id));
  pending.set(id, task);
  return task;
}

async function present(
  frame: EventFrame,
  id: string,
  client: FrontendClient,
  signal?: AbortSignal,
): Promise<void> {
  const owner = new vscode.CancellationTokenSource();
  const abort = () => owner.cancel();
  signal?.addEventListener("abort", abort, { once: true });
  const answer = async (
    value: string | Record<string, unknown>,
    action?: "accept" | "always" | "decline" | "cancel",
    explicit = false,
  ) => {
    signal?.throwIfAborted();
    await client.submitInput(id, value, { action, alwaysSendAction: explicit, signal });
  };
  try {
    if (isApprovalRequest(frame.kind)) {
      const plan = planApproval(
        frame.prompt ?? "Allow this tool to run?",
        frame.tool,
        frame.reason,
        frame.schema,
      );
      const pick = plan.usable
        ? await vscode.window.showQuickPick(approvalChoices(plan).map((row) => ({ ...row })), {
          title: approvalTitle(plan),
          placeHolder: "This tool call needs your approval before it runs",
          ignoreFocusOut: true,
        }, owner.token)
        : undefined;
      await answer("", plan.usable ? approvalAnswer(pick?.action).action : "decline", true);
      return;
    }
    const plan = planElicitation(frame.prompt ?? "Input requested", frame.schema, frame.serverId);
    if (!plan) {
      const value = await vscode.window.showInputBox({
        prompt: frame.prompt ?? "REPL input",
        password: frame.password === true,
        ignoreFocusOut: true,
      }, owner.token);
      await answer(value ?? "");
      return;
    }
    const pick = await vscode.window.showQuickPick(
      [
        {
          label: "$(edit) Fill in form",
          description: "Answer every field",
          action: "accept" as const,
        },
        {
          label: "$(circle-slash) Decline",
          description: "Refuse this request",
          action: "decline" as const,
        },
        { label: "$(close) Cancel", description: "Cancel the request", action: "cancel" as const },
      ],
      {
        title: `${plan.prompt}${plan.serverId === undefined ? "" : ` (server ${plan.serverId})`}`,
        placeHolder: "This MCP server asked for structured input",
        ignoreFocusOut: true,
      },
      owner.token,
    );
    if (pick?.action !== "accept") {
      await answer("", pick?.action ?? "cancel");
      return;
    }
    const values: Record<string, unknown> = {};
    for (const field of plan.fields) {
      const raw = await promptField(
        field,
        `${plan.prompt}${plan.serverId === undefined ? "" : ` (server ${plan.serverId})`}`,
        owner.token,
      );
      if (raw === undefined) {
        await answer("", "cancel");
        return;
      }
      const value = coerceField(field, raw);
      if (!value.ok) {
        await vscode.window.showWarningMessage(
          `Maieutics: ${value.error} The request was cancelled.`,
        );
        await answer("", "cancel");
        return;
      }
      if (value.value !== undefined) values[field.name] = value.value;
    }
    await answer(values, "accept");
  } finally {
    signal?.removeEventListener("abort", abort);
    owner.dispose();
  }
}

async function promptField(
  field: ElicitField,
  prompt: string,
  token: vscode.CancellationToken,
): Promise<string | undefined> {
  const title = `${prompt} — ${field.title}${field.required ? "" : " (optional)"}`;
  if (field.enumValues) {
    return (await vscode.window.showQuickPick(
      field.enumValues.map((value) => ({
        label: value,
        description: value === field.defaultValue ? "default" : undefined,
      })),
      {
        title,
        placeHolder: field.description,
        ignoreFocusOut: true,
      },
      token,
    ))?.label;
  }
  if (field.type === "boolean") {
    const pick = await vscode.window.showQuickPick(
      [{ label: "$(check) Yes", value: "true" }, { label: "$(circle-slash) No", value: "false" }],
      { title, placeHolder: field.description, ignoreFocusOut: true },
      token,
    );
    return pick?.value;
  }
  return await vscode.window.showInputBox({
    title,
    prompt: field.description,
    value: field.defaultValue,
    ignoreFocusOut: true,
  }, token);
}
