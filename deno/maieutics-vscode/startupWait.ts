/** Cancel this wait while the connection owner continues to observe shared startup. */
export async function waitForStartup<T>(starting: Promise<T>, signal: AbortSignal): Promise<T> {
  signal.throwIfAborted();
  let abort = () => {};
  const cancelled = new Promise<never>((_, reject) => {
    abort = () => reject(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
  });
  try {
    return await Promise.race([starting, cancelled]);
  } finally {
    signal.removeEventListener("abort", abort);
  }
}
