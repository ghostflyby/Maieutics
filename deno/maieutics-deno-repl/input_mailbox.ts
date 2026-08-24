/**
 * Blocking input mailbox: lets the REPL worker's prompt/confirm/alert recover
 * their original synchronous signatures even though the actual input round trip
 * (WebSocket -> .NET -> Jupyter stdin) is asynchronous.
 *
 * Design: the worker BLOCKS its own thread with Atomics.wait on a
 * SharedArrayBuffer, while the main thread (repl_client, whose event loop is
 * alive) performs the async round trip and writes the answer — or an interrupt
 * — back into the same buffer, then notifies. Because the two sides are
 * different threads, the wait does not deadlock the reply.
 *
 * Request direction is postMessage (synchronous, does not need the blocked
 * thread's event loop); reply direction is the SAB mailbox.
 *
 * Mailbox layout (one SharedArrayBuffer per request, shared by both sides):
 *   bytes 0-3    status:  0=pending, 1=answered, 2=interrupted
 *   bytes 4-7    kind:    1=prompt, 2=confirm, 3=alert
 *   bytes 8-11   answer:  prompt length in bytes, or 0/1 for confirm, 0 for alert
 *   bytes 12+    UTF-8 answer text (prompt only)
 *
 * The kind + answer slots are written by the answering side together with the
 * status, so a prompt answer can never be mistaken for a confirm boolean: the
 * waiting side reads the kind slot, not a shared length slot.
 */

export const INPUT_MAILBOX_TEXT_BYTES = 4096;
export const INPUT_MAILBOX_BYTES = 12 + INPUT_MAILBOX_TEXT_BYTES;
export const INPUT_MAILBOX_TIMEOUT_MS = 5 * 60_000;

export const InputMailboxStatus = {
  pending: 0,
  answered: 1,
  interrupted: 2,
} as const;

export type InputMailboxStatus = (typeof InputMailboxStatus)[keyof typeof InputMailboxStatus];

export const InputMailboxKind = {
  prompt: 1,
  confirm: 2,
  alert: 3,
} as const;

export type InputMailboxKind = (typeof InputMailboxKind)[keyof typeof InputMailboxKind];

/** Worker-side handle over one request's SharedArrayBuffer. */
export interface InputMailbox {
  /** The underlying shared buffer (to transfer to the answering thread). */
  readonly sab: SharedArrayBuffer;
  readonly status: Int32Array; // byte 0
  readonly kind: Int32Array; // byte 4
  readonly answer: Int32Array; // byte 8: prompt length / confirm bool
  readonly text: Uint8Array; // byte 12+: prompt answer text
}

/** The kind of input request, mirrored on the wire so the server knows how to reply. */
export type InputMailboxRequestKind = "prompt" | "confirm" | "alert";

/** One worker -> main-thread input request, sent over the input mailbox channel. */
export interface InputMailboxRequest {
  /** The SharedArrayBuffer this request's answer is written into. */
  sab: SharedArrayBuffer;
  kind: InputMailboxRequestKind;
  prompt: string;
}

/** Create a fresh mailbox buffer for one blocking request. */
export function createInputMailbox(): InputMailbox {
  const sab = new SharedArrayBuffer(INPUT_MAILBOX_BYTES);
  return {
    sab,
    status: new Int32Array(sab, 0, 1),
    kind: new Int32Array(sab, 4, 1),
    answer: new Int32Array(sab, 8, 1),
    text: new Uint8Array(sab, 12),
  };
}

/** Map a wire kind string to the mailbox kind code. */
export function mailboxKindCode(kind: InputMailboxRequestKind): number {
  return InputMailboxKind[kind];
}

/** The mailbox view for a buffer that arrived from the worker. */
export function mailboxFor(sab: SharedArrayBuffer): InputMailbox {
  return {
    sab,
    status: new Int32Array(sab, 0, 1),
    kind: new Int32Array(sab, 4, 1),
    answer: new Int32Array(sab, 8, 1),
    text: new Uint8Array(sab, 12),
  };
}

