/**
 * Admission handshake for remote extension-point contributions (ADR 0021
 * decision 9): a contract's aggregator may install a synchronous hook that
 * decides whether a `provide()` contribution is accepted. The provider blocks
 * its thread on a SharedArrayBuffer until the aggregator's verdict lands, so
 * a rejection throws at the `provide()` call site — a normal local validation
 * failure — instead of an asynchronous unhandled rejection that would leave
 * the module half-registered.
 *
 * The mechanism mirrors the REPL's blocking input mailbox
 * (`maieutics-deno-repl/input_mailbox.ts`): the request direction is a bare
 * `postMessage` frame (synchronous, does not need the blocked thread's event
 * loop), the reply direction is the shared buffer, and the wait is bounded so
 * a dead or busy aggregator fails closed instead of wedging module evaluation
 * forever.
 *
 * Hook contract: hooks are synchronous and fast — no I/O, no long loops, and
 * never a call into the providing worker (its thread is parked; a callback
 * would deadlock until the timeout). Verdicts: return `void` to accept,
 * return a string to reject with a reason, or throw to reject with that
 * error (`name` + `message` cross the isolate; stacks do not).
 *
 * Mailbox layout (one SharedArrayBuffer per provide attempt):
 *   bytes 0-3   status: 0=pending, 1=accepted, 2=rejected
 *   bytes 4-7   error name length (rejected only)
 *   bytes 8-11  error message length
 *   bytes 12+   UTF-8 error name, then UTF-8 error message (contiguous)
 */

export const ADMISSION_TEXT_BYTES = 4096;
export const ADMISSION_BYTES = 12 + ADMISSION_TEXT_BYTES;

/** Bounded wait for the aggregator's verdict; a timeout fails closed. */
export const ADMISSION_TIMEOUT_MS = 15_000;

export const AdmissionStatus = {
  pending: 0,
  accepted: 1,
  rejected: 2,
} as const;

export interface AdmissionMailbox {
  readonly sab: SharedArrayBuffer;
  readonly status: Int32Array; // byte 0
  readonly nameLen: Int32Array; // byte 4
  readonly msgLen: Int32Array; // byte 8
  readonly text: Uint8Array; // byte 12+
}

/** The wire frame a provider posts to its parent before blocking. */
export interface AdmissionRequestFrame {
  type: "__admit";
  /** Extension point name the contribution targets. */
  ep: string;
  /** Defining-worker specifier of the contract (routing the relay). */
  def: string;
  /** The provider's contribution key (`<specifier>:<uuid>`). */
  providerKey: string;
  /** ES module URL of the providing code (via the SDK load hook). */
  providerModule: string;
  /** The shared verdict buffer. */
  sab: SharedArrayBuffer;
}

/** Context handed to an admission hook for one contribution. */
export interface AdmissionContext {
  readonly extensionPoint: string;
  /** Canonical specifier of the contributing worker. */
  readonly providerSpecifier: string;
  /** ES module URL of the providing code. */
  readonly providerModule: string;
  /** Specifiers of the contract's current live contributors. */
  readonly existingProviders: readonly string[];
}

/** A synchronous, fast admission policy. See the module contract above. */
export type AdmissionHook = (context: AdmissionContext) => void | string;

/** Rejection carrying an aggregator-supplied reason. */
export class AdmissionRejectedError extends Error {
  override name = "AdmissionRejected";
}

/** The aggregator never answered within the bounded wait; fails closed. */
export class AdmissionTimeoutError extends Error {
  override name = "AdmissionTimeout";
}

export function createAdmissionMailbox(): AdmissionMailbox {
  const sab = new SharedArrayBuffer(ADMISSION_BYTES);
  return {
    sab,
    status: new Int32Array(sab, 0, 1),
    nameLen: new Int32Array(sab, 4, 1),
    msgLen: new Int32Array(sab, 8, 1),
    text: new Uint8Array(sab, 12),
  };
}

export function admissionMailboxFor(sab: SharedArrayBuffer): AdmissionMailbox {
  return {
    sab,
    status: new Int32Array(sab, 0, 1),
    nameLen: new Int32Array(sab, 4, 1),
    msgLen: new Int32Array(sab, 8, 1),
    text: new Uint8Array(sab, 12),
  };
}

