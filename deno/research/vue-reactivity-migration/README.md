# Vue migration feasibility probes

Research only; these files are not embedded in the executable or imported by the product module
graph. See the [migration study](../../../docs/vue-reactive-ui-migration.md) for the results,
architecture and production acceptance work.

`probe.ts` pins `@vue/reactivity@3.5.13` and contains nine explicit, signal-driven semantic tests.
`compare_existing.py` runs temporary copies of the current SDK's twelve local reactive tests first
with Preact, then with a minimal Vue comparison adapter. That adapter is not a proposed public API
or production implementation. The subprocess owns a private temporary directory and lockfile; legacy
tests' generated files are removed with it. A child timeout fails the comparison.

Run from the repository root with Deno 2.9.6 and the existing dependency cache:

```sh
cp deno/deno.lock /tmp/maieutics-vue-study.lock
deno test --no-config --import-map=deno/research/vue-reactivity-migration/imports.json \
  --lock=/tmp/maieutics-vue-study.lock --frozen=false --cached-only --allow-env=NODE_ENV \
  deno/research/vue-reactivity-migration/probe.ts
python3 deno/research/vue-reactivity-migration/compare_existing.py --deno /path/to/deno-2.9.6
```

The standalone probe is named `probe.ts`, so it is run explicitly rather than being added to the
product's normal test discovery. Source npm entries require NODE_ENV; the comparison additionally
needs file access for temporary test modules. Both use cached-only resolution. Neither changes the
repository dependency declarations/lockfile nor starts a real plugin host, kernel or browser.
