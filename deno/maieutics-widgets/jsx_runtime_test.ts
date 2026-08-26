import { assertEquals } from "@std/assert";
import { jsx, jsxs } from "./jsx-runtime.ts";
import { bindWidgetHost } from "./index.ts";

const DISPLAY = Symbol.for("Jupyter.display");

function bindFakeHost(): { opens: Array<Record<string, unknown>> } {
  const opens: Array<Record<string, unknown>> = [];
  bindWidgetHost({
    broadcast: async (messageType: string, content: Record<string, unknown>) => {
      if (messageType === "comm_open") opens.push(content);
    },
    onComm: () => {},
  });
  return { opens };
}

Deno.test("jsx factory turns a known control tag into a widget model", async () => {
  const { opens } = bindFakeHost();
  const model = jsx("IntSlider", { value: 5, min: 0, max: 10 }) as unknown as {
    commId: string;
    get(key: string): unknown;
  } & { [DISPLAY]: () => Promise<Record<string, unknown>> };

  // comm_open was broadcast with the classic controls identity.
  assertEquals(opens.length, 1);
  const open = opens[0].data as { state: Record<string, unknown> };
  assertEquals(open.state._model_name, "IntSliderModel");
  assertEquals(open.state.value, 5);

  // The element is displayable: $display yields the widget-view bundle.
  const bundle = await (model[DISPLAY] as () => Promise<Record<string, unknown>>)();
  const view = bundle["application/vnd.jupyter.widget-view+json"] as { model_id: string };
  assertEquals(view.model_id, model.commId);
  assertEquals(model.get("value"), 5);
});

Deno.test("jsx factory leaves unknown tags as inert vnodes", () => {
  const vnode = jsx("div", { class: "x" });
  assertEquals(vnode, { type: "div", props: { class: "x" }, key: null });
});

Deno.test("jsxs factory handles multiple children for control elements", () => {
  const { opens } = bindFakeHost();
  const model = jsxs("IntSlider", { value: 3 }) as { get(key: string): unknown };
  assertEquals(opens.length, 1);
  assertEquals(model.get("value"), 3);
});

Deno.test("jsx factory maps style={{}} to a nested StyleModel reference", () => {
  const { opens } = bindFakeHost();
  const model = jsx("IntSlider", { value: 5, style: { width: "100%", alignItems: "center" } }) as {
    get(key: string): unknown;
  };

  // The control's state.style is the IPY_MODEL_ reference.
  const styleRef = model.get("style") as string;
  assertEquals(styleRef.startsWith("IPY_MODEL_"), true);

  // Two comm_opens: the control plus its nested StyleModel.
  assertEquals(opens.length, 2);
  const nested = opens.find((open) => {
    const state = (open.data as { state: Record<string, unknown> }).state;
    return state._model_name === "StyleModel";
  })!;
  assertEquals(nested.target_name, "jupyter.widget");
  const nestedState = (nested.data as { state: Record<string, unknown> }).state;
  assertEquals(nestedState._model_module, "@jupyter-widgets/base");
  assertEquals(nestedState.width, "100%");
  assertEquals(nestedState.align_items, "center");
});
