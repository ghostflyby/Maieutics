/** Copy only shipped passive assets beside the bundled browser modules. */
await Deno.mkdir(new URL("dist/", import.meta.url), { recursive: true });
for (
  const [source, target] of [
    ["demo/index.html", "index.html"],
    ["styles.css", "styles.css"],
    ["demo/styles.css", "demo.css"],
  ]
) {
  await Deno.copyFile(
    new URL(source, import.meta.url),
    new URL(`dist/${target}`, import.meta.url),
  );
}
