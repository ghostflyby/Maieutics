import { assertEquals } from "@std/assert";
import {
  buildSkillReference,
  encodeSkillSubmission,
  forgetSkillReferences,
  referenceModelFor,
  skillNameOf,
  SkillReferenceModel,
} from "./skillReferences.ts";

const REFERENCE = "[code-review](skill://code-review)";

Deno.test("buildSkillReference rejects names outside the catalog charset", () => {
  buildSkillReference("code-review");
  assertEquals(
    buildSkillReference("code-review", "$code-review"),
    "[$code-review](skill://code-review)",
  );
  for (const bad of ["Code-Review", "-lead", "code_review", "", "a".repeat(65)]) {
    let threw = false;
    try {
      buildSkillReference(bad);
    } catch {
      threw = true;
    }
    assertEquals(threw, true, `expected rejection for "${bad}"`);
  }
});

Deno.test("skillNameOf extracts the URL host", () => {
  assertEquals(skillNameOf(REFERENCE), "code-review");
  assertEquals(skillNameOf("[任意显示文本](skill://tdd)"), "tdd");
});

Deno.test("a whole-link insertion mints; typing never does", () => {
  const model = new SkillReferenceModel();
  model.mintIfInsertion(0, REFERENCE);
  model.validate(REFERENCE);
  assertEquals(model.ranges().length, 1);

  const typed = new SkillReferenceModel();
  const parts = REFERENCE.split("");
  let offset = 0;
  for (const part of parts) {
    typed.applyChange(offset, 0, part.length);
    offset += part.length;
  }
  typed.validate(REFERENCE);
  assertEquals(typed.ranges().length, 0);
});

Deno.test("prefix edits before a reference shift it without loss", () => {
  const model = new SkillReferenceModel();
  model.mintIfInsertion(0, REFERENCE);
  const prefix = "请先看这段：";
  const text = prefix + REFERENCE;
  model.applyChange(0, 0, prefix.length);
  model.validate(text);
  assertEquals(model.ranges().length, 1);
  assertEquals(
    text.slice(model.ranges()[0].start, model.ranges()[0].end),
    REFERENCE,
  );
});

Deno.test("splitting the link degrades it; a valid re-spelling is escaped as a mention", () => {
  const urlHostAt = REFERENCE.indexOf("(skill://") + "(skill://".length;

  // A space inside the URL host breaks the link shape entirely: no escape
  // needed, the grammar already rejects it on both sides.
  const brokenModel = new SkillReferenceModel();
  const broken = "[code-review](skill://code- review)";
  brokenModel.mintIfInsertion(0, REFERENCE);
  brokenModel.applyChange(urlHostAt + 5, 0, 1);
  brokenModel.validate(broken);
  assertEquals(brokenModel.ranges().length, 0);
  assertEquals(brokenModel.encodeForSubmit(broken), broken);

  // A valid but different re-spelling (same bytes shape, different host) is
  // no longer the minted reference: it rides as a mention.
  const shiftedModel = new SkillReferenceModel();
  const shifted = "[code-review](skill://code-reviews)";
  shiftedModel.mintIfInsertion(0, REFERENCE);
  shiftedModel.applyChange(urlHostAt + "code-review".length, 0, 1);
  shiftedModel.validate(shifted);
  assertEquals(shiftedModel.ranges().length, 0);
  assertEquals(shiftedModel.encodeForSubmit(shifted), `\\${shifted}`);
});

Deno.test("encodeForSubmit passes tracked references and escapes untracked look-alikes", () => {
  const model = new SkillReferenceModel();
  const text = `${REFERENCE}\nmention: ${REFERENCE}`;
  model.mintIfInsertion(0, REFERENCE);

  const encoded = model.encodeForSubmit(text);

  assertEquals(encoded, `${REFERENCE}\nmention: \\${REFERENCE}`);
  assertEquals(model.encodeForSubmit(text), encoded);
});

Deno.test("seedFromText adopts well-formed references on reopen", () => {
  const model = new SkillReferenceModel();
  const text = `lead ${REFERENCE}`;
  model.seedFromText(text);
  model.validate(text);
  assertEquals(model.encodeForSubmit(text), text);
});

Deno.test("the uri-keyed registry encodes tracked selections; no model means kernel form default", () => {
  const key = "cell://one";
  const model = referenceModelFor(key);
  model.mintIfInsertion(0, REFERENCE);
  assertEquals(encodeSkillSubmission(key, REFERENCE), REFERENCE);

  // A cell with no boundary model (never edited since activation) rides the
  // kernel's form default: unescaped exact form = reference.
  const untouched = encodeSkillSubmission("cell://never-opened", REFERENCE);
  assertEquals(untouched, REFERENCE);

  forgetSkillReferences(key);
  assertEquals(encodeSkillSubmission(key, REFERENCE), REFERENCE);
});
