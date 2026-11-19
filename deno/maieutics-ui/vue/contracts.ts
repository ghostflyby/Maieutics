/** Browser-only host capabilities. Credentials and native handles stay outside the component. */
export interface ConversationHost {
  writeText(text: string): Promise<void>;
}
/** The draft snapshot remains provisional until the host confirms acceptance. */
export interface ConversationSubmission {
  readonly viewId: string;
  readonly text: string;
  readonly revision: number;
}
