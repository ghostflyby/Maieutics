const source = await Deno.readTextFile(new URL("./dist/vue_worker.js", import.meta.url));
await Deno.writeTextFile(
  new URL("./bundle/vue_worker_source.js", import.meta.url),
  `// Generated trusted worker entry, loaded as a textual module URL.\nexport const vueWorkerSource=${
    JSON.stringify(source)
  };\n`,
);
