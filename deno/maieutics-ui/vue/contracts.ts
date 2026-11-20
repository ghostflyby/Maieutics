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

/** A projection or gesture correction applied its scroll write; unloaded anchors have no entry ID yet. */
export interface ConversationAnchorRestoration {
  readonly reason: "projection" | "gesture";
  readonly ordinal: number;
  readonly entryId?: string;
  readonly offset: number;
}
