import { renderMermaidSVG } from "beautiful-mermaid";

const MaximumDiagrams = 8;
const MaximumSourceUnits = 8_192;
const MaximumStatements = 128;
const MaximumImageUrlUnits = 128_000;
const MaximumImageUrlUnitsPerSnapshot = 192_000;

/** Produce an image URL so diagram output cannot participate in the host DOM. */
export function renderMermaid(
  source: string,
  env: { mermaidCount?: number; mermaidImageUnits?: number },
): string | undefined {
  if (
    source.length > MaximumSourceUnits ||
    source.split(/[;\n]/, MaximumStatements + 2).length > MaximumStatements + 1 ||
    (env.mermaidCount ?? 0) >= MaximumDiagrams
  ) return undefined;
  env.mermaidCount = (env.mermaidCount ?? 0) + 1;

  try {
    const svg = renderMermaidSVG(source, {
      bg: "var(--diagram-bg,#fff)",
      fg: "var(--diagram-fg,#27272a)",
      font: "system-ui",
    });
    // The library can return a zero-sized SVG for a diagram without drawable content.
    const viewBox = /^<svg\b[^>]* viewBox="0 0 ([\d.]+) ([\d.]+)"/.exec(svg);
    if (!viewBox || !(Number(viewBox[1]) > 0 && Number(viewBox[2]) > 0)) return undefined;

    const offlineSvg = svg
      .replace(/@import url\('https:\/\/fonts\.googleapis\.com\/[^'\r\n]*'\);/g, "")
      .replace(
        "<style>",
        "<style>@media(prefers-color-scheme:dark){svg{--diagram-bg:#18181b;--diagram-fg:#f4f4f5}}",
      );
    const url = `data:image/svg+xml,${encodeURIComponent(offlineSvg)}`;
    const imageUnits = (env.mermaidImageUnits ?? 0) + url.length;
    if (url.length > MaximumImageUrlUnits || imageUnits > MaximumImageUrlUnitsPerSnapshot) {
      return undefined;
    }
    env.mermaidImageUnits = imageUnits;
    return url;
  } catch {
    return undefined;
  }
}
