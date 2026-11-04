import { assertEquals, assertThrows } from "@std/assert";
import { normalizeBundle } from "./bundle_output.ts";

Deno.test("bundle normalization preserves template values and refuses changes to tagged raw strings", () => {
  const source = "return `a \t\nb${1 + 2}  \nc`; // comment  \n";
  const normalized = normalizeBundle(source.replace("return ", "const result = "));
  assertEquals(Function(source)(), Function(normalized + ";return result;")());
  assertEquals(normalized.split("\n").some((line) => /[ \t]$/.test(line)), false);
  assertThrows(() => normalizeBundle("const x = String.raw`a \n`;"));
});
