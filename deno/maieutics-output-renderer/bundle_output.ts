import { parse } from "acorn";

/** Preserve literal payload whitespace while making the committed generated file diff-checkable. */
export function normalizeBundle(source: string): string {
  const changes: { start: number; end: number; text: string }[] = [];
  const visit = (value: unknown, tagged = false): void => {
    if (Array.isArray(value)) {
      for (const child of value) visit(child, tagged);
    } else if (typeof value === "object" && value !== null) {
      const node = value as Record<string, unknown>;
      if (
        node.type === "TemplateElement" && typeof node.start === "number" &&
        typeof node.end === "number"
      ) {
        const raw = source.slice(node.start, node.end);
        const text = raw.replace(/[ \t]+(?=\r?\n)/g, (space, offset: number) => {
          if (tagged) {
            throw new Error("Tagged template payload requires explicit whitespace handling.");
          }
          let slashes = 0;
          for (let index = offset - 1; raw[index] === "\\"; index--) slashes++;
          if (slashes % 2 !== 0) {
            throw new Error("Escaped template whitespace requires explicit handling.");
          }
          return Array.from(space, (character) => character === "\t" ? "\\t" : "\\x20").join("");
        });
        if (text !== raw) changes.push({ start: node.start, end: node.end, text });
      }
      for (const [key, child] of Object.entries(node)) {
        visit(child, tagged || (node.type === "TaggedTemplateExpression" && key === "quasi"));
      }
    }
  };
  visit(parse(source, { ecmaVersion: "latest", sourceType: "module" }));
  for (const change of changes.sort((left, right) => right.start - left.start)) {
    source = source.slice(0, change.start) + change.text + source.slice(change.end);
  }
  return source.replace(/[ \t]+$/gm, "");
}

if (import.meta.main) {
  const path = "bundle/service.js";
  await Deno.writeTextFile(path, normalizeBundle(await Deno.readTextFile(path)));
}
