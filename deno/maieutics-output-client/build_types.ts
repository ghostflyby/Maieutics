/** Ship declarations from the same public contract consumed by the implementation. */
await Deno.copyFile(
  new URL("./types.d.ts", import.meta.url),
  new URL("./dist/outputClient.d.ts", import.meta.url),
);
