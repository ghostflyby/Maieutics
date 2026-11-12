/// <reference lib="dom" />
import { getCurrentInstance, inject, type InjectionKey, onScopeDispose } from "@vue/runtime-dom";
import type { PluginFetch, PluginHttpClient, PluginHttpOptions } from "./types.d.ts";

const fetchKey: InjectionKey<PluginFetch> = Symbol("maieutics.plugin.fetch");

/** Create a host-owned client for one server-authorized plugin mount. Starts no I/O. */
export function createPluginHttpClient(
  baseUrl: string,
  options: PluginHttpOptions = {},
): PluginHttpClient {
  const base = new URL(baseUrl);
  if (
    !["http:", "https:"].includes(base.protocol) || base.username || base.password ||
    base.search || base.hash || !base.pathname.endsWith("/")
  ) throw new TypeError("invalid_plugin_http_base");
  const lifetime = new AbortController();
  const send = options.fetch ?? globalThis.fetch.bind(globalThis);
  const scopedFetch: PluginFetch = async (path, init = {}) => {
    lifetime.signal.throwIfAborted();
    // Root-relative paths are relative to the plugin root, never the gateway root.
    if (typeof path !== "string" || path.startsWith("//") || path.includes("\\")) {
      throw new TypeError("invalid_plugin_http_path");
    }
    const url = new URL(path.replace(/^\//, ""), base);
    if (
      url.origin !== base.origin || !url.pathname.startsWith(base.pathname) ||
      url.username || url.password || url.hash
    ) throw new TypeError("invalid_plugin_http_path");
    const signal = init.signal ? AbortSignal.any([lifetime.signal, init.signal]) : lifetime.signal;
    signal.throwIfAborted();
    return await send(url, {
      ...init,
      signal,
      credentials: "omit",
      referrerPolicy: "no-referrer",
      redirect: "error",
    });
  };
  return {
    fetch: scopedFetch,
    install(app) {
      lifetime.signal.throwIfAborted();
      app.provide(fetchKey, scopedFetch);
    },
    dispose() {
      lifetime.abort();
    },
  };
}

/** Obtain plugin-scoped native Fetch in synchronous Vue setup; unmount aborts active bodies. */
export function useFetch(): PluginFetch {
  if (!getCurrentInstance()) throw new Error("plugin_fetch_requires_setup");
  const fetch = inject(fetchKey, undefined);
  if (!fetch) throw new Error("plugin_fetch_unavailable");
  const lifetime = new AbortController();
  onScopeDispose(() => lifetime.abort());
  return (path, init = {}) =>
    fetch(path, {
      ...init,
      signal: init.signal ? AbortSignal.any([lifetime.signal, init.signal]) : lifetime.signal,
    });
}
