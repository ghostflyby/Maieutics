/**
 * TSX cell transformer for the REPL kernel.
 *
 * The Aves REPL kernel accepts a `CodeTransform` (esbuild-compatible contract:
 * TS/JS in, ESM out). The default esbuild-wasm pipeline uses `loader: "ts"`,
 * which strips types but does not compile JSX. This transformer switches the
 * loader to `"tsx"` and routes JSX through the Maieutics widget runtime's
 * automatic runtime (`maieutics-widgets/jsx-runtime`) so a JSX element whose
 * tag is a known classic control becomes a widget model directly.
 *
 * The esbuild-wasm import uses an absolute `npm:` specifier so resolution does
 * not depend on the REPL child's config import map; the wasm payload ships in
 * the shared lock and loads lazily on the first transform.
 */

// The absolute npm: specifier is deliberate: the REPL child resolves this
// module without the config import map (see the module doc comment above).
// deno-lint-ignore no-import-prefix
import esbuildWasmCjs from "npm:esbuild-wasm@0.25.12/lib/browser.js";

const esbuildWasm = esbuildWasmCjs as unknown as {
  initialize(options: { wasmModule: WebAssembly.Module; worker: boolean }): Promise<void>;
  transform(
    code: string,
    options: { loader: string; format: string; jsx?: string; jsxImportSource?: string },
  ): Promise<{ code: string }>;
};

type TsxLoader = "ts" | "tsx";
type CellTransform = (code: string, loader: TsxLoader) => Promise<{ code: string }>;

let transformPromise: Promise<CellTransform> | null = null;

function getTransform(): Promise<CellTransform> {
  if (transformPromise === null) {
    transformPromise = (async () => {
      const dir = new URL(
        ".",
        import.meta.resolve("npm:esbuild-wasm@0.25.12/esbuild.wasm"),
      );
      const wasmBytes = await Deno.readFile(new URL("esbuild.wasm", dir));
      const wasmModule = await WebAssembly.compile(wasmBytes);
      await esbuildWasm.initialize({ wasmModule, worker: false });
      return (code: string, loader: "ts" | "tsx") =>
        esbuildWasm.transform(code, {
          loader,
          format: "esm",
          ...(loader === "tsx" ? { jsx: "automatic", jsxImportSource: "maieutics-widgets" } : {}),
        });
    })();
    transformPromise.catch(() => {
      transformPromise = null;
    });
  }
  return transformPromise;
}

/**
 * Aves `CodeTransform` that compiles TSX cells to ESM with the Maieutics
 * widget runtime's automatic JSX runtime. Meant to be passed as
 * `createReplKernel({ transform })`.
 *
 * Plain cells use the `ts` loader so existing TypeScript (including generic
 * arrow functions like `const f = <T>(x: T) => x`, which `tsx` would parse as
 * JSX) keeps working; the `tsx` loader is selected only when the cell actually
 * contains JSX.
 */
export function createTsxTransform(): (
  code: string,
  options: { loader: "ts"; format: "esm" },
) => Promise<{ code: string }> {
  return async (code) => {
    const transform = await getTransform();
    const loader = looksLikeJsx(code) ? "tsx" : "ts";
    return transform(code, loader);
  };
}

/** True when the cell text plausibly contains JSX (a `<Tag` element). */
function looksLikeJsx(code: string): boolean {
  return /<[A-Za-z][A-Za-z0-9.]*(?:\s|>)/.test(code);
}
