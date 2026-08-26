/**
 * Widget runtime: model-instance registry plus bidirectional comm routing.
 *
 * The runtime owns the ipywidgets protocol wire shapes for the kernel side:
 *   - comm_open   (target "jupyter.widget") carries the initial model state;
 *   - comm_msg    carries {method:"update", state, buffer_paths} in both
 *                 directions (kernel -> frontend via sync(), frontend ->
 *                 kernel via handleIncoming());
 *   - display     is a `application/vnd.jupyter.widget-view+json` bundle with
 *                 {model_id, version_major, version_minor}.
 *
 * It is transport-neutral: the host supplies a broadcast function (Maieutics
 * wires `Deno.jupyter.broadcast`) and a comm subscription (Maieutics wires
 * `maieutics.comm.on`). Controls rendering itself lives entirely in the
 * frontend; this runtime only maintains model state and routes updates.
 */

export const WIDGET_COMM_TARGET = "jupyter.widget";
export const WIDGET_PROTOCOL_VERSION_MAJOR = 2;
export const WIDGET_PROTOCOL_VERSION_MINOR = 0;

/** A broadcast function compatible with `Deno.jupyter.broadcast`'s comm surface. */
export type WidgetBroadcast = (
  messageType: string,
  content: Record<string, unknown>,
  extra?: { metadata?: Record<string, unknown>; buffers?: Uint8Array[] },
) => Promise<void>;

/** An incoming comm event delivered by the host (mirrors `maieutics.comm.on`). */
export interface IncomingCommMessage {
  kind: number;
  commId: string;
  targetName?: string;
  data?: unknown;
  buffers: Uint8Array[];
}

export interface WidgetModelOptions<State extends Record<string, unknown>> {
  /** Identity + initial state merged into the comm_open payload. */
  state: State;
  /** Called when the frontend changes a state key (frontend -> kernel). */
  onChange?: (key: string & keyof State, value: State[keyof State]) => void;
}

/** A registered widget model instance. */
export interface WidgetModel<State extends Record<string, unknown>> {
  readonly commId: string;
  get<K extends keyof State>(key: K): State[K];
  set<K extends keyof State>(key: K, value: State[K]): void;
  /** Broadcast an `update` for one state key to the frontend. */
  sync<K extends keyof State>(key: K, value: State[K]): Promise<void>;
  /** The display MIME bundle for this model. */
  mimeBundle(): Record<string, unknown>;
}

interface RegisteredModel {
  state: Record<string, unknown>;
  onChange: (key: string, value: unknown) => void;
}

export class WidgetRuntime {
  readonly #broadcast: WidgetBroadcast;
  readonly #models = new Map<string, RegisteredModel>();

  constructor(broadcast: WidgetBroadcast) {
    this.#broadcast = broadcast;
  }

  /** The protocol identity fields shared by every classic controls model. */
  static identityFields(
    modelName: string,
    viewName: string,
  ): Record<string, unknown> {
    return {
      _model_module: "@jupyter-widgets/controls",
      _model_name: modelName,
      _model_module_version: "^2.0.0",
      _view_module: "@jupyter-widgets/controls",
      _view_name: viewName,
      _view_module_version: "^2.0.0",
      _view_count: null,
    };
  }

