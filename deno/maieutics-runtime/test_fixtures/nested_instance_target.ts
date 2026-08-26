/**
 * Test fixture: a nested worker target that does nothing but exit. Used by
 * nested_probe.ts to construct a real Worker through the patched constructor
 * and observe `instanceof`.
 */

(self as unknown as { postMessage(value: unknown): void }).postMessage({
  phase: "nested-instance-ready",
});
