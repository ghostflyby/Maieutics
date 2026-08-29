/**
 * The plugin HTTP root contract (ADR 0021 decision 3).
 *
 * A plugin declares its HTTP UI by providing one `Deno.ServeHandler` into the
 * service collection; the host aggregates the contract and routes
 * `/<entrance-token>/plugins/<pluginId>/…` to it with the URL projected onto
 * the plugin's virtual root. One root per plugin — the plugin routes
 * internally on the request path.
 *
 * The element type and the handler info are Deno's own (`Deno.ServeHandler`,
 * `Deno.ServeHandlerInfo`): a plugin handler has exactly the `Deno.serve`
 * handler shape. Contract notes that a handler author relies on:
 *   - `request.url` is the projected virtual root; relative URLs are the
 *     mount-portable way to reference the plugin's own resources;
 *   - `request.body` arrives as a natively transferred stream;
 *   - `request.signal` is a live projection of the host-side request signal
 *     (aborts at request end: client abandonment or delivery complete) — a
 *     handler awaiting the next body chunk must race it with the signal;
 *   - `info.completed` resolves when the response (including body) has been
 *     fully delivered and rejects with `Deno.errors.Interrupted` when the
 *     client disconnects mid-delivery.
 *
 * The host process aggregates this contract: the defining-worker specifier is
 * bound to a reserved, syntactically unproducible specifier at module load,
 * so `provide(http, …)` from any plugin worker routes to the host's
 * collection before any init-dependent state exists (ADR 0021 decision 3).
 * One root per plugin is enforced by the host's admission hook (decision 9).
 */

import {
  bindDefiningWorker,
  defineServiceExtensionPoint,
  type ExtensionPointIdentity,
} from "./reactive.ts";

/**
 * Reserved specifier of the host-side aggregator. Canonical plugin specifiers
 * have the form `<plugin>/<entrypoint>`; the `maieutics:` prefix is a host-
 * reserved namespace that the plugin host's acquire router never resolves to
 * a plugin worker.
 */
export const HTTP_AGGREGATOR_SPECIFIER = "maieutics:http-aggregator";

/**
 * The plugin HTTP root: a service extension point whose element is a
 * `Deno.ServeHandler`. Provided values convert to remote actor references,
 * so the host holds a callable reference and the httpCodec moves
 * `Request`/`Response` (body streams, projected signals) across the boundary.
 */
export const http: ExtensionPointIdentity<Deno.ServeHandler> = defineServiceExtensionPoint<
  Deno.ServeHandler
>("http");

bindDefiningWorker(http, HTTP_AGGREGATOR_SPECIFIER);
