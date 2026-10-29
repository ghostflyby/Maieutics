import { basename, resolve } from "node:path";
import type { FrontendClient } from "./client.ts";

/** Enumerate all disk projects in a VS Code multi-root workspace, preserving folder order. */
export function localProjectPaths(
  folders: readonly { uri: { scheme: string; fsPath: string } }[],
): string[] {
  return folders.filter((folder) => folder.uri.scheme === "file").map((folder) =>
    folder.uri.fsPath
  );
}

/** Connection-owned, serialized registration of editor projects in the kernel's fixed home. */
export class ProjectMounts {
  private readonly lifetime = new AbortController();
  private readonly opened = new Set<string>();
  private pending: Promise<void> = Promise.resolve();
  private disposed = false;
  private home: string | undefined;

  constructor(private readonly client: Pick<FrontendClient, "capabilities" | "executeCommand">) {}

  sync(paths: readonly string[]): Promise<void> {
    if (this.disposed) return Promise.reject(new Error("Project registration is disposed."));
    const targets = [...new Set(paths.map((path) => resolve(path)))];
    const task = this.pending.then(async () => {
      const signal = AbortSignal.any([this.lifetime.signal, AbortSignal.timeout(20_000)]);
      signal.throwIfAborted();
      if (!targets.length) return;
      this.home ??= (await this.client.capabilities(signal)).workspaceRoot;
      const failures: unknown[] = [];
      for (const path of targets) {
        signal.throwIfAborted();
        if (path === this.home || this.opened.has(path)) continue;
        try {
          if (/[\r\n]/.test(path) || path.trimEnd() !== path) {
            throw new Error("The IDE project path cannot be represented by the workspace command.");
          }
          // An explicit single-token alias disambiguates paths ending in ' as <word>'.
          // Ordinary names and collisions remain the kernel registry's responsibility.
          const ambiguous = /\s+as\s+\S+$/i.test(path);
          const alias = basename(path).replace(/[\s<>:"/\\|?*]+/g, "_");
          await this.client.executeCommand(
            `%workspace open ${path}${ambiguous ? ` as ${alias}` : ""}`,
            signal,
          );
          this.opened.add(path);
        } catch (error) {
          signal.throwIfAborted();
          failures.push(error);
        }
      }
      if (failures.length) {
        throw new AggregateError(
          failures,
          `${failures.length} IDE project registration(s) failed: ${
            failures.map(String).join("; ")
          }`,
        );
      }
    });
    // Observe failures while allowing later syncs to retry failed registrations.
    this.pending = task.catch(() => {});
    return task;
  }

  async dispose(): Promise<void> {
    this.disposed = true;
    this.lifetime.abort();
    await this.pending;
  }
}
