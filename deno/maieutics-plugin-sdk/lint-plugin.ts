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
 *    be produced by `defineActor`. A bare function export is auto-fixed to a
 *    const bound to a `defineActor(function ...)` expression; a bare
 *    object-literal export whose members are all functions is auto-fixed to
 *    wrap it in `defineActor(...)`; other bare exports (constants,
 *    re-exports) are reported without a fix because no semantically safe
 *    automatic rewrite exists.
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

/**
 * True if `init` is a call to a name in `names` (a defineActor binding), or a
 * member call on a namespace import (`sdk.defineActor(...)`).
 */
function isDefineActorCall(
  init: unknown,
  names: ReadonlySet<string>,
  namespaceNames: ReadonlySet<string>,
): boolean {
  return isCallTo(init, names, namespaceNames, "defineActor");
}

/**
 * True if `init` is a call to `defineExtensionPoint` (either the identity
 * single-argument form or the legacy handler form). Extension point exports
 * are legitimate entrypoint exports and must not be reported by the
 * entrypoint-exports rule.
 */
function isDefineExtensionPointCall(
  init: unknown,
  names: ReadonlySet<string>,
  namespaceNames: ReadonlySet<string>,
): boolean {
  return isCallTo(init, names, namespaceNames, "defineExtensionPoint");
}

function isCallTo(
  init: unknown,
  names: ReadonlySet<string>,
  namespaceNames: ReadonlySet<string>,
  target: string,
): boolean {
  const callee = (init as {
    callee?: {
      type?: string;
      name?: string;
      object?: { name?: string };
      property?: { name?: string };
    };
  } | null)
    ?.callee;
  if (callee === undefined || callee === null) return false;
  if (callee.type === "Identifier") return names.has(callee.name ?? "");
  if (callee.type === "MemberExpression") {
    return namespaceNames.has(callee.object?.name ?? "") &&
      callee.property?.name === target;
  }
  return false;
}

