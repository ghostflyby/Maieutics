import { assertEquals } from "@std/assert";
import {
  buildSkillMarker,
  encodeSkillSubmission,
  forgetSkillReferences,
  referenceModelFor,
  SkillReferenceModel,
} from "./skillReferences.ts";

const MARKER = '[[maieutics:skill name="code-review"]]';

Deno.test("buildSkillMarker rejects names outside the catalog charset", () => {
  buildSkillMarker("code-review");
  for (const bad of ["Code-Review", "-lead", "code_review", "", "a".repeat(65)]) {
    let threw = false;
    try {
      buildSkillMarker(bad);
    } catch {
      threw = true;
    }
    assertEquals(threw, true, `expected rejection for "${bad}"`);
  }
});

Deno.test("a whole-marker insertion mints; typing never does", () => {
  const model = new SkillReferenceModel();
  model.mintIfInsertion(0, MARKER);
  model.validate(MARKER);
  assertEquals(model.ranges().length, 1);

  const typed = new SkillReferenceModel();
  // Keystroke-by-keystroke typing inserts fragments; nothing whole, no mint.
  const parts = MARKER.split("");
  let offset = 0;
  for (const part of parts) {
    typed.applyChange(offset, 0, part.length);
    offset += part.length;
  }
  typed.validate(MARKER);
  assertEquals(typed.ranges().length, 0);
});

Deno.test("prefix edits before a marker shift it without loss", () => {
  const model = new SkillReferenceModel();
  model.mintIfInsertion(0, MARKER);
  const prefix = "请先看这段：";
  const text = prefix + MARKER;
  // The prefix insertion happened before the marker: shift by its length.
  model.applyChange(0, 0, prefix.length);
  model.validate(text);
  assertEquals(model.ranges().length, 1);
  assertEquals(
    text.slice(model.ranges()[0].start, model.ranges()[0].end),
    MARKER,
  );
});

Deno.test("splitting the name inside the marker degrades it to text", () => {
  const model = new SkillReferenceModel();
  const broken = '[[maieutics:skill name="code-re view"]]';
  model.mintIfInsertion(0, MARKER);
  // The edit breaks the name with a space inside the marker.
  model.applyChange(36, 0, 1);
  model.validate(broken);
  assertEquals(model.ranges().length, 0);
  // A space breaks the marker SHAPE itself: the text is no longer marker-form
  // at all (the kernel's grammar rejects it too), so encoding is a no-op —
  // the degrade already happened at the grammar.
  assertEquals(
    model.encodeForSubmit(broken),
    broken,
  );
  // A still-marker-shaped sibling of the same edit WOULD be escaped: the
  // name "code-review" becomes the valid but different "codex-review", the
  // tracked entry drops (its bytes are gone), and the occurrence is no
  // longer a minted selection.
  const shifted = '[[maieutics:skill name="codex-review"]]';
  const nameAt = MARKER.indexOf("code-review");
  const shiftedModel = new SkillReferenceModel();
  shiftedModel.mintIfInsertion(0, MARKER);
  shiftedModel.applyChange(nameAt + 4, 0, 1);
  shiftedModel.validate(shifted);
  assertEquals(
    shiftedModel.encodeForSubmit(shifted),
    `\\${shifted}`,
  );
});

Deno.test("encodeForSubmit passes tracked markers and escapes untracked look-alikes", () => {
  const model = new SkillReferenceModel();
  const text = `${MARKER}\nmention: ${MARKER}`;
  // Both occurrences are byte-identical; the model tracks only the first.
  model.mintIfInsertion(0, MARKER);
  // The second occurrence arrived as a separate untracked event: it exists in
  // the text but was never minted, so seedFromText is NOT called here.

  const encoded = model.encodeForSubmit(text);

  assertEquals(encoded, `${MARKER}\nmention: \\${MARKER}`);
  // Pure over the text: re-encoding the same document text is stable.
  assertEquals(model.encodeForSubmit(text), encoded);
});

Deno.test("seedFromText adopts well-formed markers on reopen", () => {
  const model = new SkillReferenceModel();
  const text = `lead ${MARKER}`;
  model.seedFromText(text);
  model.validate(text);
  assertEquals(model.encodeForSubmit(text), text);
});

Deno.test("the uri-keyed registry encodes tracked selections; no model means kernel form default", () => {
  const key = "cell://one";
  const model = referenceModelFor(key);
  model.mintIfInsertion(0, MARKER);
  assertEquals(encodeSkillSubmission(key, MARKER), MARKER);

  // A cell with no boundary model (never edited since activation) rides the
  // kernel's form default: unescaped exact form = reference.
  const untouched = encodeSkillSubmission("cell://never-opened", MARKER);
  assertEquals(untouched, MARKER);

  forgetSkillReferences(key);
  assertEquals(encodeSkillSubmission(key, MARKER), MARKER);
});
