import { assertEquals, assertThrows } from "@std/assert";
import {
  AdmissionRejectedError,
  AdmissionTimeoutError,
  answerAdmission,
  createAdmissionMailbox,
  waitForAdmission,
} from "./admission.ts";

Deno.test("admission mailbox accepts synchronously", () => {
  const mailbox = createAdmissionMailbox();
  answerAdmission(mailbox, () => undefined);
  assertEquals(waitForAdmission(mailbox, 1), { status: "accepted" });
});

Deno.test("admission mailbox carries rejection reason", () => {
  const mailbox = createAdmissionMailbox();
  answerAdmission(mailbox, () => "duplicate root");
  const verdict = waitForAdmission(mailbox, 1);
  assertEquals(verdict.status, "rejected");
  assertEquals(verdict.errorMessage, "duplicate root");
});

Deno.test("admission timeout is observable and typed", () => {
  const mailbox = createAdmissionMailbox();
  assertEquals(waitForAdmission(mailbox, 1).status, "timeout");
  assertThrows(() => {
    throw new AdmissionRejectedError("duplicate root");
  }, AdmissionRejectedError);
  assertThrows(() => {
    throw new AdmissionTimeoutError("timed out");
  }, AdmissionTimeoutError);
});
