import { assertEquals } from "@std/assert";
import {
  appendAttachmentMarkers,
  type AttachmentMarker,
  DefaultAttachmentMediaType,
  formatAttachmentMarker,
  guessAttachmentMediaType,
  MaximumMarkerNameLength,
  parseAttachmentMarkers,
  stripAttachmentMarkers,
} from "./attachments.ts";

const diagram: AttachmentMarker = {
  sha256: "a".repeat(64),
  mediaType: "image/png",
  name: "diagram.png",
};

Deno.test("format → parse round-trips a marker", () => {
  const marker = formatAttachmentMarker(diagram);
  assertEquals(
    marker,
    `[[maieutics:object sha256=${"a".repeat(64)} mime=image/png name="diagram.png"]]`,
  );
  assertEquals(parseAttachmentMarkers(marker), [diagram]);
});

Deno.test("names with quotes, backslashes, and unicode round-trip through escapes", () => {
  const tricky: AttachmentMarker = {
    sha256: "b".repeat(64),
    mediaType: "text/plain",
    name: '说"明" \\ 文档.txt',
  };
  const marker = formatAttachmentMarker(tricky);
  assertEquals(parseAttachmentMarkers(marker), [tricky]);
});

Deno.test("control characters in a name are replaced, never emitted", () => {
  const marker = formatAttachmentMarker({
    ...diagram,
    name: "line1\nline2\ttab\u0000nul.jpg",
  });
  // The emitted marker is one line with no control characters (code-point
  // comparison instead of a control-class regex).
  assertEquals(marker.includes("\n"), false);
  const hasControl = [...marker].some((ch) => ch < " " || ch === "\u007f");
  assertEquals(hasControl, false);
  assertEquals(parseAttachmentMarkers(marker)[0]?.name, "line1 line2 tab nul.jpg");
});

Deno.test("long display names are capped", () => {
  const marker = formatAttachmentMarker({
    ...diagram,
    name: "x".repeat(MaximumMarkerNameLength + 500),
  });
  const parsed = parseAttachmentMarkers(marker);
  assertEquals(parsed.length, 1);
  assertEquals(parsed[0]?.name.length, MaximumMarkerNameLength);
});

Deno.test("markers parse in order, anywhere in the text", () => {
  const second: AttachmentMarker = {
    sha256: "c".repeat(64),
    mediaType: "application/pdf",
    name: "report.pdf",
  };
  const text = [
    "look at this:",
    formatAttachmentMarker(diagram),
    "and the report:",
    formatAttachmentMarker(second),
  ].join("\n");
  assertEquals(parseAttachmentMarkers(text), [diagram, second]);
  assertEquals(
    stripAttachmentMarkers(text),
    "look at this:\nand the report:",
  );
});

Deno.test("only the canonical form is a marker: near-misses stay literal text", () => {
  const good = formatAttachmentMarker(diagram);
  const nearMisses = [
    good.replace("maieutics", "maieutic"), // wrong prefix
    good.replace(/sha256=a{64}/, `sha256=${"A".repeat(64)}`), // uppercase sha
    good.replace(/a{64}/, "a".repeat(63)), // short sha
    good.replace("mime=image/png", "mime=image\/png;ext"), // mime token overflow
    good.replace("mime=image/png ", "mime=image/png  "), // double space
    good.replace(`name="diagram.png"`, `name=diagram.png`), // unquoted name
    good.replace('name="diagram.png"', 'name="diagram.png'), // unterminated quote
    good.replace("name=", "filename="), // wrong field name
    good.slice(0, -2), // missing closing brackets
    'name="backslash\\ escape"', // raw backslash escape sequence
  ];
  for (const candidate of nearMisses) {
    assertEquals(parseAttachmentMarkers(candidate), [], `parsed a near-miss: ${candidate}`);
  }
  // Stripping near-miss text changes nothing but the tidying trim.
  assertEquals(
    stripAttachmentMarkers(`keep ${nearMisses[1]} as-is`),
    `keep ${nearMisses[1]} as-is`,
  );
});

Deno.test("strip leaves marker-free text alone", () => {
  assertEquals(stripAttachmentMarkers("plain question"), "plain question");
  assertEquals(stripAttachmentMarkers(""), "");
});

Deno.test("append places markers one per line after the text", () => {
  const marker = formatAttachmentMarker(diagram);
  assertEquals(appendAttachmentMarkers("", [marker]), marker);
  assertEquals(appendAttachmentMarkers("hello", [marker]), `hello\n${marker}`);
  // A trailing newline is not doubled.
  assertEquals(appendAttachmentMarkers("hello\n", [marker, marker]), `hello\n${marker}\n${marker}`);
  assertEquals(appendAttachmentMarkers("hello", []), "hello");
});

Deno.test("media types come from the suffix table, case-insensitive", () => {
  assertEquals(guessAttachmentMediaType("photo.PNG"), "image/png");
  assertEquals(guessAttachmentMediaType("notes.md"), "text/markdown");
  assertEquals(guessAttachmentMediaType("a.b.c.pdf"), "application/pdf");
  assertEquals(guessAttachmentMediaType("archive.tar.gz"), "application/gzip");
  assertEquals(guessAttachmentMediaType("noextension"), DefaultAttachmentMediaType);
  assertEquals(guessAttachmentMediaType("trailing."), DefaultAttachmentMediaType);
  assertEquals(guessAttachmentMediaType("unknown.xyz"), DefaultAttachmentMediaType);
});
