/**
 * Cross-worker interop (`@maieutics/plugin-sdk/interop`).
 *
 * The actor-reference and collection-stream codecs plus the surface-registry
 * primitives used by host-side code and advanced plugins that build on raw
 * actor references. Plugin authors writing ordinary extension points do not
 * need this path; the high-level `defineActor` / `depActor` / collection APIs
 * live on the `.` entry.
 */

// —— Reference codecs and surface primitives ——

export {
  ACTOR_BRAND,
  ACTOR_CODEC_TAG,
  actorRefCodec,
  clearActorExports,
  clearNamespaceSurface,
  decodeActorValue,
  encodeActorValue,
  flattenSurface,
  REF_PROXY_BRAND,
  registerActorExport,
  remoteActor,
  setNamespaceSurface,
  setSpecifierAcquire,
} from "./actor_ref.ts";
export type { RemoteActor } from "./actor_ref.ts";

// —— Collection stream transport ——

export { collectionStreamCodec, markCollectionStream } from "./collection_stream.ts";

// —— Dependency stubs (low-level acquire surfaces) ——

export { createDependencyStub } from "./mod.ts";