/** Aggregator-side writer: accept the contribution. */
export function acceptAdmission(mailbox: AdmissionMailbox): void {
  Atomics.store(mailbox.status, 0, AdmissionStatus.accepted);
  Atomics.notify(mailbox.status, 0, 1);
}

/** Aggregator-side writer: reject with a cross-isolate error. */
export function rejectAdmission(mailbox: AdmissionMailbox, error: Error): void {
  const name = new TextEncoder().encode(error.name || "Error");
  const message = new TextEncoder().encode(error.message);
  mailbox.nameLen[0] = Math.min(name.length, 256);
  mailbox.text.set(name.subarray(0, mailbox.nameLen[0]));
  const messageStart = mailbox.nameLen[0];
  const messageSpace = ADMISSION_TEXT_BYTES - messageStart;
  mailbox.msgLen[0] = Math.min(message.length, messageSpace);
  mailbox.text.set(message.subarray(0, mailbox.msgLen[0]), messageStart);
  Atomics.store(mailbox.status, 0, AdmissionStatus.rejected);
  Atomics.notify(mailbox.status, 0, 1);
}

export interface AdmissionVerdict {
  status: "accepted" | "rejected" | "timeout";
  errorName?: string;
  errorMessage?: string;
}

/** Provider-side reader: block this thread until the verdict or the timeout. */
export function waitForAdmission(
  mailbox: AdmissionMailbox,
  timeoutMs: number = ADMISSION_TIMEOUT_MS,
): AdmissionVerdict {
  const result = Atomics.wait(
    mailbox.status,
    0,
    AdmissionStatus.pending,
    timeoutMs,
  );
  if (result === "timed-out") return { status: "timeout" };
  const state = Atomics.load(mailbox.status, 0);
  if (state === AdmissionStatus.rejected) {
    const nameLen = mailbox.nameLen[0];
    const msgLen = mailbox.msgLen[0];
    const name = new TextDecoder().decode(mailbox.text.subarray(0, nameLen));
    const message = new TextDecoder().decode(
      mailbox.text.subarray(nameLen, nameLen + msgLen),
    );
    return { status: "rejected", errorName: name, errorMessage: message };
  }
  return { status: "accepted" };
}

/**
 * Provider-side handshake: post the admission frame to the parent (the
 * aggregator, or the host relaying to it) and block until the verdict.
 * Throws `AdmissionRejectedError` (or the aggregator's own error type) in
 * place on rejection, `AdmissionTimeoutError` when nothing answers in time.
 */
export function requestAdmission(
  request: Omit<AdmissionRequestFrame, "type" | "sab">,
  timeoutMs: number = ADMISSION_TIMEOUT_MS,
): void {
  const mailbox = createAdmissionMailbox();
  (self as unknown as { postMessage(message: unknown): void }).postMessage({
    type: "__admit",
    ...request,
    sab: mailbox.sab,
  });
  const verdict = waitForAdmission(mailbox, timeoutMs);
  if (verdict.status === "timeout") {
    throw new AdmissionTimeoutError(
      `The aggregator did not answer the admission of '${request.ep}' within ${timeoutMs} ms.`,
    );
  }
  if (verdict.status === "rejected") {
    const name = verdict.errorName ?? "AdmissionRejected";
    if (name === "AdmissionRejected") {
      throw new AdmissionRejectedError(
        verdict.errorMessage ?? "The contribution was rejected.",
      );
    }
    const error = new Error(verdict.errorMessage ?? "The contribution was rejected.");
    error.name = name;
    throw error;
  }
}

/**
 * Aggregator-side evaluation: run the hook synchronously and write the
 * verdict into the mailbox. Accepts on `void`, rejects on a returned string
 * (reason) or on a thrown error.
 */
export function answerAdmission(
  mailbox: AdmissionMailbox,
  run: () => void | string,
): void {
  try {
    const verdict = run();
    if (typeof verdict === "string") {
      rejectAdmission(mailbox, new AdmissionRejectedError(verdict));
      return;
    }
    acceptAdmission(mailbox);
  } catch (error) {
    rejectAdmission(
      mailbox,
      error instanceof Error ? error : new Error(String(error)),
    );
  }
}
