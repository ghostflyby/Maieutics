/**
 * Plugin HTTP gateway (ADR 0021): the host-side aggregator for the plugin
 * HTTP root contract plus the loopback router that fronts it.
 *
 * Aggregator — the host is the defining worker of the SDK's `http` contract
 * (`HTTP_AGGREGATOR_SPECIFIER`, bound at SDK module load). A plugin worker's
 * `provide(http, signal(handler))` acquires this process's collection surface
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
}

/** Mount-table snapshot for registry reporting and tests. */
export interface HttpMountSnapshot {
  readonly pluginId: string;
  readonly specifier: string;
  readonly live: boolean;
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

/** The contributor's plugin directory name: the `<plugin>/` prefix. */
function pluginRoot(specifier: string): string {
  const cut = specifier.indexOf("/");
  return cut === -1 ? specifier : specifier.slice(0, cut);
}

/** The contributor's canonical specifier from its contribution key. */
function contributorSpecifier(providerKey: string): string {
  const cut = providerKey.lastIndexOf(":");
  return cut === -1 ? providerKey : providerKey.slice(0, cut);
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
  readonly #routerListeners = new Set<() => void>();
  #router: { addr: Deno.NetAddr; stop: () => Promise<void> } | undefined;

  constructor() {
    // One root per plugin (ADR 0021 decision 2/9): the second contributor
    // from the same plugin fails at its own provide() call site.
    setAdmissionHook(CONTRACT_NAME, (context) => {
      const root = pluginRoot(context.providerSpecifier);
      const duplicate = context.existingProviders.some(
        (specifier) =>
          specifier !== context.providerSpecifier &&
          pluginRoot(specifier) === root,
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
    }));
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
  admit(frame: AdmissionRequestFrame, providerSpecifier: string): void {
    if (!(frame.sab instanceof SharedArrayBuffer)) return;
    const mailbox = admissionMailboxFor(frame.sab);
    answerAdmission(mailbox, () =>
      evaluateAdmissionHook(frame.ep ?? CONTRACT_NAME, {
        extensionPoint: frame.ep ?? CONTRACT_NAME,
        providerSpecifier,
        providerModule: frame.providerModule ?? "",
        existingProviders: [
          ...new Set(
            [...this.#mounts.values()].map((mount) => mount.specifier),
          ),
        ],
      }));
  }

  /**
   * Serves the http contract's collection surface on the acquired port — the
   * host is the collection owner, so the acquire router hands the port here
   * instead of forwarding to a plugin worker.
   */
  serveCollection(port: MessagePort): void {
    const registry = new PayloadCodecRegistry();
    for (const codec of [actorRefCodec, collectionStreamCodec, httpCodec]) {
      registry.register(codec);
    }
    for (const codec of [iterableCodec, errorCodec, abortSignalCodec, callbackCodec]) {
      if (!registry.has(codec.tag)) registry.register(codec);
    }
    const channel = connectChannel(port);
    registry.registerChannel(channel);
    const handler = makeRpcHandler(this.#collectionApi(), registry);
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

  #collectionApi(): Record<string, (...args: unknown[]) => unknown> {
    const add = async (...args: unknown[]): Promise<void> => {
      const initial = args[0];
      const changes = args[1] as AsyncIterable<unknown>;
      const providerKey = args[2];
      if (typeof providerKey === "string" && providerKey.length > 0) {
        this.#mount(providerKey, initial);
        void this.#pullChanges(providerKey, changes);
      }
    };
    const remove = async (...args: unknown[]): Promise<void> => {
      const providerKey = args[0];
      if (typeof providerKey === "string") this.#unmount(providerKey);
    };
    return {
      [`${CONTRACT_NAME}.add`]: add,
      [`${CONTRACT_NAME}.remove`]: remove,
    };
  }

  #mount(providerKey: string, handler: unknown): void {
    const specifier = contributorSpecifier(providerKey);
    const mount: HttpMount = {
      pluginId: pluginRoot(specifier),
      providerKey,
      specifier,
      handler: handler as Remote<Deno.ServeHandler>,
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
  async #pullChanges(providerKey: string, changes: AsyncIterable<unknown>): Promise<void> {
    try {
      for await (const value of changes) {
        if (value === undefined) this.#unmount(providerKey);
        else this.#mount(providerKey, value);
      }
    } catch {
      // A failed change stream means the provider is unreachable.
    }
    this.#unmount(providerKey);
  }

  #notifyMounts(): void {
    for (const listener of this.#routerListeners) listener();
  }

  // —— Router ——

  /**
   * Starts the loopback listener. The token gates every request; the URL
   * shape is `/<token>/plugins/<pluginId>/<path>`.
   */
  async startRouter(options: {
    hostname?: string;
    port?: number;
    token: string;
    onListening?: (address: Deno.NetAddr) => void;
  }): Promise<Deno.NetAddr> {
    if (this.#router !== undefined) return this.#router.addr;
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
    return addr;
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
      segments.length < 3 || !timingSafeEqual(segments[0], token) ||
      segments[1] !== "plugins"
    ) {
      return notFound();
    }
    const pluginId = decodeURIComponent(segments[2]);
    const mount = [...this.#mounts.values()].find((m) => m.pluginId === pluginId);
    if (mount === undefined) return notFound();

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

    const response = await callHandler(mount.handler, forwarded, {
      remoteAddr: serveInfo.remoteAddr,
      // A thenable bridging Deno's ServeHandlerInfo.completed: resolves when
      // the response (including body) is fully delivered, rejects with
      // Deno.errors.Interrupted on client disconnect (ADR 0021 decision 5).
      completed: serveInfo.completed,
    });

    // Header hygiene (ADR 0021 decision 8): the host owns the entrance.
    response.headers.set("referrer-policy", "no-referrer");
    const contentType = response.headers.get("content-type") ?? "";
    if (contentType.startsWith("text/html")) {
      response.headers.set("content-security-policy", CSP_HTML);
    }
    const location = response.headers.get("location");
    if (location !== null && location.startsWith("/") && !location.startsWith("//")) {
      response.headers.set(
        "location",
        `/${token}/plugins/${encodeURIComponent(pluginId)}${location}`,
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
  info: Deno.ServeHandlerInfo<Deno.NetAddr>,
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
