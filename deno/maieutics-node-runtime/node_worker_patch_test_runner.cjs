// Node-side test runner for the Maieutics node worker patch.
//
// Run with `node node_worker_patch_test_runner.cjs`. The runner spawns a real
// `node` child process for every scenario so each scenario observes a fresh
// process state (a patched builtin export cannot be un-patched in a shared
// process). It uses only built-in Node modules.
//
// Every scenario child starts with the product preload:
//
//   node --require node_worker_preload.cjs test_fixtures/scenario_*.cjs
//
// Each scenario prints a single JSON line; the runner parses it and reports
// the aggregated result.

"use strict";

const { spawnSync } = require("node:child_process");
const path = require("node:path");

const ADAPTER_DIR = __dirname;
const PRELOAD = path.join(ADAPTER_DIR, "node_worker_preload.cjs");
const FIXTURES_DIR = path.join(ADAPTER_DIR, "test_fixtures");

const scenarioNames = [
  "scenario_named_import",
  "scenario_esm_import",
  "scenario_esm_worker_data",
  "scenario_nested_marker",
  "scenario_options_name",
  "scenario_specifier_resolution",
  "scenario_prototype_backdoor",
  "scenario_hostile_preload",
  "scenario_unsupported",
  "scenario_startup_error",
];

/** Preload modes under test: --require (CJS) and --import (ESM entry). */
const preloadModes = ["require", "import"];

let passed = 0;
let failed = 0;

for (const mode of preloadModes) {
  for (const name of scenarioNames) {
    if (runScenario(name, mode)) passed += 1;
    else failed += 1;
  }
}

console.log(`\n${passed} scenario(s) passed, ${failed} scenario(s) failed.`);
if (failed > 0) process.exit(1);

function runScenario(name, mode) {
  const script = path.join(FIXTURES_DIR, `${name}.cjs`);
  const args = mode === "require" ? ["--require", PRELOAD, script] : ["--import", PRELOAD, script];
  const result = spawnSync(process.execPath, args, {
    encoding: "utf8",
    timeout: 30_000,
  });
  const stdout = (result.stdout ?? "").trim();
  const stderr = (result.stderr ?? "").trim();

  let report = null;
  try {
    report = JSON.parse(stdout);
  } catch {
    // Not JSON; the scenario aborted before reporting.
  }

  if (report !== null && report.ok === true) {
    console.log(`ok - ${name} (${mode})`);
    return true;
  }
  console.log(`not ok - ${name} (${mode})`);
  const detail = report === null ? stdout : JSON.stringify(report.payload);
  console.log(`  ${detail.split("\n").join("\n  ")}`);
  if (stderr.length > 0) {
    console.log(`  stderr: ${stderr.split("\n").join("\n  ")}`);
  }
  return false;
}

// Keep an explicit reference so the file is a documented part of the adapter.
module.exports = { scenarioNames };
