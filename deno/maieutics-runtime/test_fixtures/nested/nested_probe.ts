/**
 * Test fixture: a NESTED worker target that probes the patched Worker surface.
 * The wrapper installed the patch before this module's top-level code ran, so
 * `globalThis.Worker` is the routed constructor. Pins the boundary:
 *
 *   - `Worker.prototype` is reachable and `Worker.prototype.constructor`
 *     points at the routed constructor;
 *   - a real instance's `constructor` (own chain) also points at the routed
 *     constructor;
 *   - the prototype parent does not carry the native Worker constructor;
 *   - `instanceof Worker` stays truthful.
 */

const patched = globalThis.Worker;
let protoAccessible = false;
let protoCtorIsRouted = false;
let instanceCtorIsRouted = false;
let parentCtorNameNotNative = false;
let instanceOfResult = false;
try {
  const proto = patched.prototype;
  protoAccessible = true;
  protoCtorIsRouted = proto.constructor === patched;
} catch {
  protoAccessible = false;
}
try {
  const probe = new Worker(
    new URL("../nested_instance_target.ts", import.meta.url),
    { type: "module" },
  );
  instanceOfResult = probe instanceof patched;
  instanceCtorIsRouted = probe.constructor === patched;
  probe.terminate();
} catch {
  instanceOfResult = false;
}
try {
  const parent = Object.getPrototypeOf(patched.prototype) as {
    constructor?: { name?: string };
  };
  parentCtorNameNotNative = parent.constructor === undefined ||
    parent.constructor.name !== "Worker";
} catch {
  parentCtorNameNotNative = false;
}
(self as unknown as { postMessage(value: unknown): void }).postMessage({
  phase: "nested-probe",
  protoAccessible,
  protoCtorIsRouted,
  instanceCtorIsRouted,
  parentCtorNameNotNative,
  instanceOfResult,
});
