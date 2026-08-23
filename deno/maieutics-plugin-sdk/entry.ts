/**
 * Plugin author entry (`@maieutics/plugin-sdk`, the `.` export).
 *
 * Aggregates the APIs a plugin needs to declare extension points, contribute
 * values to collections, and call dependency actors — the SDK's main usage
 * intent — in one import. Worker runtime bootstrap (`initPluginWorker`), the
 * cross-worker codecs, and the reactive-injection hooks are NOT exposed here;
 * they live on the `./runtime`, `./interop`, and `./reactive` paths
 * respectively.
 *
 * ```ts
 * import {
 *   defineActor,
 *   defineExtensionPoint,
 *   defineServiceExtensionPoint,
 *   provide,
 *   signal,
 *   depActor,
 * } from "@maieutics/plugin-sdk";
 * ```
 */

// —— Extension point declarations (host extension points + service identities) ——

export {
  defineExtensionPoint,
  defineService,
  defineServiceExtensionPoint,
  isService,
} from "./mod.ts";

// —— Actor surfaces and dependency interop ——

export { defineActor, defineDependency, depActor } from "./mod.ts";

// —— Reactive collections: provide / consume / subscribe ——

export {
  collection,
  isExtensionPoint,
  isLocalExtensionPoint,
  isRemoteExtensionPoint,
  markCollectionStream,
  provide,
  providerCount,
  snapshot,
  subscribe,
  unprovide,
  values,
} from "./mod.ts";

export { computed, effect, signal } from "./reactive.ts";

// —— Built-in extension point markers ——

export { ExtensionPoint } from "./mod.ts";

// —— Types ——

export type {
  CollectionStream,
  CollectionValue,
  ExtensionPointIdentity,
  ExtensionPointImpl,
  ExtensionPointInput,
  ExtensionPointName,
  ProviderRegistration,
  ReactiveValue,
  Remote,
  RemoteActor,
} from "./mod.ts";
export type {
  DiscoverContext,
  McpDiscover,
  McpDiscoverFunction,
  McpDiscoverFunctionInput,
  McpDiscoverInput,
  McpDiscoverObject,
  McpDiscoverObjectInput,
  McpDiscovery,
  ToolHookDecision,
  ToolInvokeContext,
  ToolPostInvoke,
  ToolPostInvokeContext,
  ToolPostInvokeFunction,
  ToolPostInvokeFunctionInput,
  ToolPostInvokeInput,
  ToolPostInvokeObject,
  ToolPostInvokeObjectInput,
  ToolPreInvoke,
  ToolPreInvokeFunction,
  ToolPreInvokeFunctionInput,
  ToolPreInvokeInput,
  ToolPreInvokeObject,
  ToolPreInvokeObjectInput,
} from "./mod.ts";
