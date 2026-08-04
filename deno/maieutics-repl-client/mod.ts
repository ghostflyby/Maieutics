/**
 * Client for the Maieutics REPL control channel.
 *
 * The kernel injects the channel address through the `MAIEUTICS_REPL_IPC`
 * environment variable and serves HTTP and WebSocket endpoints on a unix
 * domain socket. Widget APIs are pending design; health and tool invocation
 * are available today.
 */

const ADDRESS_ENV = "MAIEUTICS_REPL_IPC";
const SERVER_URL = "http://localhost";

type HttpClient = ReturnType<typeof Deno.createHttpClient>;

export interface ReplClientOptions {
  /** Unix domain socket path of the kernel control channel. */
  address?: string;
}

export interface ReplTools {
  /** Invokes a script-callable workspace tool and returns its structured result. */
  invoke(name: string, args?: Record<string, unknown>): Promise<unknown>;
}

export interface ReplClient {
  /** Unix domain socket path of the kernel control channel. */
  readonly address: string;
  /** Probes the kernel control channel health endpoint. */
  health(): Promise<string>;
  /** Script tool invocation. */
  tools: ReplTools;
}

function createHttp(address: string): HttpClient {
  return Deno.createHttpClient({ proxy: { transport: "unix", path: address } });
}

async function healthProbe(http: HttpClient): Promise<string> {
  const response = await fetch(`${SERVER_URL}/health`, { client: http });
  if (!response.ok) {
    throw new Error(
      `REPL control channel health check failed with status ${response.status}.`,
    );
  }
  return response.text();
}

function createTools(http: HttpClient): ReplTools {
  return {
    async invoke(name, args = {}) {
      const response = await fetch(`${SERVER_URL}/v1/tool.invoke`, {
        client: http,
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ version: 1, tool: name, arguments: args }),
      });
      if (!response.ok) {
        throw new Error(`Tool invocation failed with status ${response.status}.`);
      }
      const envelope = await response.json() as {
        status?: string;
        code?: string;
        message?: string;
        value?: unknown;
      };
      if (envelope?.status !== "ok") {
        throw new Error(
          `${envelope?.code ?? "tool_failed"}: ${envelope?.message ?? "the tool failed"}`,
        );
      }
      return envelope.value;
    },
  };
}

let defaultAddress: string | undefined;
let defaultHttp: HttpClient | undefined;

function ensureDefault(): HttpClient {
  if (defaultHttp !== undefined) {
    return defaultHttp;
  }
  const address = defaultAddress ??= Deno.env.get(ADDRESS_ENV);
  if (!address) {
    throw new Error(
      `Missing ${ADDRESS_ENV} environment variable; cannot connect to the REPL control channel.`,
    );
  }
  defaultHttp = createHttp(address);
  return defaultHttp;
}

/** Creates a control channel client for the given or env-provided socket address. */
export function connect(options: ReplClientOptions = {}): ReplClient {
  const address = options.address ?? Deno.env.get(ADDRESS_ENV);
  if (!address) {
    throw new Error(
      `Missing ${ADDRESS_ENV} environment variable; cannot connect to the REPL control channel.`,
    );
  }
  const http = createHttp(address);
  return { address, health: () => healthProbe(http), tools: createTools(http) };
}

/** Probes the default client. Convenience for scripts that use the module namespace directly. */
export async function health(): Promise<string> {
  return healthProbe(ensureDefault());
}

/** Script tool invocation against the default client. */
export const tools: ReplTools = {
  invoke: (name, args) => createTools(ensureDefault()).invoke(name, args),
};
