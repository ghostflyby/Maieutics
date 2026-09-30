/** Session run-activity state (the `run.status` busy/idle frames).
 *
 * Pure state: the controller marks busy/idle as frames arrive, the status
 * bar / sessions tree / background notifications derive from it. Kept free of
 * vscode imports so the transitions are unit-testable. */

export class SessionActivity {
  private readonly busy = new Set<string>();
  private readonly listeners = new Set<() => void>();

  /** Marks the session busy; returns whether the state changed. */
  markBusy(sessionId: string): boolean {
    if (this.busy.has(sessionId)) return false;
    this.busy.add(sessionId);
    this.fire();
    return true;
  }

  /** Marks the session idle; returns whether the state changed. */
  markIdle(sessionId: string): boolean {
    if (!this.busy.delete(sessionId)) return false;
    this.fire();
    return true;
  }

  isBusy(sessionId: string): boolean {
    return this.busy.has(sessionId);
  }

  /** Sessions with a run in flight right now (snapshot, sorted for stable
   * display and tests). */
  busySessions(): readonly string[] {
    return [...this.busy].sort();
  }

  onDidChange(listener: () => void): { dispose(): void } {
    this.listeners.add(listener);
    return {
      dispose: () => {
        this.listeners.delete(listener);
      },
    };
  }

  private fire(): void {
    for (const listener of [...this.listeners]) listener();
  }
}
