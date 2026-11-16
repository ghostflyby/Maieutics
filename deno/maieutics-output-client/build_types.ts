/** Ship the implementation's declarations and the bundled runtime's original attribution. */
await Deno.copyFile(
  new URL("./types.d.ts", import.meta.url),
  new URL("./dist/outputClient.d.ts", import.meta.url),
);
await Deno.copyFile(
  new URL("./formActorHost.d.ts", import.meta.url),
  new URL("./dist/formActorHost.d.ts", import.meta.url),
);
for (const name of ["LICENSE", "NOTICE"]) {
  await Deno.copyFile(
    new URL(`../maieutics-plugin-sdk/actor-runtime/${name}`, import.meta.url),
    new URL(`./dist/actor-runtime.${name}`, import.meta.url),
  );
}
