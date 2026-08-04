/**
 * Client for the Maieutics REPL control channel.
 *
 * The kernel injects the channel address through the `MAIEUTICS_REPL_IPC`
 * environment variable and serves HTTP and WebSocket endpoints on a unix
 * domain socket. Tool and widget APIs are pending design and will extend
 * this module's exports.
 */

const ADDRESS_ENV = "MAIEUTICS_REPL_IPC";
const SERVER_URL = "http://localhost";

export interface ReplClientOptions {
  /** Unix domain socket path of the kernel control channel. */
  address?: string;
}

export interface ReplClient {
  /** Unix domain socket path of the kernel control channel. */
  readonly address: string;
  /** Probes the kernel control channel health endpoint. */
  health(): Promise<string>;
}

let defaultClient: ReplClient | undefined;

/** Creates a control channel client for the given or env-provided socket address. */
export function connect(options: ReplClientOptions = {}): ReplClient {
  const address = options.address ?? Deno.env.get(ADDRESS_ENV);
  if (!address) {
    throw new Error(
      `Missing ${ADDRESS_ENV} environment variable; cannot connect to the REPL control channel.`,
    );
  }

  const http = Deno.createHttpClient({
    proxy: { transport: "unix", path: address },
  });
  const health = async (): Promise<string> => {
    const response = await fetch(`${SERVER_URL}/health`, { client: http });
    if (!response.ok) {
      throw new Error(
        `REPL control channel health check failed with status ${response.status}.`,
      );
    }
    return response.text();
  };
  return { address, health };
}

/** Returns the process-wide default client, created lazily from the environment. */
export function repl(): ReplClient {
  defaultClient ??= connect();
  return defaultClient;
}

/** Probes the default client. Convenience for scripts that use the module namespace directly. */
export async function health(): Promise<string> {
  return repl().health();
}
