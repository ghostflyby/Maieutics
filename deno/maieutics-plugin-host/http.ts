/**
 * Plugin HTTP gateway (ADR 0021): the host-side aggregator for the plugin
 * HTTP root contract plus the loopback router that fronts it.
 *
 * Aggregator — the host is the defining worker of the SDK's `http` contract
 * (`HTTP_AGGREGATOR_SPECIFIER`, bound at SDK module load). A plugin worker's
 * `provide(http, shallowRef(handler))` acquires this process's collection surface
 * through the standard `__acquire-actor` routing (the acquire router serves
 * it directly instead of forwarding to a plugin worker), and every subsequent
 * request is a direct host↔worker actor call: the httpCodec moves the fetch
 * `Request`/`Response` across the boundary with natively transferred body
 * streams and a live projected `request.signal`. The host is not in the
 * streaming data path after the call is placed — backpressure and cancel
 * propagate through the transferred streams.
 *
 * Admission (ADR 0021 decision 9) — the gateway installs the contract's
 * admission hook enforcing one root per plugin. The verdict travels back to
 * the providing worker's blocked `provide()` through the admission mailbox,
 * so a duplicate root fails at the offender's own call site while the first
 * mount stays intact.
 *
 * Router — one `Deno.serve()` on loopback (the process already holds
 * `--allow-net` as trusted orchestration, ADR 0018 decision 8). External
 * shape `/<entrance-token>/plugins/<pluginId>/…`; the mount is stripped with
 * path-boundary matching and the URL projected onto the plugin's virtual
 * root. The kernel and the control bus are never in the request data path.
 */

import {
  admissionMailboxFor,
  type AdmissionRequestFrame,
  answerAdmission,
} from "../maieutics-plugin-sdk/admission.ts";
import { evaluateAdmissionHook, setAdmissionHook } from "../maieutics-plugin-sdk/reactive.ts";
import { http, HTTP_AGGREGATOR_SPECIFIER } from "../maieutics-plugin-sdk/http.ts";
import { httpCodec } from "../maieutics-plugin-sdk/http_codec.ts";
import { actorRefCodec, collectionStreamCodec } from "../maieutics-plugin-sdk/interop.ts";
import {
  abortSignalCodec,
  callbackCodec,
  errorCodec,
  iterableCodec,
} from "@ghostflyby/worker-actor/codecs";
import {
  connectChannel,
  makeRpcHandler,
  PayloadCodecRegistry,
} from "@ghostflyby/worker-actor/codec";
import type { Remote } from "@ghostflyby/worker-actor";

/** A live HTTP root contributed by one plugin worker. */
export interface HttpMount {
  /** Plugin directory name derived from the contribution key. */
  readonly pluginId: string;
  /** The provider's contribution key (`<specifier>:<uuid>`). */
  readonly providerKey: string;
  /** Canonical specifier of the contributing worker. */
  readonly specifier: string;
  /** The provided `Deno.ServeHandler`, as a remote actor reference. */
  readonly handler: Remote<Deno.ServeHandler>;
  /** Per-mount credential; never share the host-wide entrance with a browser component. */
  readonly accessToken: string;
}

/** Mount-table snapshot for registry reporting and tests. */
export interface HttpMountSnapshot {
  readonly pluginId: string;
  readonly specifier: string;
  readonly live: boolean;
  /** Scoped to this plugin and registration generation; authenticated control plane only. */
  readonly accessToken: string;
}

/** What the kernel learns about the gateway (ADR 0038 stage 4): the entrance
 * address and token (a capability carrier — control-bus and bearer-authed
 * surfaces only, never logged) plus the live mount table. */
export interface HttpGatewayDescriptor {
  readonly hostname: string;
  readonly port: number;
  readonly token: string;
  readonly mounts: readonly HttpMountSnapshot[];
}

const CONTRACT_NAME = http.name;

const CSP_HTML = "default-src 'self'; connect-src 'none'; img-src 'self' data:";
const HOP_BY_HOP = [
  "connection",
  "keep-alive",
  "proxy-authenticate",
  "proxy-authorization",
  "te",
  "trailer",
  "transfer-encoding",
  "upgrade",
  "host",
  "content-length",
];

interface HttpProvider {
  pluginId: string;
  specifier: string;
  active: boolean;
  port: MessagePort;
  lifetime: AbortController;
  completion: Promise<void>;
}

