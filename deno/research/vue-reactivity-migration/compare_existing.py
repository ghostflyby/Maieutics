"""Compare existing local SDK collection tests using temporary copies only.

The small compatibility adapter is an experiment, not the proposed public API.
Neither production sources nor the repository lockfile are changed.
"""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


parser = argparse.ArgumentParser()
parser.add_argument("--deno", default="deno")
parser.add_argument("--baseline-ref", default="0b10d0a")
args = parser.parse_args()
sdk = Path(__file__).resolve().parents[2] / "maieutics-plugin-sdk"
adapter = """
import { shallowRef, computed, watch, type Ref } from 'npm:@vue/reactivity@3.5.13';
export type Signal<T> = Readonly<Ref<T>>;
export const signal = shallowRef;
export {computed};
export function effect(fn: () => void | (() => void)): () => void {
  const handle=watch((onCleanup)=>{
    const cleanup=fn();
    if(typeof cleanup==='function')onCleanup(cleanup);
  },null);
  return ()=>handle.stop();
}
"""

with tempfile.TemporaryDirectory(prefix="maieutics-vue-collections-") as directory:
    root = Path(directory)
    child_temp = root / "temp"
    child_temp.mkdir()
    child_env = dict(os.environ)
    for key in ("TMPDIR", "TEMP", "TMP"):
        child_env[key] = str(child_temp) + os.sep
    lock = root / "deno.lock"
    lock.write_text(subprocess.check_output([
        "git", "show", f"{args.baseline_ref}:deno/deno.lock",
    ], cwd=sdk.parent.parent, text=True))
    for name in ("reactive.ts", "reactive_test.ts", "admission.ts"):
        source = subprocess.check_output([
            "git", "show", f"{args.baseline_ref}:deno/maieutics-plugin-sdk/{name}",
        ], cwd=sdk.parent.parent, text=True)
        (root / name).write_text(source)
    (root / "vue_adapter.ts").write_text(adapter)
    for label, implementation in (
        ("preact-baseline", "npm:@preact/signals-core@1.14.4"),
        ("vue-experiment", (root / "vue_adapter.ts").as_uri()),
    ):
        mapping = root / "imports.json"
        mapping.write_text(json.dumps({"imports": {
            "@std/assert": "jsr:@std/assert@^1.0.19",
            "@preact/signals-core": implementation,
        }}))
        result = subprocess.run([
            args.deno, "test", "--no-config", "--lock", str(lock), "--frozen=false", "--cached-only",
            "--allow-env=NODE_ENV", "--allow-read", "--allow-write",
            "--import-map", str(mapping), str(root / "reactive_test.ts"),
        ], capture_output=True, text=True, timeout=60, check=False, env=child_env)
        print(label, "exit", result.returncode, flush=True)
        for line in (result.stdout + result.stderr).splitlines():
            if "passed" in line or "error:" in line:
                print(line, flush=True)
        if result.returncode:
            print(result.stdout, result.stderr)
            raise SystemExit(result.returncode)