/** Main-thread writer: write a text answer (prompt). */
export function writeInputMailboxAnswer(
  mailbox: InputMailbox,
  kind: InputMailboxRequestKind,
  value: string,
): void {
  const bytes = new TextEncoder().encode(value);
  mailbox.text.set(bytes.subarray(0, INPUT_MAILBOX_TEXT_BYTES));
  mailbox.answer[0] = Math.min(bytes.length, INPUT_MAILBOX_TEXT_BYTES);
  mailbox.kind[0] = mailboxKindCode(kind);
  Atomics.store(mailbox.status, 0, InputMailboxStatus.answered);
  Atomics.notify(mailbox.status, 0, 1);
}

/** Main-thread writer: write a boolean answer (confirm). */
export function writeInputMailboxBoolean(
  mailbox: InputMailbox,
  value: boolean,
): void {
  mailbox.kind[0] = InputMailboxKind.confirm;
  mailbox.answer[0] = value ? 1 : 0;
  Atomics.store(mailbox.status, 0, InputMailboxStatus.answered);
  Atomics.notify(mailbox.status, 0, 1);
}

/** Main-thread writer: acknowledge a plain alert (no answer value). */
export function writeInputMailboxAck(mailbox: InputMailbox): void {
  mailbox.kind[0] = InputMailboxKind.alert;
  mailbox.answer[0] = 0;
  Atomics.store(mailbox.status, 0, InputMailboxStatus.answered);
  Atomics.notify(mailbox.status, 0, 1);
}

/** Main-thread writer: interrupt a pending input (status=2 wakes the worker early). */
export function interruptInputMailbox(mailbox: InputMailbox): void {
  Atomics.store(mailbox.status, 0, InputMailboxStatus.interrupted);
  Atomics.notify(mailbox.status, 0, 1);
}

/** Main-thread writer: fail a pending input that can never be answered (status=3). */
export function failInputMailbox(mailbox: InputMailbox, error: unknown): void {
  const message = error instanceof Error ? error.message : String(error);
  const bytes = new TextEncoder().encode(message);
  mailbox.text.set(bytes.subarray(0, INPUT_MAILBOX_TEXT_BYTES));
  mailbox.answer[0] = Math.min(bytes.length, INPUT_MAILBOX_TEXT_BYTES);
  mailbox.kind[0] = InputMailboxKind.prompt;
  Atomics.store(mailbox.status, 0, 3);
  Atomics.notify(mailbox.status, 0, 1);
}

/** Worker-side reader: block until the answer, an interrupt, the timeout, or an error. */
export function waitForInputMailbox(
  mailbox: InputMailbox,
  timeoutMs: number = INPUT_MAILBOX_TIMEOUT_MS,
): {
  status: InputMailboxStatus | 3;
  answer?: string;
  ok?: boolean;
  error?: string;
} {
  const result = Atomics.wait(mailbox.status, 0, InputMailboxStatus.pending, timeoutMs);
  const state = Atomics.load(mailbox.status, 0);
  if (result === "timed-out") {
    return { status: InputMailboxStatus.pending };
  }
  switch (state) {
    case InputMailboxStatus.interrupted:
      return { status: InputMailboxStatus.interrupted };
    case 3:
      return {
        status: 3,
        error: new TextDecoder().decode(mailbox.text.subarray(0, mailbox.answer[0])),
      };
    case InputMailboxStatus.answered:
      switch (mailbox.kind[0]) {
        case InputMailboxKind.prompt:
          return {
            status: InputMailboxStatus.answered,
            answer: new TextDecoder().decode(
              mailbox.text.subarray(0, mailbox.answer[0]),
            ),
          };
        case InputMailboxKind.confirm:
          return { status: InputMailboxStatus.answered, ok: mailbox.answer[0] === 1 };
        case InputMailboxKind.alert:
          return { status: InputMailboxStatus.answered };
        default:
          return { status: InputMailboxStatus.answered };
      }
    default:
      return { status: InputMailboxStatus.pending };
  }
}
