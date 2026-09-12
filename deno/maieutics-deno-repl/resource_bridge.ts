/**
 * Virtual resource fetch bridge (ADR 0026 decision 5).
 *
 * Translates `GET` requests to virtual resource URLs (`workspace://`,
 * `mcp://`, or any custom scheme registered with the kernel) into one
 * `GET /v1/resource?uri=...` on the process control channel, so cell code can
 * `fetch()` kernel-side resources natively. `http(s)/ws(s)/blob/data/about`
 * (and unknown/relative values) are not virtual and pass through untouched.
 *
 * The bridge reuses the REPL child's existing control-channel grants: Unix
 * speaks to the unix-socket endpoint through a proxy `Deno.createHttpClient`
 * against `http://localhost` (granted as `localhost:80`), Windows speaks to the
 * loopback `host:port` with the bootstrap bearer credential. Only `GET` is
 * supported; other methods throw `TypeError`. Non-2xx answers surface as
 * ordinary `Response` objects (native `fetch` semantics).
 */

const RESOURCE_PATH = "/v1/resource";
const PROXY_HOST = "localhost";

const NON_VIRTUAL_SCHEMES = new Set([
  "http",
  "https",
  "ws",
  "wss",
  "blob",
  "data",
  "about",
  "file",
]);

export interface ResourceBridgeOptions {
  /** Control-channel address: a unix socket path or a Windows `host:port`. */
  address: string;
  /** Windows bearer credential issued during process bootstrap. */
  credential?: string;
  /** Overrides the platform-derived transport (test seam). */
  transport?: "unix" | "tcp";
}

const SCHEME_PATTERN = /^[a-zA-Z][a-zA-Z0-9+.-]*:/;

/** True when the value is an absolute URL whose scheme must be resolved by
 * the kernel's resource registry rather than native network fetch. */
export function isVirtualResourceUrl(
  url: unknown,
): url is string | URL {
  if (typeof url !== "string" && !(url instanceof URL)) return false;
  const text = String(url);
  const match = SCHEME_PATTERN.exec(text);
  if (match === null) return false;
  return !NON_VIRTUAL_SCHEMES.has(match[0].slice(0, -1).toLowerCase());
}

export interface ResourceReader {
  /** Reads one virtual resource URI over the control channel. */
  (uri: string, init?: { signal?: AbortSignal }): Promise<Response>;
}

/** Builds the control-channel reader for virtual resource URIs. */
export function createResourceReader(
  options: ResourceBridgeOptions,
): ResourceReader {
  if (options.address.length === 0) {
    throw new TypeError("The control-channel address is required.");
  }
  const useUnixProxy = options.transport === "unix" ||
    (options.transport === undefined && Deno.build.os !== "windows");
  const client = useUnixProxy
    ? Deno.createHttpClient({
      proxy: { transport: "unix", path: options.address },
    })
    : undefined;
  const headers = options.credential === undefined
    ? undefined
    : { Authorization: `Bearer ${options.credential}` };

  return (uri, init) => {
    const base = useUnixProxy ? `http://${PROXY_HOST}` : `http://${options.address}`;
    const target = `${base}${RESOURCE_PATH}?uri=${encodeURIComponent(String(uri))}`;
    return fetch(target, {
      method: "GET",
      redirect: "error",
      ...(headers === undefined ? {} : { headers }),
      ...(client === undefined ? {} : { client }),
      ...(init?.signal === undefined ? {} : { signal: init.signal }),
    });
  };
}

/** Wraps a fetch implementation so virtual resource URLs are served by the
 * kernel while every other input reaches the original implementation. */
export function patchFetch(
  original: typeof fetch,
  options: ResourceBridgeOptions,
): typeof fetch {
  const readResource = createResourceReader(options);
  const patched: typeof fetch = (input, init) => {
    const url = input instanceof Request ? input.url : input;
    if (!isVirtualResourceUrl(url)) return original(input, init);

    const method = (init?.method ?? (input instanceof Request ? input.method : "GET"))
      .toUpperCase();
    if (method !== "GET") {
      throw new TypeError(
        `Virtual resource fetch supports GET only (got ${method}).`,
      );
    }
    const signal = init?.signal ?? (input instanceof Request ? input.signal : undefined);
    return signal === undefined ? readResource(String(url)) : readResource(String(url), { signal });
  };
  return patched;
}
