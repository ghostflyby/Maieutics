/** Bounded cleanup causes, flattened and deduplicated by identity across ownership layers. */
export class FailureCollector {
  #causes: unknown[] = [];
  #seen = new WeakSet<object>();
  #failed = false;
  #truncated = false;

  get hasErrors(): boolean {
    return this.#failed;
  }

  record(error: unknown): void {
    this.#failed = true;
    const visit = (cause: unknown, depth: number): void => {
      if (cause !== null && (typeof cause === "object" || typeof cause === "function")) {
        if (this.#seen.has(cause)) return;
        this.#seen.add(cause);
      } else if (this.#causes.includes(cause)) return;
      if (depth > 32 || this.#causes.length >= 128) {
        this.#truncated = true;
        return;
      }
      if (cause instanceof AggregateError && Array.isArray(cause.errors) && cause.errors.length) {
        for (const nested of cause.errors) {
          visit(nested, depth + 1);
          if (this.#truncated) break;
        }
      } else this.#causes.push(cause);
    };
    visit(error, 0);
  }

  error(message: string, options?: ErrorOptions): AggregateError {
    return new AggregateError(
      this.#causes,
      message + (this.#truncated ? " (failure details truncated)" : ""),
      options,
    );
  }
}

export function aggregateFailures(
  errors: readonly unknown[],
  message: string,
  options?: ErrorOptions,
): AggregateError {
  const failures = new FailureCollector();
  for (const error of errors) failures.record(error);
  return failures.error(message, options);
}