/** Constant-time string equality for the entrance token compare. */
function timingSafeEqual(a: string, b: string): boolean {
  const aBytes = new TextEncoder().encode(a);
  const bBytes = new TextEncoder().encode(b);
  const length = Math.max(aBytes.length, bBytes.length);
  let mismatch = aBytes.length === bBytes.length ? 0 : 1;
  for (let index = 0; index < length; index += 1) {
    mismatch |= (aBytes[index] ?? 0) ^ (bBytes[index] ?? 0);
  }
  return mismatch === 0;
}

/**
 * Aggregator + router for the plugin HTTP root. One instance per host
 * process; `routeAcquire` and the worker `__admit` frames feed it, the
 * router fronts it.
 */
export class HttpGateway {
  readonly #mounts = new Map<string, HttpMount>();
  readonly #providerPlugins = new Map<string, string>();
  readonly #providers = new Set<HttpProvider>();
  readonly #registrations = new Map<string, symbol>();
  readonly #admissions = new Map<string, { specifier: string; providerKey: string }>();
  #disposed = false;
  #closing: Promise<void> | undefined;
  readonly #routerListeners = new Set<() => void>();
  #router: { addr: Deno.NetAddr; stop: () => Promise<void> } | undefined;

  constructor() {
    // One root per plugin (ADR 0021 decision 2/9): the second contributor
    // from the same plugin fails at its own provide() call site.
    setAdmissionHook(CONTRACT_NAME, (context) => {
      const root = this.#providerPlugins.get(context.providerSpecifier);
      // admit() supplies this mapping from host-derived worker identity before evaluating policy.
      const duplicate = context.existingProviders.some(
        (specifier) => this.#providerPlugins.get(specifier) === root,
      );
      if (duplicate) {
        return `plugin '${root}' already provides an HTTP root; only one root per plugin is allowed`;
      }
    });
  }

