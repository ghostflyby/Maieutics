/// <reference lib="deno.ns" />
import { assertEquals, assertStrictEquals } from "@std/assert";
import {
  approvalAnswer,
  approvalChoices,
  approvalTitle,
  isApprovalRequest,
  planApproval,
} from "./approval.ts";

Deno.test("only an explicit approval kind is an approval request", () => {
  assertStrictEquals(isApprovalRequest("approval"), true);
  // An elicitation must never be misread as an approval, and a server predating
  // the discriminator sends no kind at all.
  assertStrictEquals(isApprovalRequest("elicitation"), false);
  assertStrictEquals(isApprovalRequest("input"), false);
  assertStrictEquals(isApprovalRequest(undefined), false);
  assertStrictEquals(isApprovalRequest(null), false);
  assertStrictEquals(isApprovalRequest(1), false);
});

Deno.test("a plan carries the tool name, reason, and argument context", () => {
  const plan = planApproval(
    "Allow 'remove_file' to run?",
    "remove_file",
    "It may destroy data.",
    { type: "object", properties: {}, description: '{"path":"/tmp/x"}' },
  );

  assertEquals(plan.usable, true);
  assertEquals(plan.toolName, "remove_file");
  assertEquals(plan.reason, "It may destroy data.");
  assertEquals(plan.argumentContext, '{"path":"/tmp/x"}');
});

Deno.test("a plan without a tool name is unusable", () => {
  // There is no sensible free-text answer to "may this run?", so the caller denies
  // rather than opening an input box.
  for (const toolName of [undefined, null, "", "   ", 42, {}]) {
    assertEquals(planApproval("Allow?", toolName, undefined, undefined).usable, false);
  }
});

Deno.test("an absent or empty reason and argument context are omitted", () => {
  const plan = planApproval("Allow?", "tool", "   ", { description: "  " });
  assertEquals(plan.reason, undefined);
  assertEquals(plan.argumentContext, undefined);

  // The server sends `{}` when the arguments were empty or too large to include.
  assertEquals(
    planApproval("Allow?", "tool", undefined, { description: "{}" }).argumentContext,
    undefined,
  );
  assertEquals(planApproval("Allow?", "tool", undefined, undefined).argumentContext, undefined);
  assertEquals(
    planApproval("Allow?", "tool", undefined, "not-an-object").argumentContext,
    undefined,
  );
});

Deno.test("an oversized argument context is truncated rather than rendered whole", () => {
  const long = "x".repeat(4000);
  const plan = planApproval("Allow?", "tool", undefined, { description: long });
  assertEquals(plan.argumentContext?.length, 2049);
  assertEquals(plan.argumentContext?.endsWith("…"), true);
});

Deno.test("the choices expose all four decisions with allow-once first", () => {
  const plan = planApproval("Allow?", "remove_file", undefined, undefined);
  const choices = approvalChoices(plan);

  assertEquals(choices.map((choice) => choice.action), ["accept", "always", "decline", "cancel"]);
  // Every label names the tool, so a queued pick is never ambiguous about what it approves.
  assertEquals(choices[1].description.includes("remove_file"), true);
  assertEquals(choices[1].description.includes("rest of this session"), true);
});

Deno.test("the title carries the prompt, reason, and argument context", () => {
  const plan = planApproval(
    "Allow 'remove_file' to run?",
    "remove_file",
    "It may destroy data.",
    { description: '{"path":"/tmp/x"}' },
  );
  const title = approvalTitle(plan);

  assertEquals(title.includes("Allow 'remove_file' to run?"), true);
  assertEquals(title.includes("It may destroy data."), true);
  assertEquals(title.includes('{"path":"/tmp/x"}'), true);
});

Deno.test("a reason already embedded in the prompt is not repeated", () => {
  // The server composes the prompt from the reason, so repeating it would double the text.
  const plan = planApproval(
    "Allow 'x'? It may destroy data.",
    "x",
    "It may destroy data.",
    undefined,
  );
  const occurrences = approvalTitle(plan).split("It may destroy data.").length - 1;
  assertEquals(occurrences, 1);
});

Deno.test("a dismissed dialog cancels rather than allowing", () => {
  // The kernel treats every non-accept action as a denial, so an abandoned dialog
  // must never be read as consent.
  assertEquals(approvalAnswer(undefined), { value: "", action: "cancel" });
  assertEquals(approvalAnswer("cancel"), { value: "", action: "cancel" });
});

Deno.test("each action maps to itself with an empty value", () => {
  for (const action of ["accept", "always", "decline", "cancel"] as const) {
    assertEquals(approvalAnswer(action), { value: "", action });
  }
});
