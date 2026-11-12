/** Package the same standalone artifact in the notebook extension; never fork its implementation. */
const source = new URL("./dist/outputClient.js", import.meta.url);
const target = new URL("../maieutics-vscode/media/outputClient.js", import.meta.url);
await Deno.copyFile(source, target);
