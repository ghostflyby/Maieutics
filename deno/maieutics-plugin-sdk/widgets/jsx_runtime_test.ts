import { assertEquals } from "@std/assert";
import { Fragment, jsx, jsxs } from "./jsx-runtime.ts";
import { bindWidgetHost, IntSlider } from "./index.ts";

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

Deno.test("jsx factory maps style={{}} to a nested per-control StyleModel", () => {
  const { opens } = bindFakeHost();
  const model = jsx("IntSlider", {
    value: 5,
    style: { fontSize: "14px", handleColor: "#f00" },
  }) as { get(key: string): unknown };

  // The control's state.style is the IPY_MODEL_ reference.
  const styleRef = model.get("style") as string;
  assertEquals(styleRef.startsWith("IPY_MODEL_"), true);

  // Two comm_opens: the control plus its nested per-control SliderStyleModel.
  assertEquals(opens.length, 2);
  const nested = opens.find((open) => {
    const state = (open.data as { state: Record<string, unknown> }).state;
    return state._model_name === "SliderStyleModel";
  })!;
  assertEquals(nested.target_name, "jupyter.widget");
  const nestedState = (nested.data as { state: Record<string, unknown> }).state;
  assertEquals(nestedState._model_module, "@jupyter-widgets/controls");
  assertEquals(nestedState.font_size, "14px");
  assertEquals(nestedState.handle_color, "#f00");
});

Deno.test("jsx factory maps style={{}} to both LayoutModel and per-control StyleModel", () => {
  const { opens } = bindFakeHost();
  const model = jsx("IntSlider", {
    value: 5,
    style: { maxWidth: "200px", fontSize: "14px", handleColor: "#f00" },
  }) as { get(key: string): unknown };

  assertEquals((model.get("layout") as string).startsWith("IPY_MODEL_"), true);
  assertEquals((model.get("style") as string).startsWith("IPY_MODEL_"), true);
  // Three comm_opens: the control + LayoutModel + SliderStyleModel.
  assertEquals(opens.length, 3);
  const layout = opens.find((open) =>
    (open.data as { state: Record<string, unknown> }).state._model_name === "LayoutModel"
  )!;
  const style = opens.find((open) =>
    (open.data as { state: Record<string, unknown> }).state._model_name === "SliderStyleModel"
  )!;
  assertEquals((layout.data as { state: Record<string, unknown> }).state.max_width, "200px");
  const styleState = (style.data as { state: Record<string, unknown> }).state;
  assertEquals(styleState.font_size, "14px");
  assertEquals(styleState.handle_color, "#f00");
});

Deno.test("jsx factory accepts the component identifier form and yields a displayable model", async () => {
  const { opens } = bindFakeHost();
  // `<IntSlider />` compiles to jsx(IntSlider, ...): the factory function
  // itself, tagged with its control kind.
  const model = jsx(IntSlider, { value: 7, min: 0, max: 10 }) as {
    commId: string;
    get(key: string): unknown;
  } & { [DISPLAY]: () => Promise<Record<string, unknown>> };

  assertEquals(model.get("value"), 7);
  assertEquals(opens.length, 1);
  const state = (opens[0].data as { state: Record<string, unknown> }).state;
  assertEquals(state._model_name, "IntSliderModel");
  const bundle = await (model[DISPLAY] as () => Promise<Record<string, unknown>>)();
  const view = bundle["application/vnd.jupyter.widget-view+json"] as { model_id: string };
  assertEquals(view.model_id, model.commId);
});

Deno.test("jsx factory renders fragments as a Box container", () => {
  const { opens } = bindFakeHost();
  const model = jsx(Fragment, {
    children: jsx("IntSlider", { value: 1 }),
  }) as { get(key: string): unknown } & {
    [DISPLAY]: () => Promise<Record<string, unknown>>;
  };

  // Fragment -> BoxModel with children = [IPY_MODEL_<slider>].
  assertEquals((model.get("children") as string[])[0].startsWith("IPY_MODEL_"), true);
  // Two comm_opens: the Box plus the nested IntSlider.
  assertEquals(opens.length, 2);
  const boxOpen = opens.find((open) =>
    (open.data as { state: Record<string, unknown> }).state._model_name === "BoxModel"
  )!;
  assertEquals(boxOpen !== undefined, true);
});
