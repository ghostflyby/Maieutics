/**
 * Multimodal attachments: the client half of the turn object-reference
 * grammar (docs/web-frontend-protocol.md, "Attachments"). Files picked onto a
 * notebook cell upload into the session's object library (`POST
 * /v1/agent/sessions/{sid}/objects`) and the cell text then carries one
 * marker line per object; markers ride the submitted text verbatim — through
 * the queue, the turn binding, stale detection, and save/reopen — and the
 * server parses the same grammar into the turn's structured data parts.
 *
 * The grammar (both sides must agree exactly):
 *
 *     [[maieutics:object sha256=<64 lowercase hex> mime=<type/subtype> name="<escaped>"]]
 *
 * - fields are fixed-order (sha256, mime, name), separated by exactly one
 *   space; `name` is double-quoted and may contain any printable character
 *   except `"` and `\`, which travel as the escapes `\"` and `\\`.
 * - parsing is strict: only the exact canonical form is a marker — anything
 *   else (uppercase sha, reordered or missing fields, stray whitespace, bad
 *   mime, unterminated quote) is ordinary text and must stay literal, so
 *   user-written look-alikes can never become references by accident and an
 *   old server receiving markers sees inert text, not a crash.
 */

// The grammar bans control characters from marker names (a marker is always
// one printable line), so the regexes below spell control ranges on purpose.
// deno-lint-ignore-file no-control-regex

/** One attachment referenced from a cell's turn text. */
export interface AttachmentMarker {
  /** Content address of the object in the session's object library. */
  sha256: string;
  /** The object's media type as uploaded. */
  mediaType: string;
  /** Display name (the uploaded file's base name). */
  name: string;
}

/**
 * The strict marker grammar. A global regex: iterate with matchAll, never
 * exec/test (lastIndex state).
 */
const MarkerPattern =
  /\[\[maieutics:object sha256=([0-9a-f]{64}) mime=([A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*\/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]*) name="((?:[^"\\\u0000-\u001f\u007f]|\\["\\])*)"\]\]/g;

/** Display names longer than this are capped on emission (bounded marker
 * lines; the wire field the server sees stays small too). */
export const MaximumMarkerNameLength = 200;

/** Parses every well-formed marker in `text`, in order of appearance.
 * Position-independent: a marker is recognized anywhere in the text, though
 * emission always writes them one per line. */
export function parseAttachmentMarkers(text: string): AttachmentMarker[] {
  const markers: AttachmentMarker[] = [];
  for (const match of text.matchAll(MarkerPattern)) {
    markers.push({
      sha256: match[1],
      mediaType: match[2],
      name: unescapeMarkerName(match[3]),
    });
  }
  return markers;
}

/** Removes well-formed markers and collapses the blank lines their removal
 * leaves behind — a display-only projection (previews, tooltips); it never
 * mutates the cell text that gets submitted. */
export function stripAttachmentMarkers(text: string): string {
  return text
    .replace(MarkerPattern, "")
    .replace(/\n{2,}/g, "\n")
    .trim();
}

/** Appends marker lines after the cell text: markers always ride at the end,
 * one per line, separated from the text by exactly one newline (a trailing
 * newline in the text is not doubled). */
export function appendAttachmentMarkers(text: string, markers: readonly string[]): string {
  if (markers.length === 0) return text;
  const separator = text.length === 0 || text.endsWith("\n") ? "" : "\n";
  return `${text}${separator}${markers.join("\n")}`;
}

/** Formats one canonical marker line. */
export function formatAttachmentMarker(marker: AttachmentMarker): string {
  return `[[maieutics:object sha256=${marker.sha256} mime=${marker.mediaType} name="${
    escapeMarkerName(marker.name)
  }"]]`;
}

/** Escapes a display name for the marker's quoted field: control characters
 * (including newlines) become spaces, then backslash and quote travel as
 * `\\` and `\"`; the result is capped at {@link MaximumMarkerNameLength}. */
function escapeMarkerName(name: string): string {
  const printable = name.replace(/[\u0000-\u001f\u007f]+/g, " ").trim();
  const capped = printable.length > MaximumMarkerNameLength
    ? printable.slice(0, MaximumMarkerNameLength)
    : printable;
  return capped.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
}

/** Reverses {@link escapeMarkerName}'s quote/backslash escapes. */
function unescapeMarkerName(name: string): string {
  return name.replace(/\\(["\\])/g, "$1");
}

/**
 * A small suffix → media type table for files picked as attachments. It only
 * needs to be right for the common cases: an unknown suffix degrades to
 * `application/octet-stream` and the server stores the bytes regardless.
 */
const MediaTypesBySuffix: Record<string, string> = {
  avif: "image/avif",
  bmp: "image/bmp",
  csv: "text/csv",
  docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  flac: "audio/flac",
  gif: "image/gif",
  gz: "application/gzip",
  htm: "text/html",
  html: "text/html",
  ico: "image/x-icon",
  jpeg: "image/jpeg",
  jpg: "image/jpeg",
  json: "application/json",
  m4a: "audio/mp4",
  md: "text/markdown",
  mov: "video/quicktime",
  mp3: "audio/mpeg",
  mp4: "video/mp4",
  oga: "audio/ogg",
  ogg: "audio/ogg",
  pdf: "application/pdf",
  png: "image/png",
  pptx: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  svg: "image/svg+xml",
  tar: "application/x-tar",
  txt: "text/plain",
  wav: "audio/wav",
  webm: "video/webm",
  webp: "image/webp",
  wasm: "application/wasm",
  xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  zip: "application/zip",
};

/** The fallback media type for files with no recognized suffix. */
export const DefaultAttachmentMediaType = "application/octet-stream";

/** Guesses a file's media type from its name (case-insensitive suffix);
 * unknown suffixes answer {@link DefaultAttachmentMediaType}. */
export function guessAttachmentMediaType(fileName: string): string {
  const dot = fileName.lastIndexOf(".");
  if (dot < 0 || dot === fileName.length - 1) return DefaultAttachmentMediaType;
  return MediaTypesBySuffix[fileName.slice(dot + 1).toLowerCase()] ?? DefaultAttachmentMediaType;
}
