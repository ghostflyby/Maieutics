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
 * 3. `maieutics/data-entrypoint` — every string-valued (data) entrypoint must
 *    resolve to a file that exists inside the plugin project, parses as JSON,
 *    and — for catalogued data names — matches the expected format (`mcp`:
 *    mcpServers/servers shape, stdio command, http url). The kernel enforces
 *    the same contract at load time with full schema strictness; this rule
 *    gives the author the feedback at edit time. Validation runs once per
 *    plugin project per `deno lint` invocation.
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

/** The catalogued data entry names (the lint-side mirror of the kernel's
 * PluginDataName). Unknown string-valued names are inert on this kernel. */
const KNOWN_DATA_NAMES = new Set(["mcp"]);

/** The entrypoints key holding the worker map (ADR 0033 hierarchy). */
const WORKER_SECTION = "worker";

/** Reads maieutics.json and returns the set of script paths declared as worker
 * entrypoints. Understands both the two-level form (`worker` kind wrapping the
 * worker map) and the legacy flat form; string values are data entry points and
 * are not script paths. */
function readEntrypointScripts(maieuticsPath: string): Set<string> {
  const result = new Set<string>();
  let parsed: unknown;
  try {
    parsed = JSON.parse(Deno.readTextFileSync(maieuticsPath));
  } catch {
    return result;
  }
  const entrypoints = (parsed as { entrypoints?: Record<string, unknown> }).entrypoints;
  if (entrypoints === null || typeof entrypoints !== "object" || Array.isArray(entrypoints)) {
    return result;
  }
  const root = maieuticsPath.slice(0, maieuticsPath.lastIndexOf("/"));
  for (const [key, value] of Object.entries(entrypoints)) {
    if (key === WORKER_SECTION) {
      if (value === null || typeof value !== "object" || Array.isArray(value)) continue;
      for (const scripts of Object.values(value)) collectScripts(scripts, root, result);
      continue;
    }
    // Legacy flat form: an array under a plain key is still a worker script list.
    collectScripts(value, root, result);
  }
  return result;
}

