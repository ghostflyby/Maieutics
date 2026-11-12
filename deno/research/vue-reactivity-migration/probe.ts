/** Feasibility probes, not a production adapter. Run explicitly; see the migration study. */
import { assertEquals, assertStrictEquals, assertThrows } from "@std/assert";
// @deno-types="npm:@vue/reactivity@3.5.13/dist/reactivity.d.ts"
import {
  computed,
  effect,
  effectScope,
  isProxy,
  onScopeDispose,
  reactive,
  ref,
  shallowRef,
  stop,
  watch,
} from "@vue/reactivity";

Deno.test("service values need shallowRef to retain original identity", () => {
  const service = {
    invoke() {
      return 42;
    },
  };
  assertStrictEquals(shallowRef(service).value, service);
  assertEquals(isProxy(ref(service).value), true);
  assertEquals(ref(service).value === service, false);
});

Deno.test("Vue effect returns a runner, not the Preact stop function", () => {
  const value = shallowRef(0);
  const seen: number[] = [];
  const runner = effect(() => seen.push(value.value));
  value.value = 1;
  runner();
  value.value = 2;
  assertEquals(seen, [0, 1, 1, 2]);
  stop(runner);
  value.value = 3;
  assertEquals(seen, [0, 1, 1, 2]);
});

Deno.test("raw watch preserves synchronous contribution transitions including withdrawal", () => {
  const value = shallowRef<number | undefined>(1);
  const collection = computed(() => value.value === undefined ? [] : [value.value]);
  const seen: number[][] = [];
  const handle = watch(() => seen.push([...collection.value]), null);
  value.value = 2;
  value.value = undefined;
  value.value = 3;
  handle.stop();
  value.value = 4;
  assertEquals(seen, [[1], [2], [], [3]]);
});

Deno.test("tracked plain projections detach nested objects and retain unrelated field granularity", () => {
  const state = reactive({ rows: [{ value: 1 }], unused: 0 });
  const projected: unknown[] = [];
  const handle = watch(
    () => projected.push(JSON.parse(JSON.stringify({ rows: state.rows }))),
    null,
  );
  state.rows[0].value = 2;
  state.rows.push({ value: 3 });
  state.unused++;
  handle.stop();
  assertEquals(projected, [
    { rows: [{ value: 1 }] },
    { rows: [{ value: 2 }] },
    { rows: [{ value: 2 }, { value: 3 }] },
  ]);
  // JSON here is a plain-data probe, not the proposed binary/value codec.
  assertThrows(() => structuredClone(state), DOMException);
});

Deno.test("deep watch old and new values cannot serve as detached patch bases", () => {
  const state = reactive({ nested: { value: 1 } });
  let identical = false;
  const handle = watch(state, (value, old) => identical = value === old);
  state.nested.value = 2;
  handle.stop();
  assertEquals(identical, true);
});

Deno.test("custom scheduling can coalesce a state projection and ignores stopped jobs", () => {
  const state = reactive({ count: 0 });
  const jobs = new Set<() => void>();
  const seen: number[] = [];
  const handle = watch(() => seen.push(state.count), null, {
    scheduler(job, first) {
      if (first) job();
      else jobs.add(job);
    },
  });
  state.count = 1;
  state.count = 2;
  assertEquals(jobs.size, 1);
  for (const job of jobs) job();
  jobs.clear();
  state.count = 3;
  handle.stop();
  for (const job of jobs) job();
  assertEquals(seen, [0, 2]);
});

Deno.test("effect scopes own synchronous watchers and cleanup", () => {
  const scope = effectScope();
  const state = shallowRef(0);
  const seen: number[] = [];
  let cleaned = 0;
  scope.run(() => {
    onScopeDispose(() => cleaned++);
    watch(() => seen.push(state.value), null);
  });
  state.value = 1;
  scope.stop();
  scope.stop();
  state.value = 2;
  assertEquals(seen, [0, 1]);
  assertEquals(cleaned, 1);
});

Deno.test("scope stop does not revoke retained refs or lazy computed reads", () => {
  const scope = effectScope();
  const state = shallowRef(1);
  const derived = scope.run(() => computed(() => state.value * 2));
  assertEquals(derived?.value, 2);
  scope.stop();
  state.value = 3;
  assertEquals(derived?.value, 6, "Model retirement needs its own admission/publication guard");
});

Deno.test("effects created after await need explicit owner reentry", async () => {
  const scope = effectScope();
  const state = shallowRef(0);
  let calls = 0;
  const handle = await scope.run(async () => {
    await Promise.resolve();
    return watch(() => {
      state.value;
      calls++;
    }, null);
  });
  scope.stop();
  state.value++;
  assertEquals(calls, 2, "Stopping the former scope does not own the late watcher");
  handle?.stop();

  const owned = effectScope();
  await owned.run(async () => {
    await Promise.resolve();
    owned.run(() =>
      watch(() => {
        state.value;
        calls++;
      }, null)
    );
  });
  owned.stop();
  const before = calls;
  state.value++;
  assertEquals(calls, before);
});
