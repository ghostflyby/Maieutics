/** Process ownership adapter used after the host has launched the child. */
import type { Codec } from "./codec.ts";
import type { CodecValueTypes } from "./type-utils.ts";
import type { Transport } from "./transport.ts";
import { aggregateFailures } from "./failures.ts";
import { type ActorHandle, type Remote, spawn, type SpawnOptions } from "../spawn_worker.ts";

export interface ProcessActorOwner {
  readonly transport: Transport;
  readonly completion: Promise<void>;
  stop(): Promise<void>;
}

/** Establish the Actor while retaining both startup and child-shutdown failures. */
export async function connectProcessActor<
  T,
  const C extends readonly Codec<unknown>[] = readonly Codec<unknown>[],
>(
  owner: ProcessActorOwner,
  options: SpawnOptions<C> = {},
): Promise<Remote<T, CodecValueTypes<C>> & ActorHandle> {
  void owner.completion.catch(() => {});
  let actor: Remote<T, CodecValueTypes<C>> & ActorHandle;
  try {
    actor = await spawn<T, C>(owner.transport, options);
  } catch (error) {
    try {
      await owner.stop();
    } catch (cleanup) {
      throw aggregateFailures([error, cleanup], "Actor process startup and cleanup failed", {
        cause: error,
      });
    }
    throw error;
  }
  const completion = Promise.allSettled([actor.completion, owner.completion]).then((results) => {
    const errors = results.flatMap((result) => result.status === "rejected" ? [result.reason] : []);
    if (errors.length) throw aggregateFailures(errors, "Actor process cleanup failed");
  });
  void completion.catch(() => {});
  return new Proxy(actor, {
    get(target, property) {
      if (property === "completion") return completion;
      if (property === "dispose") {
        return () => {
          void target.dispose();
          return completion;
        };
      }
      return Reflect.get(target, property);
    },
  });
}
