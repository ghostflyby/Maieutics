// Unit tests for the blocking input mailbox protocol: the worker side blocks
// in Atomics.wait while the answering side writes answers / interrupts /
// errors into the shared buffer.

import { assertEquals, assertThrows } from "@std/assert";
import {
  createInputMailbox,
  failInputMailbox,
  InputMailboxStatus,
  interruptInputMailbox,
  mailboxFor,
  waitForInputMailbox,
  writeInputMailboxAnswer,
  writeInputMailboxBoolean,
} from "./input_mailbox.ts";

Deno.test("mailbox round-trips a prompt answer with text", () => {
  const mailbox = createInputMailbox();
  writeInputMailboxAnswer(mailbox, "prompt", "hello");
  const result = waitForInputMailbox(mailbox, 100);
  assertEquals(result.status, InputMailboxStatus.answered);
  assertEquals(result.answer, "hello");
});

Deno.test("mailbox round-trips a confirm boolean", () => {
  const mailbox = createInputMailbox();
  writeInputMailboxBoolean(mailbox, true);
  const result = waitForInputMailbox(mailbox, 100);
  assertEquals(result.status, InputMailboxStatus.answered);
  assertEquals(result.ok, true);
});

Deno.test("mailbox interrupt wakes the waiter with the interrupted status", () => {
  const mailbox = createInputMailbox();
  // Simulate the answering side interrupting a pending request.
  interruptInputMailbox(mailbox);
  const result = waitForInputMailbox(mailbox, 100);
  assertEquals(result.status, InputMailboxStatus.interrupted);
});

Deno.test("mailbox error surfaces the failure message", () => {
  const mailbox = createInputMailbox();
  failInputMailbox(mailbox, new Error("socket closed"));
  const result = waitForInputMailbox(mailbox, 100);
  assertEquals(result.status, 3);
  assertEquals(result.error, "socket closed");
});

Deno.test("mailbox wait times out when nothing arrives", () => {
  const mailbox = createInputMailbox();
  const result = waitForInputMailbox(mailbox, 50);
  assertEquals(result.status, InputMailboxStatus.pending);
});

Deno.test("mailboxFor views a transferred buffer with the same layout", () => {
  const mailbox = createInputMailbox();
  // The worker sends the bare buffer over the link; the answering side re-views it.
  const remote = mailboxFor(mailbox.sab);
  writeInputMailboxAnswer(remote, "prompt", "world");
  const result = waitForInputMailbox(mailbox, 100);
  assertEquals(result.answer, "world");
});
