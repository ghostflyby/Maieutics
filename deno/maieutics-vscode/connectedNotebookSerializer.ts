import * as vscode from "vscode";
import type { Connection } from "./connection.ts";
import { readTurnBinding } from "./cellHistory.ts";
import { historicalAssistantOutput } from "./historyRendering.ts";
import type { OutputSnapshot } from "./notebookFormat.ts";
import {
  MaieuticsNotebookSerializer,
  readStoredSessionId,
  timelineNotebookOutput,
} from "./serializer.ts";
import { TurnOutputMime } from "./turnView.ts";

/** Add connected history verification without giving portable files execution authority. */
export function connectedNotebookSerializer(options: {
  connection(signal: AbortSignal): Promise<Connection | undefined>;
  log(message: string): void;
}): vscode.NotebookSerializer {
  const serializer = new MaieuticsNotebookSerializer();
  return {
    serializeNotebook: (data, token) => serializer.serializeNotebook(data, token),
    deserializeNotebook: async (bytes, token) => {
      const data = serializer.deserializeNotebook(bytes, token);
      const sessionId = readStoredSessionId(data.metadata);
      if (
        sessionId === undefined || !/^[a-f\d]{32}$/i.test(sessionId) ||
        token.isCancellationRequested ||
        !data.cells.some((cell) =>
          readTurnBinding({ metadata: cell.metadata ?? {}, text: cell.value }) !== undefined
        )
      ) return data;
      const cancellation = new AbortController();
      const registration = token.onCancellationRequested(() => cancellation.abort());
      const signal = AbortSignal.any([cancellation.signal, AbortSignal.timeout(20_000)]);
      try {
        const attached = await options.connection(signal);
        if (attached === undefined || attached.isDisposed) return data;
        const capabilities = await attached.capabilities(signal);
        if (capabilities.rendering?.assistantVersion !== 1) return data;
        const transcript = await attached.client.transcript(sessionId, signal);
        signal.throwIfAborted();
        // Transcript version is a changing history revision, not the wire protocol version.
        if (
          attached.isDisposed || !Number.isSafeInteger(transcript.version) ||
          transcript.version < 0 ||
          transcript.sessionId !== sessionId
        ) {
          return data;
        }
        const turns = new Map(transcript.turns.map((turn) => [turn.runId, turn]));
        for (const cell of data.cells) {
          const binding = readTurnBinding({ metadata: cell.metadata ?? {}, text: cell.value });
          const turn = binding && turns.get(binding.runId);
          if (binding === undefined || turn === undefined) continue;
          cell.outputs = cell.outputs?.map((output) => {
            if (output.items.length !== 1 || output.items[0].mime !== TurnOutputMime) return output;
            const node = JSON.parse(
              new TextDecoder().decode(output.items[0].data),
            ) as OutputSnapshot;
            const verified = historicalAssistantOutput(node, binding, turn);
            return verified === undefined ? output : timelineNotebookOutput(verified, true);
          });
        }
      } catch (error) {
        if (!signal.aborted) {
          options.log(
            `Historical component verification unavailable (${
              error instanceof Error ? error.name : "error"
            }); retaining passive output.`,
          );
        }
      } finally {
        registration.dispose();
      }
      return data;
    },
  };
}
