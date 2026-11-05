import { type Expression, parse, tokenizer } from "acorn";
import { toDisplayString } from "vue";

/** A data-only template binding; undefined is represented explicitly on JSON boundaries. */
export interface TemplateBinding {
  readonly name: string;
  readonly state: "pending" | "ready" | "failed" | "cancelled";
  readonly value?: unknown;
  readonly undefined?: boolean;
}

/** One complete, immutable input snapshot. Missing names stay pending until source is sealed. */
export interface TemplateContext {
  readonly version: 1;
  readonly bindings: readonly TemplateBinding[];
  readonly sourceSealed?: boolean;
}

export type TemplateState = "ready" | "pending" | "failed" | "cancelled" | "incomplete";

/** A local expression result; dependencies include only the branches actually read. */
export interface TemplateResult {
  readonly state: TemplateState;
  readonly text: string;
  readonly dependencies: readonly string[];
  readonly code?: string;
}

export const MaximumExpressionCharacters = 2048;
export const MaximumExpressions = 256;
/** Recoverable rejection of malformed or excessive template snapshot input. */
export class TemplateInputError extends Error {
  constructor() {
    super("invalid_template");
    this.name = "TemplateInputError";
  }
}

const forbidden = new Set(["__proto__", "prototype", "constructor"]);
const identifier = /^[a-z_$][a-z\d_$]{0,63}$/i;

/** Copies bounded JSON data without retaining caller objects or invoking accessors. */
export function captureTemplate(value: unknown): TemplateContext {
  const invalid = () => {
    throw new TemplateInputError();
  };
  if (typeof value !== "object" || value === null) return invalid();
  const context = value as Partial<TemplateContext>;
  if (
    context.version !== 1 || !Array.isArray(context.bindings) || context.bindings.length > 128 ||
    (context.sourceSealed !== undefined && typeof context.sourceSealed !== "boolean")
  ) return invalid();
  let nodes = 0;
  let characters = 0;
  const copy = (input: unknown, depth: number): unknown => {
    if (++nodes > 8192 || depth > 16) return invalid();
    if (typeof input === "string") {
      characters += input.length;
      if (characters > 64_000) return invalid();
      return input;
    }
    if (
      input === null || typeof input === "boolean" ||
      (typeof input === "number" && Number.isFinite(input))
    ) return input;
    if (typeof input !== "object") return invalid();
    const prototype = Object.getPrototypeOf(input);
    if (!Array.isArray(input) && prototype !== Object.prototype && prototype !== null) {
      return invalid();
    }
    const output: Record<string, unknown> | unknown[] = Array.isArray(input)
      ? []
      : Object.create(null);
    const descriptors = Object.getOwnPropertyDescriptors(input);
    if (
      Array.isArray(input) &&
      (input.length > 8192 || Object.keys(descriptors).length !== input.length + 1)
    ) return invalid();
    for (const [key, descriptor] of Object.entries(descriptors)) {
      if (Array.isArray(input) && key === "length") continue;
      if (Array.isArray(input) && (!/^(0|[1-9]\d*)$/.test(key) || Number(key) >= input.length)) {
        return invalid();
      }
      if (!Object.hasOwn(descriptor, "value") || key.length > 128 || forbidden.has(key)) {
        return invalid();
      }
      characters += key.length;
      if (characters > 64_000) return invalid();
      Object.defineProperty(output, key, {
        value: copy(descriptor.value, depth + 1),
        enumerable: true,
      });
    }
    return Object.freeze(output);
  };
  const names = new Set<string>();
  const bindings = Array.from(context.bindings, (item: TemplateBinding) => {
    if (
      typeof item !== "object" || item === null || typeof item.name !== "string" ||
      !identifier.test(item.name) || forbidden.has(item.name) || item.name === "undefined" ||
      names.has(item.name) || !["pending", "ready", "failed", "cancelled"].includes(item.state) ||
      (item.undefined !== undefined && typeof item.undefined !== "boolean")
    ) return invalid();
    names.add(item.name);
    if (item.state !== "ready") {
      if (Object.hasOwn(item, "value") || item.undefined === true) return invalid();
      return Object.freeze({ name: item.name, state: item.state });
    }
    if (item.undefined === true) {
      if (Object.hasOwn(item, "value")) return invalid();
      return Object.freeze({ name: item.name, state: item.state, undefined: true });
    }
    if (!Object.hasOwn(item, "value")) return invalid();
    return Object.freeze({ name: item.name, state: item.state, value: copy(item.value, 0) });
  });
  return Object.freeze({
    version: 1,
    bindings: Object.freeze(bindings),
    sourceSealed: context.sourceSealed === true,
  });
}

