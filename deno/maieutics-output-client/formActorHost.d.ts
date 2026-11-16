/** Browser messages never choose their editor or pinned session. The embedding host supplies both. */
export interface FormActorHostOptions<Owner extends object> {
  session(owner: Owner): string | undefined;
  visible(owner: Owner): boolean;
  handle(owner: Owner, message: unknown): Promise<void>;
  release(owner: Owner): Promise<void>;
  post(owner: Owner, message: unknown): void | PromiseLike<unknown>;
  log(message: string): void;
}

/** A bundled, self-contained host adapter; no kernel or VS Code API dependencies. */
export declare class FormActorHost<Owner extends object> {
  constructor(options: FormActorHostOptions<Owner>);
  receive(owner: Owner, message: unknown): void;
  post(owner: Owner, message: unknown): void;
  updateVisible(owners: readonly Owner[]): void;
  releaseWhere(predicate: (owner: Owner, sessionId: string) => boolean): Promise<void>;
  dispose(): Promise<void>;
}
