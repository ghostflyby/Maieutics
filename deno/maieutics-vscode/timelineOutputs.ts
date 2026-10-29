import type { OutputSnapshot } from "./notebookFormat.ts";
import type { TimelineEntry } from "./turnTimeline.ts";

/** Live output fragments; serialization rejoins them into the existing notebook snapshot. */
export interface TimelineOutputSnapshot extends OutputSnapshot {
  nodeId: string;
  streaming?: boolean;
}

/** One independently updatable notebook output per node, with turn metadata stored once. */
export function timelineOutputSnapshots(
  snapshot: OutputSnapshot,
  streaming = false,
): TimelineOutputSnapshot[] {
  const { timeline, tools, reasoning, subagents, ...header } = snapshot;
  const outputs: TimelineOutputSnapshot[] = [{ ...header, nodeId: "header", streaming }];
  const entries = timeline ?? [
    ...(reasoning ?? []).map((entry): TimelineEntry => ({
      kind: "reasoning",
      messageId: entry.messageId,
    })),
    ...(tools ?? []).map((_, index): TimelineEntry => ({ kind: "tool", index })),
    ...(subagents ?? []).map((_, index): TimelineEntry => ({ kind: "subagent", index })),
  ];
  for (const [index, entry] of entries.entries()) {
    let fragment: OutputSnapshot | undefined;
    switch (entry.kind) {
      case "text":
        if (entry.text) fragment = { timeline: [{ ...entry }] };
        break;
      case "reasoning": {
        const summary = reasoning?.find((summary) => summary.messageId === entry.messageId);
        if (summary !== undefined) fragment = { reasoning: [summary], timeline: [{ ...entry }] };
        break;
      }
      case "tool": {
        const tool = tools?.[entry.index];
        if (tool !== undefined) {
          fragment = { tools: [tool], timeline: [{ kind: "tool", index: 0 }] };
        }
        break;
      }
      case "subagent": {
        const child = subagents?.[entry.index];
        if (child !== undefined) {
          fragment = { subagents: [child], timeline: [{ kind: "subagent", index: 0 }] };
        }
        break;
      }
    }
    if (fragment !== undefined) outputs.push({ ...fragment, nodeId: `entry:${index}` });
  }
  if (
    !snapshot.error && snapshot.text &&
    !entries.some((entry) => entry.kind === "text" && entry.text === snapshot.text)
  ) {
    outputs.push({
      nodeId: "answer",
      timeline: [{ kind: "text", messageId: "", text: snapshot.text }],
    });
  }
  return outputs;
}

/** Rebase local tool/child indexes while joining node outputs in notebook order. */
export function mergeTimelineOutputSnapshots(snapshots: readonly OutputSnapshot[]): OutputSnapshot {
  const merged: OutputSnapshot = {};
  const timeline: TimelineEntry[] = [];
  const tools: NonNullable<OutputSnapshot["tools"]> = [];
  const reasoning: NonNullable<OutputSnapshot["reasoning"]> = [];
  const subagents: NonNullable<OutputSnapshot["subagents"]> = [];
  for (const snapshot of snapshots) {
    const {
      nodeId: _nodeId,
      streaming: _streaming,
      timeline: entries,
      tools: nodeTools,
      reasoning: nodeReasoning,
      subagents: nodeChildren,
      ...fields
    } = snapshot;
    Object.assign(merged, fields);
    for (const entry of entries ?? []) {
      timeline.push(
        entry.kind === "tool"
          ? { ...entry, index: entry.index + tools.length }
          : entry.kind === "subagent"
          ? { ...entry, index: entry.index + subagents.length }
          : { ...entry },
      );
    }
    tools.push(...nodeTools ?? []);
    reasoning.push(...nodeReasoning ?? []);
    subagents.push(...nodeChildren ?? []);
  }
  if (timeline.length) merged.timeline = timeline;
  if (tools.length) merged.tools = tools;
  if (reasoning.length) merged.reasoning = reasoning;
  if (subagents.length) merged.subagents = subagents;
  return merged;
}
