/**
 * Maieutics lint plugin.
 *
 * Enforces the plugin entry contract:
 *
 * 1. `maieutics/entrypoint-registered` — a file whose exports use `defineActor`
 *    (the conversion export) must be declared in `maieutics.json` under some
 *    entrypoint's script list. Without that declaration the file is never
 *    started as a worker, so the actor it defines is unreachable.
 * 2. `maieutics/entrypoint-exports` — every export of an entrypoint file must
 *    be a worker-actor function member: either produced by `defineActor` or a
 *    bare function (worker-actor's rpc surface accepts function members
 *    natively). A bare object-literal export whose members are all functions is
 *    auto-fixed to wrap it in `defineActor(...)`; other bare exports
 *    (constants, re-exports) are reported without a fix because no
 *    semantically safe automatic rewrite exists.
 *
 * The plugin locates `maieutics.json` by walking up from the linted file
 * (never from `Deno.cwd()`, which is the launch directory and unreliable).
 * When no `maieutics.json` is found the plugin is silent: the file is not
 * part of a Maieutics plugin project.
 */

/** Walks up from `startDir` looking for `fileName`; returns its path or undefined. */
function findUp(startDir: string, fileName: string): string | undefined {
  let dir = startDir;
  for (;;) {
    const candidate = `${dir}/${fileName}`;
    try {
      Deno.statSync(candidate);
      return candidate;
    } catch {
      // not here — keep walking up
    }
    const separator = dir.lastIndexOf("/");
    if (separator <= 0) return undefined;
    dir = dir.slice(0, separator);
  }
}

/** Reads maieutics.json and returns the set of script paths declared as entrypoints. */
function readEntrypointScripts(maieuticsPath: string): Set<string> {
  const result = new Set<string>();
  let parsed: unknown;
  try {
    parsed = JSON.parse(Deno.readTextFileSync(maieuticsPath));
  } catch {
    return result;
  }
  const entrypoints = (parsed as { entrypoints?: Record<string, unknown> }).entrypoints;
  if (entrypoints === null || typeof entrypoints !== "object") return result;
  const root = maieuticsPath.slice(0, maieuticsPath.lastIndexOf("/"));
  for (const scripts of Object.values(entrypoints)) {
    if (!Array.isArray(scripts)) continue;
    for (const script of scripts) {
      if (typeof script !== "string") continue;
      try {
        result.add(new URL(script, `file://${root}/`).pathname);
      } catch {
        // malformed script path — ignore
      }
    }
  }
  return result;
}

/** True if `dir` is a prefix of `file` (both normalized paths). */
function isWithin(dir: string, file: string): boolean {
  return file.startsWith(dir.endsWith("/") ? dir : `${dir}/`);
}

/** True if `file` equals or lies inside one of the entrypoint script paths. */
function isEntrypoint(file: string, entrypoints: Set<string>): boolean {
  for (const entry of entrypoints) {
    if (isWithin(entry, file) || entry === file) return true;
  }
  return false;
}

function isObjectLiteral(node: unknown): boolean {
  return (node as { type?: string } | null)?.type === "ObjectExpression";
}

/** True if the object literal's members are all function-valued (methods or function props). */
function allMembersAreFunctions(node: unknown): boolean {
  const properties = (node as { properties?: unknown[] } | null)?.properties ?? [];
  return properties.length > 0 && properties.every((property) => {
    const p = property as { type?: string; value?: { type?: string } } | null;
    return p?.type === "Property" && p.value?.type === "FunctionExpression" ||
      p?.type === "Property" && p.value?.type === "ArrowFunctionExpression";
  });
}