function collectScripts(scripts: unknown, root: string, into: Set<string>): void {
  if (!Array.isArray(scripts)) return;
  for (const script of scripts) {
    if (typeof script !== "string") continue;
    try {
      into.add(new URL(script, `file://${root}/`).pathname);
    } catch {
      // malformed script path — ignore
    }
  }
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

const entrypointRegisteredRule: Deno.lint.Rule = {
  create(context: Deno.lint.RuleContext) {
    let defineActorNames: Set<string> | undefined;
    let namespaceNames: Set<string> | undefined;
    let registeredEntrypoints: Set<string> | undefined;

    const ensureNames = (): void => {
      if (defineActorNames !== undefined) return;
      defineActorNames = new Set();
      namespaceNames = new Set();
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

const entrypointExportsRule: Deno.lint.Rule = {
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
const provideOnceRule: Deno.lint.Rule = {
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
const provideTopLevelRule: Deno.lint.Rule = {
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

/**
 * `maieutics/data-entrypoint` — every string-valued entrypoint is a data entry
 * point: the kernel collects the referenced file and the entry name's interpreter
 * interprets it (ADR 0033). This rule gives the author that feedback at edit time
 * instead of at plugin-load time:
 *
 * - the resolved path must stay inside the plugin project;
 * - the file must exist and parse as JSON;
 * - unknown data names are collected but inert on this kernel (visible hint);
 * - for `mcp`, the collected JSON must match the format shape: the
 *   `mcpServers`/`servers` top-level key (not both), each entry an object with a
 *   stdio (`command`) or http (`url`, https unless loopback) transport.
 *
 * The kernel remains the authority — its load-time validation is stricter (per
 * transport allowed keys, timeout positivity, `enabled` semantics). Validation
 * runs once per plugin project per `deno lint` invocation and reports against
 * the first linted file of the project (JSON files themselves are not linted).
 */
const validatedProjects = new Set<string>();

function dataFileProblems(root: string, name: string, relativePath: string): string[] {
  const label = `data entry '${name}'`;
  if (relativePath.trim() === "") return [`${label}: the path is empty`];

  let pathname: string;
  try {
    pathname = new URL(relativePath, `file://${root}/`).pathname;
  } catch {
    return [`${label}: '${relativePath}' is not a valid path`];
  }
  if (!isWithin(root, pathname)) {
    return [`${label}: '${relativePath}' resolves outside the plugin project`];
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(Deno.readTextFileSync(pathname));
  } catch (error) {
    const reason = error instanceof Deno.errors.NotFound
      ? "the file does not exist"
      : error instanceof SyntaxError
      ? `the file is not valid JSON (${(error as Error).message})`
      : `the file could not be read (${(error as Error).message})`;
    return [`${label}: ${reason} (${relativePath})`];
  }

  if (!KNOWN_DATA_NAMES.has(name)) {
    return [
      `${label}: this data name is not supported by this kernel; the file is ` +
      `collected but inert (known names: ${[...KNOWN_DATA_NAMES].join(", ")})`,
      ...mcpFormatProblems(label, parsed),
    ].slice(0, 1);
  }
  return mcpFormatProblems(label, parsed);
}

/** Format-shape checks for the `mcp` data format. Deliberately lighter than the
 * kernel's load-time validation: this catches shape mistakes at edit time. */
function mcpFormatProblems(label: string, parsed: unknown): string[] {
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    return [`${label}: mcp data must be a JSON object`];
  }
  const root = parsed as { mcpServers?: unknown; servers?: unknown };
  if (root.mcpServers !== undefined && root.servers !== undefined) {
    return [`${label}: mcp data must not combine the 'mcpServers' and 'servers' top-level keys`];
  }
  const servers = root.mcpServers ?? root.servers;
  if (servers === undefined) return [];
  if (servers === null || typeof servers !== "object" || Array.isArray(servers)) {
    return [`${label}: the server map must be a JSON object`];
  }

  const problems: string[] = [];
  for (const [serverKey, server] of Object.entries(servers as Record<string, unknown>)) {
    const serverLabel = `${label} / '${serverKey}'`;
    if (server === null || typeof server !== "object" || Array.isArray(server)) {
      problems.push(`${serverLabel}: the server entry must be a JSON object`);
      continue;
    }
    const config = server as Record<string, unknown>;
    const declared = typeof config.type === "string"
      ? config.type.toLowerCase()
      : typeof config.transport === "string"
      ? (config.transport as string).toLowerCase()
      : undefined;
    if (declared === "sse") {
      problems.push(`${serverLabel}: the 'sse' transport is not supported`);
      continue;
    }
    const kind = declared ?? (typeof config.url === "string" ? "http" : "stdio");
    if (kind !== "stdio" && kind !== "http") {
      problems.push(`${serverLabel}: the transport must be 'stdio' or 'http'`);
      continue;
    }
    if (kind === "stdio") {
      if (typeof config.command !== "string" || (config.command as string).trim() === "") {
        problems.push(`${serverLabel}: stdio servers require a non-empty 'command'`);
      }
      continue;
    }
    const url = typeof config.url === "string" ? config.url : "";
    const secure = url.startsWith("https://");
    const loopback = /^http:\/\/(localhost|127\.0\.0\.1|\[::1\])([:\\/]|$)/.test(url);
    if (!secure && !loopback) {
      problems.push(
        `${serverLabel}: http servers require an absolute 'url' — https, or plain http only for loopback`,
      );
    }
  }
  return problems;
}

const dataEntrypointRule: Deno.lint.Rule = {
  create(context: Deno.lint.RuleContext) {
    return {
      Program(node: Deno.lint.Program) {
        const dir = context.filename.slice(0, context.filename.lastIndexOf("/"));
        const maieuticsPath = findUp(dir, "maieutics.json");
        if (maieuticsPath === undefined) return;
        // One validation per plugin project per invocation: the diagnostics are
        // project-level, so reporting them on every linted file would only repeat.
        if (validatedProjects.has(maieuticsPath)) return;
        validatedProjects.add(maieuticsPath);

        let parsed: unknown;
        try {
          parsed = JSON.parse(Deno.readTextFileSync(maieuticsPath));
        } catch {
          return; // a broken manifest is the kernel's load-time error, not lint's
        }
        const entrypoints = (parsed as { entrypoints?: unknown }).entrypoints;
        if (entrypoints === null || typeof entrypoints !== "object" || Array.isArray(entrypoints)) {
          return;
        }
        const root = maieuticsPath.slice(0, maieuticsPath.lastIndexOf("/"));
        const problems: string[] = [];
        for (const [name, value] of Object.entries(entrypoints as Record<string, unknown>)) {
          if (name === WORKER_SECTION) continue;
          if (typeof value !== "string") continue;
          problems.push(...dataFileProblems(root, name, value));
        }
        for (const message of problems) {
          context.report({ node: node as Deno.lint.Node, message });
        }
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
    "data-entrypoint": dataEntrypointRule,
  },
};
