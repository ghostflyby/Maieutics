/**
 * Shared Worker wrapper entry.
 *
 * NESTED Workers (created through the recursive Worker patch) enter through
 * this module. It:
 *
 *   1. parses the bootstrap metadata (target module URL + non-sensitive
 *      version/profile markers) from its own module URL query string;
 *   2. installs the shared Worker patch BEFORE the target module is evaluated;
 *   3. installs the versioned bootstrap marker;
 *   4. composes the profile capability for nested realms — there is no profile
 *      entry module here, so the wrapper is the composition point. The only
 *      profile-conditional capability is the plugin storage client (ADR 0022),
 *      installed for "plugin" realms so nested workers share their plugin's
 *      `localStorage` through the host;
 *   5. imports the target module.
 *
 * A bootstrap import failure rejects this module's top-level evaluation, which
 * Deno surfaces as Worker startup failure — the actor owner observes the death
 * through its normal crash path and no detached child survives.
 *
 * Root Workers do NOT enter through this module: the Maieutics-controlled
 * entries (repl_worker.ts, worker_entry.ts) install the shared bootstrap
 * themselves so their module graph stays statically analyzable (a wrapper's
 * dynamic import of the target would require the worker to hold `import`
 * access for the target's bare-specifier graph).
 *
 * This module must never be imported directly: only the patch (or a test)
 * builds wrapper URLs that carry the contract query keys.
 */

import { installBootstrapMarker, readBootstrapMetadata } from "./bootstrap_contract.ts";
import { installPluginStorage } from "./storage_channel.ts";
import { installWorkerPatch } from "./worker_patch.ts";

const metadata = readBootstrapMetadata(import.meta.url);
if (metadata === null) {
  throw new Error(
    "The Maieutics worker bootstrap must run through the shared wrapper " +
      "(build the entry with buildWrapperUrl).",
  );
}

installWorkerPatch(metadata.profile);
installBootstrapMarker({ version: metadata.version, profile: metadata.profile });
if (metadata.profile === "plugin") installPluginStorage();

await import(metadata.targetUrl);