  /** Live mount-table snapshot (registry reporting, tests). */
  snapshots(): HttpMountSnapshot[] {
    return [...this.#mounts.values()].map((mount) => ({
      pluginId: mount.pluginId,
      specifier: mount.specifier,
      live: true,
      accessToken: mount.accessToken,
    }));
  }

  /** A live HTTP root holds a worker reference until it is withdrawn. */
  hasProvider(specifier: string): boolean {
    return [...this.#mounts.values()].some((mount) => mount.specifier === specifier);
  }

  /** Registers a callback invoked whenever the mount table changes. */
  onMountsChanged(listener: () => void): () => void {
    this.#routerListeners.add(listener);
    return () => this.#routerListeners.delete(listener);
  }

  /**
   * Answers a worker's `__admit` frame. `providerSpecifier` is authoritative
   * (derived by the host from the requesting worker's registered specifier).
   */
  admit(frame: AdmissionRequestFrame, providerSpecifier: string, pluginId: string): void {
    if (!(frame.sab instanceof SharedArrayBuffer)) return;
    const mailbox = admissionMailboxFor(frame.sab);
    answerAdmission(mailbox, () => {
      if (this.#disposed) return "http_gateway_disposed";
      this.#providerPlugins.set(providerSpecifier, pluginId);
      const isHttp = frame.ep === CONTRACT_NAME && frame.def === HTTP_AGGREGATOR_SPECIFIER;
      if (isHttp && !frame.providerKey.startsWith(`${providerSpecifier}:`)) {
        return "invalid_http_provider";
      }
      const verdict = evaluateAdmissionHook(frame.ep ?? CONTRACT_NAME, {
        extensionPoint: frame.ep ?? CONTRACT_NAME,
        providerSpecifier,
        providerModule: frame.providerModule ?? "",
        existingProviders: [
          ...new Set(
            isHttp
              ? [...this.#admissions.values()].map((admission) => admission.specifier)
              : [...this.#mounts.values()].map((mount) => mount.specifier),
          ),
        ],
      });
      if (typeof verdict === "string") return verdict;
      // Reserve before waking provide(): undefined pauses service, not ownership of its root.
      if (isHttp) {
        this.#admissions.set(pluginId, {
          specifier: providerSpecifier,
          providerKey: frame.providerKey,
        });
      }
    });
  }

  /**
   * Serves the http contract's collection surface on the acquired port — the
   * host is the collection owner, so the acquire router hands the port here
   * instead of forwarding to a plugin worker.
   */
  serveCollection(port: MessagePort, provider: { pluginId: string; specifier: string }): void {
    if (this.#disposed) {
      port.close();
      return;
    }
    const lease: HttpProvider = {
      ...provider,
      active: true,
      port,
      lifetime: new AbortController(),
      completion: Promise.resolve(),
    };
    this.#providers.add(lease);
    const registry = new PayloadCodecRegistry();
    for (const codec of [actorRefCodec, collectionStreamCodec, httpCodec]) {
      registry.register(codec);
    }
    for (const codec of [iterableCodec, errorCodec, abortSignalCodec, callbackCodec]) {
      if (!registry.has(codec.tag)) registry.register(codec);
    }
    const channel = connectChannel(port);
    registry.registerChannel(channel);
    const handler = makeRpcHandler(this.#collectionApi(lease), registry);
    channel.onMessage(async (message: unknown) => {
      const frame = message as {
        type?: string;
        id?: number;
        method?: string;
        args?: unknown[];
      };
      if (frame?.type !== "call") return;
      const result = await handler({
        id: frame.id ?? 0,
        method: frame.method ?? "",
        args: frame.args ?? [],
      });
      if (result.ok) {
        channel.send(
          { type: "result", id: result.id, ok: true, value: result.value },
          result.transfer,
        );
      } else {
        channel.send({
          type: "result",
          id: result.id,
          ok: false,
          error: result.error,
        });
      }
    });
  }

  /** Revoke credentials synchronously when the owning worker stops or loses approval. */
  withdrawProvider(specifier: string): Promise<void> {
    const completions: Promise<void>[] = [];
    for (const provider of this.#providers) {
      if (provider.specifier !== specifier) continue;
      provider.active = false;
      provider.lifetime.abort();
      provider.port.close();
      completions.push(provider.completion);
      this.#providers.delete(provider);
    }
    for (const mount of this.#mounts.values()) {
      if (mount.specifier === specifier) this.#unmount(mount.providerKey);
    }
    for (const key of this.#registrations.keys()) {
      if (key.startsWith(`${specifier}:`)) this.#registrations.delete(key);
    }
    for (const [plugin, admission] of this.#admissions) {
      if (admission.specifier === specifier) this.#admissions.delete(plugin);
    }
    this.#providerPlugins.delete(specifier);
    return Promise.all(completions).then(() => undefined);
  }

  /** Stop owned subscriptions and the listener, joining all receive pumps. */
  dispose(): Promise<void> {
    if (this.#closing) return this.#closing;
    this.#disposed = true;
    const stopped = [...new Set([...this.#providers].map((provider) => provider.specifier))]
      .map((specifier) => this.withdrawProvider(specifier));
    this.#admissions.clear();
    this.#providerPlugins.clear();
    this.#closing = Promise.all([...stopped, this.stopRouter()]).then(() => undefined);
    return this.#closing;
  }

  #collectionApi(provider: HttpProvider): Record<string, (...args: unknown[]) => unknown> {
    const add = (...args: unknown[]): Promise<void> => {
      if (!provider.active) throw new Error("http_provider_closed");
      const initial = args[0];
      const changes = args[1] as AsyncIterable<unknown>;
      const providerKey = args[2];
      if (typeof providerKey === "string" && providerKey.length > 0) {
        if (!providerKey.startsWith(`${provider.specifier}:`)) {
          throw new Error("invalid_http_provider");
        }
        if (this.#admissions.get(provider.pluginId)?.providerKey !== providerKey) {
          throw new Error("http_admission_required");
        }
        if (this.#registrations.has(providerKey)) throw new Error("duplicate_http_root");
        this.#mount(providerKey, initial, provider);
        const registration = Symbol();
        this.#registrations.set(providerKey, registration);
        provider.completion = Promise.all([
          provider.completion,
          this.#pullChanges(providerKey, changes, provider, registration),
        ]).then(() => undefined);
      }

      return Promise.resolve();
    };
    const remove = (...args: unknown[]): Promise<void> => {
      const providerKey = args[0];
      if (provider.active && typeof providerKey === "string") {
        if (!providerKey.startsWith(`${provider.specifier}:`)) {
          throw new Error("invalid_http_provider");
        }
        this.#registrations.delete(providerKey);
        this.#releaseAdmission(provider, providerKey);
        this.#unmount(providerKey);
      }

      return Promise.resolve();
    };
    return {
      [`${CONTRACT_NAME}.add`]: add,
      [`${CONTRACT_NAME}.remove`]: remove,
    };
  }

  #releaseAdmission(provider: HttpProvider, providerKey: string): void {
    if (this.#admissions.get(provider.pluginId)?.providerKey === providerKey) {
      this.#admissions.delete(provider.pluginId);
    }
  }

  #mount(
    providerKey: string,
    handler: unknown,
    provider: HttpProvider,
  ): void {
    if (handler === undefined) {
      this.#unmount(providerKey);
      return;
    }
    if (
      [...this.#mounts.values()].some((mount) =>
        mount.pluginId === provider.pluginId && mount.providerKey !== providerKey
      )
    ) {
      throw new Error("duplicate_http_root");
    }
    const mount: HttpMount = {
      pluginId: provider.pluginId,
      providerKey,
      specifier: provider.specifier,
      handler: handler as Remote<Deno.ServeHandler>,
      accessToken: this.#mounts.get(providerKey)?.accessToken ?? crypto.randomUUID(),
    };
    this.#mounts.set(providerKey, mount);
    this.#notifyMounts();
  }

  #unmount(providerKey: string): void {
    if (this.#mounts.delete(providerKey)) this.#notifyMounts();
  }

  /**
   * Pulls a provider's signal changes: `undefined` drops the mount (the
   * "currently not providing" convention), any other value remounts it. The
   * stream ending means the provider is gone — withdraw the mount.
   */
  async #pullChanges(
    providerKey: string,
    changes: AsyncIterable<unknown>,
    provider: HttpProvider,
    registration: symbol,
  ): Promise<void> {
    const stopped = Promise.withResolvers<IteratorResult<unknown>>();
    const abort = () => stopped.resolve({ done: true, value: undefined });
    provider.lifetime.signal.addEventListener("abort", abort, { once: true });
    try {
      const iterator = changes[Symbol.asyncIterator]();
      while (provider.active && this.#registrations.get(providerKey) === registration) {
        const result = await Promise.race([iterator.next(), stopped.promise]);
        if (
          result.done || !provider.active || this.#registrations.get(providerKey) !== registration
        ) break;
        const value = result.value;
        if (value === undefined) this.#unmount(providerKey);
        else this.#mount(providerKey, value, provider);
      }
    } catch (error) {
      console.error(
        "[plugin-host] HTTP contribution stream failed:",
        error instanceof Error && error.message === "duplicate_http_root"
          ? "duplicate_http_root"
          : "http_change_stream_failed",
      );
    } finally {
      provider.lifetime.signal.removeEventListener("abort", abort);
    }
    if (this.#registrations.get(providerKey) === registration) {
      this.#registrations.delete(providerKey);
      this.#releaseAdmission(provider, providerKey);
      this.#unmount(providerKey);
    }
  }

  #notifyMounts(): void {
    for (const listener of this.#routerListeners) listener();
  }

  // —— Router ——

  /**
   * Starts the loopback listener. The token gates every request; the URL
   * shape is `/<token>/plugins/<pluginId>/<path>`.
   */
  startRouter(options: {
    hostname?: string;
    port?: number;
    token: string;
    onListening?: (address: Deno.NetAddr) => void;
  }): Promise<Deno.NetAddr> {
    if (this.#disposed) return Promise.reject(new Error("http_gateway_disposed"));
    if (this.#router !== undefined) return Promise.resolve(this.#router.addr);
    const hostname = options.hostname ?? "127.0.0.1";
    const token = options.token;
    const controller = new AbortController();
    const server = Deno.serve(
      { hostname, port: options.port ?? 0, signal: controller.signal },
      (request, serveInfo) => this.#handle(request, serveInfo, token),
    );
    const addr = server.addr as Deno.NetAddr;
    this.#router = {
      addr,
      stop: async () => {
        controller.abort();
        await server.finished;
      },
    };
    options.onListening?.(addr);
    return Promise.resolve(addr);
  }

  async stopRouter(): Promise<void> {
    const router = this.#router;
    this.#router = undefined;
    await router?.stop();
  }

  #handle(
    request: Request,
    serveInfo: Deno.ServeHandlerInfo<Deno.NetAddr>,
    token: string,
  ): Promise<Response> {
    return this.#route(request, serveInfo, token).catch(() => new Response(null, { status: 500 }));
  }

  async #route(
    request: Request,
    serveInfo: Deno.ServeHandlerInfo<Deno.NetAddr>,
    token: string,
  ): Promise<Response> {
    const external = new URL(request.url);

    // Entrance checks fail closed as 404s: they leak nothing about the
    // registry. Host validation closes DNS rebinding independently of the
    // token (ADR 0021 decision 6).
    const hostHeader = request.headers.get("host") ?? "";
    const expected = `${external.hostname}:${external.port}`;
    const allowedHosts = new Set([
      expected,
      `localhost:${external.port}`,
      `[::1]:${external.port}`,
    ]);
    if (!allowedHosts.has(hostHeader)) return notFound();

    const segments = external.pathname.split("/").filter((s) => s.length > 0);
    if (
      segments.length < 3 ||
      segments[1] !== "plugins"
    ) {
      return notFound();
    }
    const pluginId = decodeURIComponent(segments[2]);
    const mount = [...this.#mounts.values()].find((m) => m.pluginId === pluginId);
    if (mount === undefined) return notFound();
    const scoped = timingSafeEqual(segments[0], mount.accessToken);
    if (!scoped && !timingSafeEqual(segments[0], token)) {
      return notFound();
    }

    // The URL credential grants this mount alone. No ambient credentials are accepted by
    // the scoped browser client, so opaque notebook origins can use ordinary Fetch/CORS.
    if (
      scoped && request.method === "OPTIONS" && request.headers.has("access-control-request-method")
    ) {
      const method = request.headers.get("access-control-request-method") ?? "";
      if (!/^[A-Za-z-]+$/.test(method)) return new Response(null, { status: 400 });
      return new Response(null, {
        status: 204,
        headers: {
          "access-control-allow-origin": "*",
          "access-control-allow-methods": method,
          "access-control-allow-headers": request.headers.get("access-control-request-headers") ??
            "",
        },
      });
    }

    const rest = `/${segments.slice(3).map(decodeURIComponent).join("/")}${external.search}`;
    const forwarded = new Request(
      new URL(`http://plugin.invalid${rest}`),
      {
        method: request.method,
        headers: sanitizeRequestHeaders(request.headers),
        ...(request.body ? { body: request.body } : {}),
      },
    );
    // Project the live host-side signal onto the forwarded request; the
    // httpCodec carries it into the plugin (ADR 0021 decision 4/5).
    Object.defineProperty(forwarded, "signal", {
      value: request.signal,
      configurable: true,
      enumerable: true,
      writable: true,
    });

    // Promise itself is not structured-cloneable. Expose its standard observation
    // methods through the callback codec, observing rejection even if the worker never waits.
    const delivery = serveInfo.completed.then(() => true, () => false);
    const waitForDelivery = async () => {
      if (!await delivery) throw new Deno.errors.Interrupted("HTTP delivery interrupted");
    };
    const response = await callHandler(mount.handler, forwarded, {
      remoteAddr: serveInfo.remoteAddr,
      // A thenable bridging Deno's ServeHandlerInfo.completed: resolves when
      // the response (including body) is fully delivered, rejects with
      // Deno.errors.Interrupted on client disconnect (ADR 0021 decision 5).
      completed: {
        then: (resolve?: (value: void) => unknown, reject?: (error: unknown) => unknown) =>
          waitForDelivery().then(resolve, reject),
        catch: (reject: (error: unknown) => unknown) => waitForDelivery().catch(reject),
        finally: (action: () => void) => waitForDelivery().finally(action),
      },
    }).catch(() => new Response(null, { status: 500 }));

    // Header hygiene (ADR 0021 decision 8): the host owns the entrance.
    response.headers.set("referrer-policy", "no-referrer");
    if (scoped) {
      response.headers.set("access-control-allow-origin", "*");
      response.headers.set("access-control-expose-headers", "*");
      response.headers.delete("access-control-allow-credentials");
    }
    const contentType = response.headers.get("content-type") ?? "";
    if (contentType.startsWith("text/html")) {
      response.headers.set("content-security-policy", CSP_HTML);
    }
    const location = response.headers.get("location");
    if (location !== null && location.startsWith("/") && !location.startsWith("//")) {
      response.headers.set(
        "location",
        `/${segments[0]}/plugins/${encodeURIComponent(pluginId)}${location}`,
      );
    }
    return response;
  }
}

/** Strips hop-by-hop and boundary headers before the mount projection. */
function sanitizeRequestHeaders(headers: Headers): Headers {
  const sanitized = new Headers();
  for (const [name, value] of headers) {
    if (HOP_BY_HOP.includes(name.toLowerCase())) continue;
    sanitized.set(name, value);
  }
  return sanitized;
}

/** Calls the plugin handler, tolerating both `fn.call(req, info)` and direct
 * remote projections of the provided function. */
async function callHandler(
  handler: Remote<Deno.ServeHandler>,
  request: Request,
  info: unknown,
): Promise<Response> {
  const call = (handler as unknown as {
    call?: (request: Request, info: unknown) => Promise<Response>;
  }).call;
  const response =
    await (call !== undefined
      ? call.call(handler, request, info)
      : (handler as unknown as (request: Request, info: unknown) => Promise<Response>)(
        request,
        info,
      ));
  if (!(response instanceof Response)) {
    throw new Error("The plugin HTTP root did not return a Response.");
  }
  return response;
}

function notFound(): Response {
  return new Response(null, { status: 404 });
}
