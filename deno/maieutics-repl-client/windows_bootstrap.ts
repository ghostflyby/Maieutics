/**
 * Windows named-pipe credential bootstrap. Deno cannot connect named pipes natively, so the
 * REPL uses FFI against kernel32: it opens the kernel-provided pipe, reads one JSON payload
 * with the session-bound credential, and closes the handle. The kernel verifies the client
 * process id before writing the credential.
 */

export interface BootstrapCredential {
  sessionId: string;
  credential: string;
}

const GENERIC_READ = 0x80000000;
const GENERIC_WRITE = 0x40000000;
const OPEN_EXISTING = 3;
const MAX_PAYLOAD_BYTES = 4096;

export function bootstrapWindowsCredential(
  pipeName: string,
): BootstrapCredential {
  if (Deno.build.os !== "windows") {
    throw new Error(
      "The named-pipe bootstrap is only available on Windows.",
    );
  }

  const systemRoot = Deno.env.get("SystemRoot");
  if (systemRoot === undefined || systemRoot.length === 0) {
    throw new Error("SystemRoot is required to resolve kernel32.dll.");
  }
  const kernel32Path = `${systemRoot}\\System32\\kernel32.dll`;

  const kernel32 = Deno.dlopen(
    kernel32Path,
    {
      CreateFileW: {
        parameters: [
          "pointer",
          "u32",
          "u32",
          "pointer",
          "u32",
          "u32",
          "pointer",
        ],
        result: "pointer",
      },
      ReadFile: {
        parameters: ["pointer", "pointer", "u32", "pointer", "pointer"],
        result: "i32",
      },
      CloseHandle: { parameters: ["pointer"], result: "i32" },
      GetLastError: { parameters: [], result: "u32" },
    } as const,
  );

  const path = `\\\\.\\pipe\\${pipeName}`;
  const pathBytes = new Uint8Array((path.length + 1) * 2);
  for (let index = 0; index < path.length; index++) {
    const code = path.charCodeAt(index);
    pathBytes[index * 2] = code & 0xff;
    pathBytes[index * 2 + 1] = code >> 8;
  }

  const handle = kernel32.symbols.CreateFileW(
    Deno.UnsafePointer.of(pathBytes),
    GENERIC_READ | GENERIC_WRITE,
    0,
    null,
    OPEN_EXISTING,
    0,
    null,
  );
  if (handle === null || handle === undefined) {
    throw new Error(
      `Failed to open the credential pipe: GetLastError=${kernel32.symbols.GetLastError()}`,
    );
  }

  try {
    const buffer = new Uint8Array(MAX_PAYLOAD_BYTES);
    const bytesRead = new Uint8Array(4);
    const ok = kernel32.symbols.ReadFile(
      handle,
      Deno.UnsafePointer.of(buffer),
      buffer.length,
      Deno.UnsafePointer.of(bytesRead),
      null,
    );
    if (ok === 0) {
      throw new Error(
        `Failed to read the credential: GetLastError=${kernel32.symbols.GetLastError()}`,
      );
    }
    const count = new DataView(bytesRead.buffer).getUint32(0, true);
    const text = new TextDecoder().decode(buffer.subarray(0, count));
    const credential = JSON.parse(text) as BootstrapCredential;
    if (
      typeof credential.credential !== "string" ||
      typeof credential.sessionId !== "string"
    ) {
      throw new Error(
        "The bootstrap payload is not a credential envelope.",
      );
    }
    return credential;
  } finally {
    kernel32.symbols.CloseHandle(handle);
  }
}
