import type { OutputSnapshot } from "./notebookFormat.ts";
import type { TimelineEntry } from "./turnTimeline.ts";

/** Live output fragments; serialization rejoins them into the existing notebook snapshot. */
export interface TimelineOutputSnapshot extends OutputSnapshot {
  nodeId: string;
  streaming?: boolean;
}

/** Retains immutable output identities so unchanged payloads never need serialization. */
export class TimelineOutputCache {
  private header: TimelineOutputSnapshot | undefined;
  private answer: { text: string; value: TimelineOutputSnapshot } | undefined;
  private readonly entries = new Map<
    string,
    { entry: TimelineEntry; source: object; value: TimelineOutputSnapshot }
  >();

  update(snapshot: OutputSnapshot, streaming = false): TimelineOutputSnapshot[] {
    const outputs = timelineOutputSnapshots(snapshot, streaming, this);
    const retained = new Set(outputs.map((node) => node.nodeId));
    for (const id of this.entries.keys()) if (!retained.has(id)) this.entries.delete(id);
    return outputs;
  }

  headerNode(next: TimelineOutputSnapshot): TimelineOutputSnapshot {
    const previous = this.header;
    if (previous !== undefined) {
      const keys = Object.keys(next) as (keyof TimelineOutputSnapshot)[];
      if (
        keys.length === Object.keys(previous).length &&
        keys.every((key) => next[key] === previous[key])
      ) return previous;
    }
    this.header = next;
    return next;
  }

  answerNode(text: string): TimelineOutputSnapshot {
    if (this.answer?.text === text) return this.answer.value;
    const value = { nodeId: "answer", timeline: [{ kind: "text" as const, messageId: "", text }] };
    this.answer = { text, value };
    return value;
  }

  node(
    id: string,
    entry: TimelineEntry,
    source: object,
    fragment: OutputSnapshot,
  ): TimelineOutputSnapshot {
    const previous = this.entries.get(id);
    if (previous?.entry === entry && previous.source === source) return previous.value;
    const value = { ...fragment, nodeId: id };
    this.entries.set(id, { entry, source, value });
    return value;
  }
}

/** One independently updatable notebook output per node, with turn metadata stored once. */
export function timelineOutputSnapshots(
  snapshot: OutputSnapshot,
  streaming = false,
  cache?: TimelineOutputCache,
): TimelineOutputSnapshot[] {
  const { timeline, tools, reasoning, subagents, text, repl: _repl, ...header } = snapshot;
  const headerNode: TimelineOutputSnapshot = {
    ...header,
    // Non-empty answers live in their own text node. Preserve an explicit empty
    // final message and the error body's retry action rather than inferring either.
    ...(snapshot.error || text === "" ? { text } : {}),
    nodeId: "header",
    streaming,
  };
  const outputs = [cache?.headerNode(headerNode) ?? headerNode];
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
    let source: object = entry;
    switch (entry.kind) {
      case "text":
        if (entry.text) fragment = { timeline: [{ ...entry }] };
        break;
      case "reasoning": {
        const summary = reasoning?.find((summary) => summary.messageId === entry.messageId);
        if (summary !== undefined) {
          source = summary;
          fragment = { reasoning: [summary], timeline: [{ ...entry }] };
        }
        break;
      }
      case "tool": {
        const tool = tools?.[entry.index];
        if (tool !== undefined) {
          source = tool;
          fragment = { tools: [tool], timeline: [{ kind: "tool", index: 0 }] };
        }
        break;
      }
      case "subagent": {
        const child = subagents?.[entry.index];
        if (child !== undefined) {
          source = child;
          fragment = { subagents: [child], timeline: [{ kind: "subagent", index: 0 }] };
        }
        break;
      }
    }
    if (fragment !== undefined) {
      const id = `entry:${index}`;
      outputs.push(cache?.node(id, entry, source, fragment) ?? { ...fragment, nodeId: id });
    }
  }
  if (
    !snapshot.error && snapshot.text &&
    !entries.some((entry) => entry.kind === "text" && entry.text === snapshot.text)
  ) {
    outputs.push(
      cache?.answerNode(snapshot.text) ?? {
        nodeId: "answer",
        timeline: [{ kind: "text", messageId: "", text: snapshot.text }],
      },
    );
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
  if (merged.text === undefined) {
    const lastText = timeline.findLast((entry) => entry.kind === "text");
    if (lastText?.kind === "text") merged.text = lastText.text;
  }
  return merged;
}
