/** Copy only shipped passive assets beside the bundled browser modules. */
await Deno.mkdir(new URL("dist/", import.meta.url), { recursive: true });
for (const name of ["index.html", "styles.css"]) {
  await Deno.copyFile(
    new URL(`demo/${name}`, import.meta.url),
    new URL(`dist/${name}`, import.meta.url),
  );
}