const entrypointRegisteredRule = {
  create(context: Deno.lint.RuleContext) {
    // The set of names that refer to defineActor in this file (resolved lazily
    // per file from its imports).
    let defineActorNames: Set<string> | undefined;
    let fileHasActorExport = false;
    let registeredEntrypoints: Set<string> | undefined;

    const ensureNames = (): void => {
      if (defineActorNames !== undefined) return;
      defineActorNames = new Set();
      fileHasActorExport = false;
      registeredEntrypoints = readEntrypointScriptsOrEmpty(context.filename);
    };

    return {
      ImportDeclaration(node: Deno.lint.ImportDeclaration) {
        ensureNames();
        const source = (node as { source?: { value?: string } }).source?.value ?? "";
        if (!source.includes("maieutics-plugin-sdk") && !source.includes("@maieutics/plugin-sdk")) {
          return;
        }
        const specifiers = (node as { specifiers?: Array<{ type?: string; imported?: { name?: string }; local?: { name?: string } }> })
          .specifiers ?? [];
        for (const specifier of specifiers) {
          if (specifier.type !== "ImportSpecifier") continue;
          if (specifier.imported?.name === "defineActor") {
            defineActorNames!.add(specifier.local?.name ?? "defineActor");
          }
        }
      },
      ExportNamedDeclaration(node: Deno.lint.ExportNamedDeclaration) {
        ensureNames();
        const declaration = node.declaration;
        const init = declaration?.type === "VariableDeclaration"
          ? declaration.declarations[0]?.init
          : undefined;
        const callee = (init as { callee?: { name?: string } } | null)?.callee;
        const isActorCall = callee !== undefined && callee !== null &&
          defineActorNames!.has((callee as { name?: string }).name ?? "");
        if (isActorCall) {
          fileHasActorExport = true;
          if (registeredEntrypoints !== undefined && !isEntrypoint(context.filename, registeredEntrypoints)) {
            context.report({
              node,
              message:
                "This file exports an actor via defineActor but is not declared in " +
                "maieutics.json entrypoints; add it to an entrypoint script list so the " +
                "worker is started.",
            });
          }
        }
      },
      Program() {
        ensureNames();
      },
    };

    function readEntrypointScriptsOrEmpty(filename: string): Set<string> | undefined {
      const dir = filename.slice(0, filename.lastIndexOf("/"));
      const maieuticsPath = findUp(dir, "maieutics.json");
      return maieuticsPath === undefined ? undefined : readEntrypointScripts(maieuticsPath);
    }
  },
};

const entrypointExportsRule = {
  create(context: Deno.lint.RuleContext) {
    let registeredEntrypoints: Set<string> | undefined;
    let defineActorNames: Set<string> | undefined;

    const isEntrypointFile = (): boolean => {
      if (registeredEntrypoints === undefined) {
        const dir = context.filename.slice(0, context.filename.lastIndexOf("/"));
        const maieuticsPath = findUp(dir, "maieutics.json");
        registeredEntrypoints = maieuticsPath === undefined
          ? undefined
          : readEntrypointScripts(maieuticsPath);
      }
      if (registeredEntrypoints === undefined) return false;
      return isEntrypoint(context.filename, registeredEntrypoints);
    };

    return {
      ImportDeclaration(node: Deno.lint.ImportDeclaration) {
        const source = (node as { source?: { value?: string } }).source?.value ?? "";
        if (!source.includes("maieutics-plugin-sdk") && !source.includes("@maieutics/plugin-sdk")) {
          return;
        }
        if (defineActorNames === undefined) defineActorNames = new Set();
        for (const specifier of (node as { specifiers?: Array<{ type?: string; imported?: { name?: string }; local?: { name?: string } }> })
          .specifiers ?? []) {
          if (specifier.type === "ImportSpecifier" && specifier.imported?.name === "defineActor") {
            defineActorNames.add(specifier.local?.name ?? "defineActor");
          }
        }
      },
      ExportNamedDeclaration(node: Deno.lint.ExportNamedDeclaration) {
        if (!isEntrypointFile()) return;
        const names = defineActorNames ?? new Set<string>();
        const declaration = node.declaration;
        // A bare function export is a worker-actor function member (the rpc
        // surface accepts function members natively; flattenSurface exposes it
        // as a method). It is a valid entrypoint export — no defineActor wrap.
        if (declaration?.type === "FunctionDeclaration") return;

        const init = declaration?.type === "VariableDeclaration"
          ? declaration.declarations[0]?.init
          : undefined;
        const callee = (init as { callee?: { name?: string } } | null)?.callee;
        const isActorCall = callee !== undefined && callee !== null &&
          names.has((callee as { name?: string }).name ?? "");
        if (isActorCall) return; // export const x = defineActor(...) — OK.

        // Bare export. If it is an object literal whose members are all functions,
        // auto-fix by wrapping in defineActor(...).
        if (init !== null && init !== undefined && isObjectLiteral(init) && allMembersAreFunctions(init)) {
          const literal = context.sourceCode.getText(init);
          context.report({
            node,
            message:
              "Entrypoint exports must be produced by defineActor; wrap the surface " +
              "in defineActor({ ... }).",
            fix(fixer: Deno.lint.Fixer) {
              return fixer.replaceText(init, `defineActor(${literal})`);
            },
          });
          return;
        }
        context.report({
          node,
          message:
            "Entrypoint exports must be produced by defineActor; define this value as an " +
            "actor surface via defineActor({ ... }) or move it out of the entrypoint file.",
        });
      },
    };
  },
};

export default {
  name: "maieutics",
  rules: {
    "entrypoint-registered": entrypointRegisteredRule,
    "entrypoint-exports": entrypointExportsRule,
  },
};