const entrypointRegisteredRule = {
  create(context: Deno.lint.RuleContext) {
    let defineActorNames: Set<string> | undefined;
    let namespaceNames: Set<string> | undefined;
    let fileHasActorExport = false;
    let registeredEntrypoints: Set<string> | undefined;

    const ensureNames = (): void => {
      if (defineActorNames !== undefined) return;
      defineActorNames = new Set();
      namespaceNames = new Set();
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
        const specifiers = (node as {
          specifiers?: Array<
            { type?: string; imported?: { name?: string }; local?: { name?: string } }
          >;
        })
          .specifiers ?? [];
        for (const specifier of specifiers) {
          if (specifier.type === "ImportSpecifier" && specifier.imported?.name === "defineActor") {
            defineActorNames!.add(specifier.local?.name ?? "defineActor");
          } else if (specifier.type === "ImportNamespaceSpecifier") {
            namespaceNames!.add(specifier.local?.name ?? "");
          }
        }
      },
      ExportNamedDeclaration(node: Deno.lint.ExportNamedDeclaration) {
        ensureNames();
        const declaration = node.declaration;
        if (declaration?.type !== "VariableDeclaration") return;
        for (const declarator of declaration.declarations) {
          const init = declarator.init;
          if (isDefineActorCall(init, defineActorNames!, namespaceNames!)) {
            fileHasActorExport = true;
            if (
              registeredEntrypoints !== undefined &&
              !isEntrypoint(context.filename, registeredEntrypoints)
            ) {
              context.report({
                node,
                message: "This file exports an actor via defineActor but is not declared in " +
                  "maieutics.json entrypoints; add it to an entrypoint script list so the " +
                  "worker is started.",
              });
            }
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
    let namespaceNames: Set<string> | undefined;
    // The file imports defineActor (or a namespace of the sdk), so a fix that
    // emits defineActor(...) is safe. Without it, fixes are skipped.
    let hasDefineActorImport = false;

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

    const names = (): ReadonlySet<string> => defineActorNames ?? new Set<string>();
    const namespace = (): ReadonlySet<string> => namespaceNames ?? new Set<string>();

    // Type-only exports (interface/type/enum declarations) have no runtime
    // value and are not actors; they are exempt from the defineActor rule.
    const isTypeExport = (declaration: unknown): boolean => {
      const type = (declaration as { type?: string } | null)?.type;
      return type === "TSTypeAliasDeclaration" || type === "TSInterfaceDeclaration" ||
        type === "TSEnumDeclaration" || type === "TSDeclareFunction";
    };

    return {
      ImportDeclaration(node: Deno.lint.ImportDeclaration) {
        const source = (node as { source?: { value?: string } }).source?.value ?? "";
        if (!source.includes("maieutics-plugin-sdk") && !source.includes("@maieutics/plugin-sdk")) {
          return;
        }
        if (defineActorNames === undefined) defineActorNames = new Set();
        if (namespaceNames === undefined) namespaceNames = new Set();
        for (
          const specifier of (node as {
            specifiers?: Array<
              { type?: string; imported?: { name?: string }; local?: { name?: string } }
            >;
          })
            .specifiers ?? []
        ) {
          if (specifier.type === "ImportSpecifier" && specifier.imported?.name === "defineActor") {
            defineActorNames.add(specifier.local?.name ?? "defineActor");
            hasDefineActorImport = true;
          } else if (specifier.type === "ImportNamespaceSpecifier") {
            namespaceNames.add(specifier.local?.name ?? "");
            hasDefineActorImport = true;
          }
        }
      },
      ExportNamedDeclaration(node: Deno.lint.ExportNamedDeclaration) {
        if (!isEntrypointFile()) return;
        const declaration = node.declaration;
        if (declaration === null || declaration === undefined) return; // export { x } from ... handled by ExportAllDeclaration? No: export { x } is ExportNamedDeclaration without declaration.
        if (isTypeExport(declaration)) return;

        if (declaration.type === "FunctionDeclaration") {
          // export function helper(...) {...} — convert to a const bound to a
          // defineActor-wrapped function expression, preserving the full
          // declaration text (params, annotations, async, generator).
          const id = declaration.id?.name;
          const fnText = context.sourceCode.getText(declaration);
          context.report({
            node,
            message: "Entrypoint exports must be produced by defineActor; wrap the function " +
              "in defineActor(...).",
            ...(hasDefineActorImport
              ? {
                fix(fixer: Deno.lint.Fixer) {
                  const name = id ?? "fn";
                  return fixer.replaceText(
                    node,
                    `export const ${name} = defineActor(${fnText});`,
                  );
                },
              }
              : {}),
          });
          return;
        }

        if (declaration.type === "VariableDeclaration") {
          // Check every declarator, not just the first.
          for (const declarator of declaration.declarations) {
            const init = declarator.init;
            if (isDefineActorCall(init, names(), namespace())) continue; // defineActor(...) — OK.
            if (isDefineExtensionPointCall(init, names(), namespace())) continue; // extension point — OK.

            if (
              init !== null && init !== undefined && isObjectLiteral(init) &&
              allMembersAreFunctions(init)
            ) {
              const literal = context.sourceCode.getText(init);
              context.report({
                node,
                message: "Entrypoint exports must be produced by defineActor; wrap the surface " +
                  "in defineActor({ ... }).",
                ...(hasDefineActorImport
                  ? {
                    fix(fixer: Deno.lint.Fixer) {
                      return fixer.replaceText(init, `defineActor(${literal})`);
                    },
                  }
                  : {}),
              });
              continue;
            }
            context.report({
              node,
              message:
                "Entrypoint exports must be produced by defineActor; define this value as an " +
                "actor surface via defineActor({ ... }) or move it out of the entrypoint file.",
            });
          }
          return;
        }

        // export { x } from / export { x } — re-export without a local declaration.
        context.report({
          node,
          message: "Entrypoint exports must be produced by defineActor; re-exports are not " +
            "actor surfaces — define the value in this entrypoint via defineActor(...) " +
            "or move it out.",
        });
      },
      ExportDefaultDeclaration(node: Deno.lint.ExportDefaultDeclaration) {
        if (!isEntrypointFile()) return;
        const declaration = (node as { declaration?: unknown }).declaration;
        const decl = declaration as { type?: string; id?: { name?: string } } | null;
        // export default function / class / object literal — a bare default
        // export is not a defineActor surface.
        context.report({
          node,
          message: "Entrypoint exports must be produced by defineActor; export default is not " +
            "an actor surface — define it as a named export via defineActor(...).",
        });
      },
      ExportAllDeclaration(node: Deno.lint.ExportAllDeclaration) {
        if (!isEntrypointFile()) return;
        context.report({
          node,
          message: "Entrypoint exports must be produced by defineActor; `export * from` is not " +
            "an actor surface — define the value in this entrypoint via defineActor(...).",
        });
      },
    };
  },
};

/**
 * `maieutics/provide-once` — a signal should not be provided to the same
 * extension point twice in one file. Each `provide(ep, signal)` registers an
 * independent contribution, so providing the same signal identifier twice
 * usually means an accidental duplicate (a re-imported module or a doubled
 * call path) and yields two identical values in the collection.
 *
 * Static scope: only the same identifier within one file is detected.
 * Different identifiers (deliberate multi-source contributions) and dynamic
 * signals (created in a loop, imported from elsewhere) are out of scope — this
 * is a hygiene check, not a correctness gate. The runtime still treats each
 * provide as an independent contribution.
 */
const provideOnceRule = {
  create(context: Deno.lint.RuleContext) {
    let provideNames: Set<string> | undefined;
    let namespaceNames: Set<string> | undefined;
    // signal identifier → line of its first provide; a second provide reports.
    const providedByIdentifier = new Map<string, number>();
    // A signal bound to a fresh expression (signal(...)) is not an identifier
    // and cannot be duplicated by name — only identifier arguments are tracked.
    const reported = new Set<string>();

    const ensureNames = (): void => {
      if (provideNames !== undefined) return;
      provideNames = new Set();
      namespaceNames = new Set();
    };

    return {
      ImportDeclaration(node: Deno.lint.ImportDeclaration) {
        ensureNames();
        const source = (node as { source?: { value?: string } }).source?.value ?? "";
        if (!source.includes("maieutics-plugin-sdk") && !source.includes("@maieutics/plugin-sdk")) {
          return;
        }
        const specifiers = (node as {
          specifiers?: Array<
            { type?: string; imported?: { name?: string }; local?: { name?: string } }
          >;
        }).specifiers ?? [];
        for (const specifier of specifiers) {
          if (specifier.type === "ImportSpecifier") {
            const imported = specifier.imported?.name ?? "";
            const local = specifier.local?.name ?? "";
            if (imported === "provide") provideNames!.add(local);
          } else if (specifier.type === "ImportNamespaceSpecifier") {
            namespaceNames!.add(specifier.local?.name ?? "");
          }
        }
      },
      CallExpression(node: Deno.lint.CallExpression) {
        if (provideNames === undefined) return;
        const callee = (node as {
          callee?: {
            type?: string;
            name?: string;
            object?: { name?: string };
            property?: { name?: string };
          };
        })?.callee;
        if (callee === undefined || callee === null) return;
        const isProvide = callee.type === "Identifier"
          ? provideNames.has(callee.name ?? "")
          : callee.type === "MemberExpression" &&
            (namespaceNames?.has(callee.object?.name ?? "") ?? false) &&
            callee.property?.name === "provide";
        if (!isProvide) return;
        const args = (node as { arguments?: unknown[] }).arguments ?? [];
        const signal = args[1] as { type?: string; name?: string } | undefined;
        if (signal === undefined || signal.type !== "Identifier") return;
        const name = signal.name ?? "";
        if (name.length === 0 || reported.has(name)) return;
        const firstLine = providedByIdentifier.get(name);
        if (firstLine !== undefined) {
          reported.add(name);
          context.report({
            node: node as Deno.lint.Node,
            message: `Signal '${name}' is provided to the same extension point more than once ` +
              `(first provide at line ${firstLine}). Each provide registers an independent ` +
              `contribution; a repeated provide of the same signal is usually an accidental ` +
              `duplicate and yields identical values in the collection.`,
          });
          return;
        }
        providedByIdentifier.set(
          name,
          (node as { loc?: { start?: { line?: number } } }).loc
            ?.start?.line ?? 0,
        );
      },
    };
  },
};

/** Function-like node types: a provide inside any of these is off the module
 * top level and registers a contribution per invocation (accumulating ghosts)
 * with an effect that is never returned — both leak. */
const FUNCTION_NODE_TYPES = new Set([
  "FunctionDeclaration",
  "FunctionExpression",
  "ArrowFunctionExpression",
]);

/**
 * `maieutics/provide-top-level` — provide() is a declarative statement about
 * the worker's contribution and must run once at module top level. A provide
 * inside a function body registers a new contribution on every invocation
 * (never unregistered, so the collection accumulates duplicates) and starts a
 * changesOf effect that is never stopped — both leak. Deliberately conditional
 * top-level provides (e.g. `if (env === "prod") provide(ep, s)`) are allowed:
 * they evaluate once and stay declarative.
 */
const provideTopLevelRule = {
  create(context: Deno.lint.RuleContext) {
    let provideNames: Set<string> | undefined;
    let namespaceNames: Set<string> | undefined;

    const ensureNames = (): void => {
      if (provideNames !== undefined) return;
      provideNames = new Set();
      namespaceNames = new Set();
    };

    const isProvideCall = (node: Deno.lint.CallExpression): boolean => {
      const callee = (node as {
        callee?: {
          type?: string;
          name?: string;
          object?: { name?: string };
          property?: { name?: string };
        };
      })?.callee;
      if (callee === undefined || callee === null) return false;
      if (callee.type === "Identifier") return provideNames!.has(callee.name ?? "");
      return callee.type === "MemberExpression" &&
        (namespaceNames?.has(callee.object?.name ?? "") ?? false) &&
        callee.property?.name === "provide";
    };

    /** Walks the parent chain up to the enclosing function, if any. */
    const enclosingFunction = (node: Deno.lint.CallExpression): string | undefined => {
      let cur: unknown = (node as { parent?: unknown }).parent;
      let depth = 0;
      while (cur !== undefined && cur !== null && depth < 32) {
        const type = (cur as { type?: string }).type ?? "";
        if (FUNCTION_NODE_TYPES.has(type)) return type;
        cur = (cur as { parent?: unknown }).parent;
        depth += 1;
      }
      return undefined;
    };

    return {
      ImportDeclaration(node: Deno.lint.ImportDeclaration) {
        ensureNames();
        const source = (node as { source?: { value?: string } }).source?.value ?? "";
        if (!source.includes("maieutics-plugin-sdk") && !source.includes("@maieutics/plugin-sdk")) {
          return;
        }
        const specifiers = (node as {
          specifiers?: Array<
            { type?: string; imported?: { name?: string }; local?: { name?: string } }
          >;
        }).specifiers ?? [];
        for (const specifier of specifiers) {
          if (specifier.type === "ImportSpecifier") {
            const imported = specifier.imported?.name ?? "";
            const local = specifier.local?.name ?? "";
            if (imported === "provide") provideNames!.add(local);
          } else if (specifier.type === "ImportNamespaceSpecifier") {
            namespaceNames!.add(specifier.local?.name ?? "");
          }
        }
      },
      CallExpression(node: Deno.lint.CallExpression) {
        if (provideNames === undefined) return;
        if (!isProvideCall(node)) return;
        const fnType = enclosingFunction(node);
        if (fnType === undefined) return;
        context.report({
          node: node as Deno.lint.Node,
          message: `provide() must be called at module top level, not inside a ${fnType}. ` +
            `A function-body provide registers a new contribution on every invocation ` +
            `(never unregistered) and starts a change stream that is never stopped — ` +
            `both leak. Declare the contribution once at top level and use ` +
            `signal.value = undefined to pause it or unprovide() to withdraw it.`,
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
    "provide-once": provideOnceRule,
    "provide-top-level": provideTopLevelRule,
  },
};
