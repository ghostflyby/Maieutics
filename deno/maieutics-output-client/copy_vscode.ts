/** Package the same standalone artifact in the notebook extension; never fork its implementation. */
const source = new URL("./dist/outputClient.js", import.meta.url);
const target = new URL("../maieutics-vscode/media/outputClient.js", import.meta.url);
await Deno.copyFile(source, target);
const hostDirectory = new URL("../maieutics-vscode/runtime/", import.meta.url);
await Deno.mkdir(hostDirectory, { recursive: true });
await Deno.copyFile(
  new URL("./dist/formActorHost.js", import.meta.url),
  new URL("formActorHost.js", hostDirectory),
);
await Deno.copyFile(
  new URL("./formActorHost.d.ts", import.meta.url),
  new URL("formActorHost.d.ts", hostDirectory),
);
for (const name of ["LICENSE", "NOTICE"]) {
  await Deno.copyFile(
    new URL(`./dist/actor-runtime.${name}`, import.meta.url),
    new URL(`actor-runtime.${name}`, hostDirectory),
  );
}
