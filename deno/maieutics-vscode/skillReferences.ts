/**
 * The input-boundary reference model for skill markers (ADR 0039 selection
 * loop, frontend half). Intent lives here, not in the text: a reference exists
 * only from the moment a completion (or a whole-marker paste) mints it, and
 * the model tracks that range through subsequent edits. At submission the
 * model encodes the turn text: tracked markers pass through verbatim, every
 * other marker-shaped occurrence gets the mention escape (`\` before the
 * opening brackets), which the kernel consumes as "literal text, not a
 * selection". Kept free of the `vscode` module so it is unit-testable under
 * Deno; the grammar must agree exactly with the kernel's
 * `FrontendSkillMarkers`.
 */

export const SkillMarkerPattern = /\[\[maieutics:skill name="([a-z0-9][a-z0-9-]{0,63})"\]\]/;
const SkillMarkerGlobal = new RegExp(SkillMarkerPattern.source, "g");
const SkillMarkerWhole = new RegExp(`^${SkillMarkerPattern.source}$`);

/** Builds the canonical marker; rejects names the catalog could never carry
 * (the same charset the `skill://` host enforces). */
export function buildSkillMarker(name: string): string {
  if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(name)) {
    throw new Error(`"${name}" is not a valid skill catalog name.`);
  }
  return `[[maieutics:skill name="${name}"]]`;
}

interface Tracked {
  name: string;
  start: number;
  end: number;
}

export class SkillReferenceModel {
  #tracked: Tracked[] = [];

  /** Mints a reference when one document change inserted exactly one whole
   * marker (a completion insert, or a paste of a complete marker — a paste of
   * a reference is a reference). Keystroke-by-keystroke typing never matches,
   * so hand-typed marker-shaped text stays literal by construction. */
  mintIfInsertion(offset: number, insertedText: string): void {
    const match = SkillMarkerWhole.exec(insertedText);
    if (match === null) return;
    this.#tracked.push({
      name: match[1],
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
          name: tracked.name,
          start: tracked.start + delta,
          end: tracked.end + delta,
        };
      }

      // The change overlaps the reference: clamp around the edit and let
      // validate decide whether the bytes still are this name's marker.
      return {
        name: tracked.name,
        start: Math.min(tracked.start, offset),
        end: Math.max(offset, tracked.end + delta),
      };
    });
  }

  /** Drops tracked references whose byte range no longer parses as exactly
   * that name's marker — the designed degrade of a broken reference. */
  validate(text: string): void {
    this.#tracked = this.#tracked.filter((tracked) =>
      text.slice(tracked.start, tracked.end) === buildSkillMarker(tracked.name)
    );
  }

  /** Reopen form-default: every well-formed marker already in the document
   * becomes tracked (intent cannot survive plain-text serialization, so the
   * form is the best available default across sessions). */
  seedFromText(text: string): void {
    for (const match of text.matchAll(SkillMarkerGlobal)) {
      this.#tracked.push({
        name: match[1],
        start: match.index,
        end: match.index + match[0].length,
      });
    }
  }

  /** The tracked ranges, for the editor's chip decoration. */
  ranges(): readonly Tracked[] {
    return this.#tracked;
  }

  /** Wire encoding: tracked markers pass through; every untracked
   * marker-shaped occurrence gets the mention escape. Pure over the text —
   * the document never changes, so repeated submissions do not accumulate
   * escapes. */
  encodeForSubmit(text: string): string {
    const matches = [...text.matchAll(SkillMarkerGlobal)];
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
 * markers, everything else marker-shaped rides as a mention. */
export function encodeSkillSubmission(key: string, text: string): string {
  const model = models.get(key);
  return model === undefined ? text : model.encodeForSubmit(text);
}

export function forgetSkillReferences(key: string): void {
  models.delete(key);
}
