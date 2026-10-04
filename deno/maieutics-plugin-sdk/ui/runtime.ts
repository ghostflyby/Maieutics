/**
 * UI model runtime: the family-agnostic producer-side registry for native
 * view families (ADR 0038). Mirrors `widgets/runtime.ts` in shape —
 * transport-neutral, injected broadcast + comm subscription — but dispatches
 * every wire shape through the model's family contract instead of hard-coding
 * the ipywidgets dialect.
 *
 * The runtime owns no transport: the REPL worker binds one at bootstrap via
 * `bindUiHost` (see `index.ts`); tests bind fakes. Model lifetime follows
 * comm lifetime exactly like widgets: registration broadcasts the family's
 * `comm_open`, a frontend close removes the model, uplink frames route by
 * comm id, and unknown ids are ignored (the kernel keeps running, invariant
 * 18).
 */

import type { UiBroadcast, UiIncomingMessage, ViewFamilyContract } from "./family.ts";

export interface UiModelHandlers {
  /** Called per uplink state key the family decoded. */
  onChange?: (key: string, value: unknown) => void;
  /** Called for one-shot uplink actions (form submit, cancel, …). */
  onEvent?: (name: string, payload?: unknown) => void;
}

/**
 * Bundled-family payload (ADR 0038 §4, stage 1b): producer-authored
 * component code that the renderer materializes inline. The source runs in
 * the renderer webview under the notebook CSP (`'unsafe-eval'` is allowed
 * there; URL-based module imports are not), so delivery is source text, not
 * a module URL.
 */
export interface BundledFamilySource {
  /** Plain JS (no imports) calling the injected `registerViewFamily`. */
  readonly esmSource: string;
  /** Optional stylesheet scoped to the family's mount points. */
  readonly cssSource?: string;
}

/** Upper bound for a bundled source riding an announcement. */
export const MAX_BUNDLED_SOURCE_BYTES = 1024 * 1024;

/** A registered UI model. Displayable: carries the Jupyter.display symbol. */
export interface UiModel<State extends Record<string, unknown> = Record<string, unknown>> {
  readonly commId: string;
  readonly family: string;
  get<K extends keyof State>(key: K): State[K];
  set<K extends keyof State>(key: K, value: State[K]): void;
  /** Apply + broadcast one state key (the family's update dialect). */
  sync<K extends keyof State>(key: K, value: State[K]): Promise<void>;
  /** The display MIME bundle member for this model. */
  announce(): Record<string, unknown>;
}

interface RegisteredModel {
  family: ViewFamilyContract;
  state: Record<string, unknown>;
  handlers: UiModelHandlers;
}

export class UiModelRuntime {
  readonly #broadcast: UiBroadcast;
  readonly #models = new Map<string, RegisteredModel>();
  readonly #families = new Map<string, { contract: ViewFamilyContract; bundled?: BundledFamilySource }>();

  constructor(broadcast: UiBroadcast) {
    this.#broadcast = broadcast;
  }

  /** Register a family's wire contract, optionally with the bundled
   * component source every announcement of the family embeds (stage 1b). */
  registerFamily(contract: ViewFamilyContract, bundled?: BundledFamilySource): void {
    if (bundled !== undefined) {
      const bytes = new TextEncoder().encode(bundled.esmSource).length;
      if (bytes > MAX_BUNDLED_SOURCE_BYTES) {
        throw new Error(
          `Bundled source for '${contract.family}' is ${bytes} bytes; the ceiling is ${MAX_BUNDLED_SOURCE_BYTES}.`,
        );
      }
    }
    this.#families.set(contract.family, { contract, bundled });
  }

  /** True when a family contract is registered. */
  hasFamily(family: string): boolean {
    return this.#families.has(family);
  }

  /** Register a model and broadcast its family's comm_open. */
  create<State extends Record<string, unknown>>(
    family: string,
    state: State,
    handlers: UiModelHandlers = {},
  ): UiModel<State> {
    const entry = this.#families.get(family);
    if (entry === undefined) {
      throw new Error(`Unknown view family '${family}'.`);
    }
    const contract = entry.contract;
    const commId = crypto.randomUUID();
    const model: RegisteredModel = {
      family: contract,
      state: { ...state },
      handlers,
    };
    this.#models.set(commId, model);
    // Broadcast is async; the open is intentionally not awaited so display
    // can proceed without serializing the frontend round trip (mirrors the
    // widget runtime's posture).
    void this.#broadcast("comm_open", {
      comm_id: commId,
      target_name: contract.target,
      data: contract.commOpenData(model.state),
    });
    return this.#wrap<State>(commId, entry.bundled);
  }

  /** Route an incoming comm message from the frontend. */
  handleIncoming(message: UiIncomingMessage): void {
    if (message.kind !== 1) return;
    const model = this.#models.get(message.commId);
    if (model === undefined) return;
    const dispatch = model.family.decodeIncoming(message.data);
    if (dispatch === undefined) return;
    if (dispatch.event !== undefined) {
      model.handlers.onEvent?.(dispatch.event.name, dispatch.event.payload);
    }
    if (dispatch.updates !== undefined) {
      for (const [key, value] of Object.entries(dispatch.updates)) {
        if (key in model.state) {
          model.state[key] = value;
          model.handlers.onChange?.(key, value);
        }
      }
    }
  }

  /** True when a model with this comm id is registered. */
  has(commId: string): boolean {
    return this.#models.has(commId);
  }

  /** Remove a model (e.g. on comm_close); unknown ids are a no-op. */
  remove(commId: string): void {
    this.#models.delete(commId);
  }

  #wrap<State extends Record<string, unknown>>(
    commId: string,
    bundled: BundledFamilySource | undefined,
  ): UiModel<State> {
    const requireModel = (): RegisteredModel => {
      const model = this.#models.get(commId);
      if (model === undefined) {
        throw new Error(`No UI model is registered for comm '${commId}'.`);
      }
      return model;
    };
    return {
      commId,
      family: requireModel().family.family,
      get: <K extends keyof State>(key: K): State[K] =>
        requireModel().state[key as string] as State[K],
      set: <K extends keyof State>(key: K, value: State[K]): void => {
        requireModel().state[key as string] = value;
      },
      sync: async <K extends keyof State>(key: K, value: State[K]): Promise<void> => {
        const model = requireModel();
        model.state[key as string] = value;
        await this.#broadcast("comm_msg", {
          comm_id: commId,
          data: model.family.commUpdateData(key as string, value),
        });
      },
      announce: (): Record<string, unknown> => {
        const model = requireModel();
        const announcement = model.family.announcement(commId, model.state);
        if (bundled !== undefined) {
          // The bundled component source rides every announcement so any
          // renderer can bootstrap the family (stage 1b).
          return {
            [model.family.displayMime]: { ...announcement, esmSource: bundled.esmSource, ...(bundled.cssSource === undefined ? {} : { cssSource: bundled.cssSource }) },
          };
        }
        return { [model.family.displayMime]: announcement };
      },
    };
  }
}
