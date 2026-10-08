/**
 * The input-boundary reference model for skill references (ADR 0039 selection
 * loop, frontend half). Intent lives here, not in the text: a reference exists
 * only from the moment a completion (or a whole-link paste) mints it, and the
 * model tracks that range through subsequent edits. At submission the model
 * encodes the turn text: tracked references pass through verbatim, every
 * other link-shaped occurrence gets the mention escape (`\` before the
 * opening bracket), which the kernel consumes as "literal text, not a
 * selection". Kept free of the `vscode` module so it is unit-testable under
 * Deno; the grammar must agree exactly with the kernel's
 * `FrontendSkillMarkers`.
 *
 * The wire form is a standard markdown link over the custom protocol:
 * `[display text](skill://name)` — the URL host is the catalog name (the
 * `skill://` host charset), the link text is display metadata.
 */

export const SkillReferencePattern = /\[([^\]\n]*)\]\((skill:\/\/[a-z0-9][a-z0-9-]{0,63})\)/;
const SkillReferenceGlobal = new RegExp(SkillReferencePattern.source, "g");
const SkillReferenceWhole = new RegExp(`^${SkillReferencePattern.source}$`);

/** Builds the canonical reference; rejects names the catalog could never
 * carry (the same charset the `skill://` host enforces). */
export function buildSkillReference(name: string, display?: string): string {
  if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(name)) {
    throw new Error(`"${name}" is not a valid skill catalog name.`);
  }
  return `[${display ?? name}](skill://${name})`;
}

/** The catalog name of one reference's URL host. */
export function skillNameOf(referenceText: string): string {
  const match = SkillReferenceWhole.exec(referenceText);
  if (match === null) {
    throw new Error(`"${referenceText}" is not a skill reference.`);
  }
  return match[2].slice("skill://".length);
}

interface Tracked {
  /** The exact reference bytes the mint committed; validation is byte-exact. */
  readonly bytes: string;
  start: number;
  end: number;
}

export class SkillReferenceModel {
  #tracked: Tracked[] = [];

  /** Mints a reference when one document change inserted exactly one whole
   * link (a completion insert, or a paste of a complete reference — a paste
   * of a reference is a reference). Keystroke-by-keystroke typing never
   * matches, so hand-typed link-shaped text stays literal by construction. */
  mintIfInsertion(offset: number, insertedText: string): void {
    const match = SkillReferenceWhole.exec(insertedText);
    if (match === null) return;
    this.#tracked.push({
      bytes: match[0],
      start: offset,
      end: offset + insertedText.length,
    });
  }

  /** LSP-style position sync for one content change (UTF-16 offsets, the same
   * units as VSCode document positions). Ranges the change touched are kept
   * conservatively and settled by the next {@linkcode validate}. */
  applyChange(offset: number, removedLength: number, insertedLength: number): void {
    const delta = insertedLength - removedLength;
    const changeEnd = offset + removedLength;
    this.#tracked = this.#tracked.map((tracked): Tracked => {
      if (tracked.end <= offset) return tracked;
      if (tracked.start >= changeEnd) {
        return {
          bytes: tracked.bytes,
          start: tracked.start + delta,
          end: tracked.end + delta,
        };
      }

      // The change overlaps the reference: clamp around the edit and let
      // validate decide whether the bytes still are this reference.
      return {
        bytes: tracked.bytes,
        start: Math.min(tracked.start, offset),
        end: Math.max(offset, tracked.end + delta),
      };
    });
  }

  /** Drops tracked references whose byte range no longer parses as exactly
   * the minted link — the designed degrade of a broken reference. */
  validate(text: string): void {
    this.#tracked = this.#tracked.filter((tracked) =>
      text.slice(tracked.start, tracked.end) === tracked.bytes
    );
  }

  /** Reopen form-default: every well-formed link already in the document
   * becomes tracked (intent cannot survive plain-text serialization, so the
   * form is the best available default across sessions). */
  seedFromText(text: string): void {
    for (const match of text.matchAll(SkillReferenceGlobal)) {
      this.#tracked.push({
        bytes: match[0],
        start: match.index,
        end: match.index + match[0].length,
      });
    }
  }

  /** The tracked ranges, for the editor's chip decoration. */
  ranges(): readonly Tracked[] {
    return this.#tracked;
  }

  /** Wire encoding: tracked references pass through; every untracked
   * link-shaped occurrence gets the mention escape. Pure over the text —
   * the document never changes, so repeated submissions do not accumulate
   * escapes. */
  encodeForSubmit(text: string): string {
    const matches = [...text.matchAll(SkillReferenceGlobal)];
    if (matches.length === 0) return text;

    let output = "";
    let position = 0;
    for (const match of matches) {
      const start = match.index;
      const end = start + match[0].length;
      const tracked = this.#tracked.some((tracked) =>
        tracked.start === start && tracked.end === end
      );
      output += text.slice(position, start);
      output += tracked ? match[0] : `\\${match[0]}`;
      position = end;
    }
    output += text.slice(position);
    return output;
  }
}

/** Per-cell models keyed by the cell document URI. The extension owns the
 * lifecycle (create-and-seed on open/change, forget on close); tests key on
 * plain strings and reset via {@linkcode forgetSkillReferences}. */
const models = new Map<string, SkillReferenceModel>();

export function referenceModelFor(key: string, documentText?: string): SkillReferenceModel {
  let model = models.get(key);
  if (model === undefined) {
    model = new SkillReferenceModel();
    if (documentText !== undefined) model.seedFromText(documentText);
    models.set(key, model);
  }
  return model;
}

/** Submits with the boundary model's encoding: tracked selections pass as
 * references, everything else link-shaped rides as a mention. */
export function encodeSkillSubmission(key: string, text: string): string {
  const model = models.get(key);
  return model === undefined ? text : model.encodeForSubmit(text);
}

export function forgetSkillReferences(key: string): void {
  models.delete(key);
}