  /**
   * Register a nested model (layout/style) and broadcast its comm_open. Nested
   * models live in `@jupyter-widgets/base` (not controls) and have no view;
   * they are referenced from a control's state via `IPY_MODEL_<commId>`.
   */
  initNested<State extends Record<string, unknown>>(
    commId: string,
    modelName: "LayoutModel" | "StyleModel",
    state: State,
  ): WidgetModel<State> {
    const registered: RegisteredModel = {
      state: { ...state },
      onChange: () => {},
    };
    this.#models.set(commId, registered);
    void this.#broadcast(
      "comm_open",
      {
        comm_id: commId,
        target_name: WIDGET_COMM_TARGET,
        data: {
          state: {
            _model_module: "@jupyter-widgets/base",
            _model_name: modelName,
            _model_module_version: "^2.0.0",
            _view_count: null,
            ...state,
          },
          buffer_paths: [],
        },
      },
      {
        metadata: {
          version: `${WIDGET_PROTOCOL_VERSION_MAJOR}.${WIDGET_PROTOCOL_VERSION_MINOR}.0`,
        },
      },
    );
    return {
      commId,
      get: <K extends keyof State>(key: K): State[K] =>
        this.#requireModel(commId).state[key as string] as State[K],
      set: <K extends keyof State>(key: K, value: State[K]): void => {
        this.#requireModel(commId).state[key as string] = value;
      },
      sync: async <K extends keyof State>(key: K, value: State[K]): Promise<void> => {
        const model = this.#requireModel(commId);
        model.state[key as string] = value;
        await this.#broadcast(
          "comm_msg",
          {
            comm_id: commId,
            data: {
              method: "update",
              state: { [key]: value },
              buffer_paths: [],
            },
          },
        );
      },
      mimeBundle: () => ({ "text/plain": `IPY_MODEL_${commId}` }),
    };
  }

  /** The `IPY_MODEL_<commId>` reference string for a nested model. */
  static modelRef(commId: string): string {
    return `IPY_MODEL_${commId}`;
  }

  /**
   * Register a model and broadcast its comm_open. Returns the created model.
   * The comm_open `state` carries the identity fields plus the caller state.
   */
  init<State extends Record<string, unknown>>(
    commId: string,
    state: State,
    onChange?: (key: string & keyof State, value: State[keyof State]) => void,
  ): WidgetModel<State> {
    const registered: RegisteredModel = {
      state: { ...state },
      onChange: (key, value) =>
        onChange?.(key as string & keyof State, value as State[keyof State]),
    };
    this.#models.set(commId, registered);

    // Broadcast is async; the open is intentionally not awaited so display can
    // proceed without serializing the frontend round trip. Errors surface via
    // the returned model's sync/display path.
    void this.#broadcast(
      "comm_open",
      {
        comm_id: commId,
        target_name: WIDGET_COMM_TARGET,
        data: {
          state: { ...state },
          buffer_paths: [],
        },
      },
      {
        metadata: {
          version: `${WIDGET_PROTOCOL_VERSION_MAJOR}.${WIDGET_PROTOCOL_VERSION_MINOR}.0`,
        },
      },
    );

    return {
      commId,
      get: <K extends keyof State>(key: K): State[K] =>
        this.#requireModel(commId).state[key as string] as State[K],
      set: <K extends keyof State>(key: K, value: State[K]): void => {
        this.#requireModel(commId).state[key as string] = value;
      },
      sync: async <K extends keyof State>(key: K, value: State[K]): Promise<void> => {
        const model = this.#requireModel(commId);
        model.state[key as string] = value;
        await this.#broadcast(
          "comm_msg",
          {
            comm_id: commId,
            data: {
              method: "update",
              state: { [key]: value },
              buffer_paths: [],
            },
          },
        );
      },
      mimeBundle: () => ({
        "application/vnd.jupyter.widget-view+json": {
          model_id: commId,
          version_major: WIDGET_PROTOCOL_VERSION_MAJOR,
          version_minor: WIDGET_PROTOCOL_VERSION_MINOR,
        },
      }),
    };
  }

  /**
   * Route an incoming comm message from the frontend. `comm_msg` with
   * `method: "update"` applies the state delta to the registered model and
   * invokes its onChange callback; unknown comm ids are ignored (the kernel
   * keeps running per invariant 18).
   */
  handleIncoming(message: IncomingCommMessage): void {
    if (message.kind !== 1 || message.data === undefined) return;
    const model = this.#models.get(message.commId);
    if (model === undefined) return;
    const data = message.data as Record<string, unknown>;
    if (data.method !== "update") return;
    const state = data.state;
    if (typeof state !== "object" || state === null) return;
    for (const [key, value] of Object.entries(state as Record<string, unknown>)) {
      if (key in model.state) {
        model.state[key] = value;
        model.onChange(key, value);
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

  #requireModel(commId: string): RegisteredModel {
    const model = this.#models.get(commId);
    if (model === undefined) {
      throw new Error(`No widget model is registered for comm '${commId}'.`);
    }
    return model;
  }
}
