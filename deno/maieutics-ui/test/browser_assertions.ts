export function assert(condition: unknown, mechanism: string): asserts condition {
  if (!condition) throw new Error(mechanism);
}
export function equal(actual: unknown, expected: unknown, mechanism: string): void {
  assert(actual === expected, `${mechanism}: expected ${expected}, received ${actual}`);
}
export async function guarded<T>(completion: Promise<T>, mechanism: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      completion,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(mechanism)), 15_000);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

/** A controlled fixture response must settle its owned read when its request is cancelled. */
export function abortable<T>(completion: Promise<T>, signal: AbortSignal): Promise<T> {
  signal.throwIfAborted();
  return new Promise<T>((resolve, reject) => {
    const abort = () => reject(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    completion.then((value) => {
      signal.removeEventListener("abort", abort);
      resolve(value);
    }, (error) => {
      signal.removeEventListener("abort", abort);
      reject(error);
    });
  });
}
export function geometryApplied(root: HTMLElement): boolean {
  if (root.querySelector(".mui-loading")) return false;
  const rows = [...root.querySelectorAll<HTMLElement>(".mui-row")];
  return rows.length > 0 && rows.every((row) => {
    const slot = row.parentElement;
    return slot &&
      Math.abs(slot.getBoundingClientRect().height - row.getBoundingClientRect().height) <= 1;
  });
}
export function waitFor(
  predicate: () => boolean,
  mechanism: string,
  root: Node = document.body,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      observer.disconnect();
      reject(new Error(mechanism));
    }, 15_000);
    const check = () => {
      if (predicate()) {
        clearTimeout(timer);
        observer.disconnect();
        resolve();
      }
    };
    const observer = new MutationObserver(check);
    observer.observe(root, {
      subtree: true,
      childList: true,
      attributes: true,
      characterData: true,
    });
    check();
  });
}