class OptionalChainStop {}

class EvaluationStop {
  constructor(readonly state: "pending" | "failed" | "cancelled", readonly code: string) {}
}

/** Interprets a bounded expression AST. No eval, generated functions, imports or host globals. */
export function evaluateTemplate(expression: string, context: TemplateContext): TemplateResult {
  const dependencies = new Set<string>();
  const fail = (code = "unsupported_expression"): never => {
    throw new EvaluationStop("failed", code);
  };
  const data = new Map(context.bindings.map((binding) => [binding.name, binding]));
  let steps = 0;
  const primitive = (value: unknown): value is string | number | boolean | null | undefined =>
    value === null || !["object", "function", "symbol", "bigint"].includes(typeof value);
  const finite = (value: unknown): unknown => {
    if (typeof value === "number" && !Number.isFinite(value)) fail("non_finite_result");
    if (typeof value === "string" && value.length > 64_000) fail("result_too_large");
    return value;
  };
  const read = (node: Expression, depth = 0): unknown => {
    if (++steps > 256 || depth > 32) fail("expression_too_complex");
    const next = (child: Expression) => read(child, depth + 1);
    switch (node.type) {
      case "Literal":
        if (!primitive(node.value) || node.regex || node.bigint) return fail();
        return finite(node.value);
      case "Identifier": {
        if (node.name === "undefined") return undefined;
        dependencies.add(node.name);
        const binding = data.get(node.name);
        if (binding === undefined) {
          throw new EvaluationStop(
            context.sourceSealed ? "failed" : "pending",
            "unresolved_binding",
          );
        }
        if (binding.state !== "ready") {
          throw new EvaluationStop(binding.state, `binding_${binding.state}`);
        }
        return binding.undefined ? undefined : binding.value;
      }
      case "ChainExpression":
        try {
          return next(node.expression);
        } catch (error) {
          if (error instanceof OptionalChainStop) return undefined;
          throw error;
        }
      case "ArrayExpression":
        return node.elements.map((item) =>
          item === null ? undefined : item.type === "SpreadElement" ? fail() : next(item)
        );
      case "ObjectExpression": {
        const output: Record<string, unknown> = Object.create(null);
        for (const property of node.properties) {
          if (property.type !== "Property" || property.kind !== "init" || property.method) {
            return fail();
          }
          const key = property.computed
            ? next(property.key as Expression)
            : property.key.type === "Identifier"
            ? property.key.name
            : property.key.type === "Literal"
            ? property.key.value
            : fail();
          if ((typeof key !== "string" && typeof key !== "number") || forbidden.has(String(key))) {
            return fail("forbidden_member");
          }
          output[key] = next(property.value as Expression);
        }
        return output;
      }
      case "MemberExpression": {
        if (node.object.type === "Super") return fail();
        const object = next(node.object);
        if (object === null || object === undefined) {
          if (node.optional) throw new OptionalChainStop();
          return fail("invalid_access");
        }
        const key = node.computed
          ? next(node.property as Expression)
          : node.property.type === "Identifier"
          ? node.property.name
          : fail();
        if ((typeof key !== "string" && typeof key !== "number") || forbidden.has(String(key))) {
          return fail("forbidden_member");
        }
        if (typeof object === "string" && key === "length") return object.length;
        if (typeof object !== "object") return fail("invalid_access");
        return Object.hasOwn(object, key) ? (object as Record<string, unknown>)[key] : undefined;
      }
      case "UnaryExpression": {
        const value = next(node.argument);
        if (node.operator === "!") return !value;
        if (node.operator === "typeof") return typeof value;
        if (!primitive(value)) return fail("invalid_operand");
        if (node.operator === "+") return finite(Number(value));
        if (node.operator === "-") return finite(-Number(value));
        return fail();
      }
      case "LogicalExpression": {
        const left = next(node.left);
        if (node.operator === "&&") return left ? next(node.right) : left;
        if (node.operator === "||") return left ? left : next(node.right);
        if (node.operator === "??") {
          return left === null || left === undefined ? next(node.right) : left;
        }
        return fail();
      }
      case "ConditionalExpression":
        return next(node.test) ? next(node.consequent) : next(node.alternate);
      case "BinaryExpression": {
        const left = next(node.left as Expression);
        const right = next(node.right);
        if (node.operator === "===") return left === right;
        if (node.operator === "!==") return left !== right;
        if (!primitive(left) || !primitive(right)) return fail("invalid_operand");
        const a = Number(left), b = Number(right);
        switch (node.operator) {
          case "+":
            return finite(
              typeof left === "string" || typeof right === "string"
                ? String(left) + String(right)
                : a + b,
            );
          case "-":
            return finite(a - b);
          case "*":
            return finite(a * b);
          case "/":
            return finite(a / b);
          case "%":
            return finite(a % b);
          case "**":
            return finite(a ** b);
          case "<":
            return typeof left === "string" && typeof right === "string" ? left < right : a < b;
          case "<=":
            return typeof left === "string" && typeof right === "string" ? left <= right : a <= b;
          case ">":
            return typeof left === "string" && typeof right === "string" ? left > right : a > b;
          case ">=":
            return typeof left === "string" && typeof right === "string" ? left >= right : a >= b;
          default:
            return fail();
        }
      }
      case "CallExpression": {
        if (
          node.callee.type !== "MemberExpression" || node.callee.computed ||
          node.callee.property.type !== "Identifier"
        ) return fail();
        const name = node.callee.property.name;
        if (
          name !== "toFixed" || node.callee.object.type === "Super" || node.arguments.length > 1
        ) return fail();
        const object = next(node.callee.object);
        if ((object === null || object === undefined) && (node.optional || node.callee.optional)) {
          throw new OptionalChainStop();
        }
        if (typeof object !== "number") return fail();
        const argument = node.arguments[0];
        if (argument?.type === "SpreadElement") return fail();
        const digits = argument === undefined ? 0 : next(argument);
        if (typeof digits !== "number" || !Number.isInteger(digits) || digits < 0 || digits > 20) {
          return fail("invalid_format");
        }
        return object.toFixed(digits);
      }
      default:
        return fail();
    }
  };
  try {
    if (expression.length > MaximumExpressionCharacters) fail("expression_too_large");
    const program = parse(`(${expression}\n)`, { ecmaVersion: 2022 });
    const statement = program.body[0];
    if (program.body.length !== 1 || statement.type !== "ExpressionStatement") return fail();
    const value = read(statement.expression);
    const text = toDisplayString(value);
    if (text.length > 64_000) fail("result_too_large");
    return { state: "ready", text, dependencies: [...dependencies] };
  } catch (error) {
    if (error instanceof EvaluationStop) {
      return {
        state: error.state,
        text: error.state === "pending" ? "…" : `[${error.code}]`,
        code: error.code,
        dependencies: [...dependencies],
      };
    }
    return {
      state: "failed",
      text: "[invalid_expression]",
      code: "invalid_expression",
      dependencies: [...dependencies],
    };
  }
}

/** Locates a real closing delimiter, ignoring braces inside strings, templates and regex literals. */
export function interpolationEnd(source: string): number | undefined {
  const tokens = tokenizer(source.slice(0, MaximumExpressionCharacters + 2), { ecmaVersion: 2022 });
  let depth = 0;
  try {
    for (;;) {
      const token = tokens.getToken();
      const label = token.type.label;
      if (label === "eof") return undefined;
      if (label === "}" && depth === 0 && source[token.start + 1] === "}") return token.start;
      if (["(", "[", "{", "${"].includes(label)) depth++;
      if ([")", "]", "}"].includes(label)) depth--;
    }
  } catch {
    return undefined;
  }
}
