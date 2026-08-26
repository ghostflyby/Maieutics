import { assertEquals } from "@std/assert";
import {
  bindCssProps,
  bindNestedStyle,
  isCssBlock,
  splitCssProps,
  styleModelFor,
} from "./style.ts";
import { WidgetRuntime } from "./runtime.ts";

function fakeRuntime(): { runtime: WidgetRuntime; opens: Array<Record<string, unknown>> } {
  const opens: Array<Record<string, unknown>> = [];
  const runtime = new WidgetRuntime(async (messageType, content) => {
    if (messageType === "comm_open") opens.push(content);
  });
  return { runtime, opens };
}

Deno.test("splitCssProps routes layout traits to layout and style traits to style", () => {
  const split = splitCssProps({
    alignItems: "center",
    maxWidth: "200px",
    fontSize: "14px",
    color: "red",
    width: "100%", // layout-owned (the single cross-domain key)
  });
  assertEquals(split.layout, {
    align_items: "center",
    max_width: "200px",
    width: "100%",
  });
  assertEquals(split.style, {
    font_size: "14px",
    color: "red",
  });
});

Deno.test("splitCssProps keeps unknown keys on the layout side", () => {
  const split = splitCssProps({ someCustom: "x" });
  assertEquals(split.layout, { someCustom: "x" });
  assertEquals(split.style, {});
});

Deno.test("ipywidgets-specific style keys map to camelCase names", () => {
  const split = splitCssProps({
    handleColor: "#f00",
    buttonColor: "#0f0",
    barColor: "#00f",
    textColor: "#333",
  });
  assertEquals(split.style, {
    handle_color: "#f00",
    button_color: "#0f0",
    bar_color: "#00f",
    text_color: "#333",
  });
  assertEquals(split.layout, {});
});

Deno.test("styleModelFor selects per-control style subclasses", () => {
  assertEquals(styleModelFor("IntSlider")?.modelName, "SliderStyleModel");
  assertEquals(styleModelFor("FloatSlider")?.modelName, "SliderStyleModel");
  assertEquals(styleModelFor("Button")?.modelName, "ButtonStyleModel");
  assertEquals(styleModelFor("ToggleButton")?.modelName, "ButtonStyleModel");
  assertEquals(styleModelFor("Text")?.modelName, "TextStyleModel");
  assertEquals(styleModelFor("Box"), undefined);
});

Deno.test("bindCssProps creates LayoutModel + per-control StyleModel refs", () => {
  const { runtime, opens } = fakeRuntime();
  const refs = bindCssProps(runtime, "IntSlider", {
    maxWidth: "200px",
    fontSize: "14px",
    handleColor: "#f00",
  });

  assertEquals(refs.layout?.startsWith("IPY_MODEL_"), true);
  assertEquals(refs.style?.startsWith("IPY_MODEL_"), true);
  // Two comm_opens: the LayoutModel and the SliderStyleModel.
  assertEquals(opens.length, 2);
  const layout = opens.find((open) =>
    (open.data as { state: Record<string, unknown> }).state._model_name === "LayoutModel"
  )!;
  const style = opens.find((open) =>
    (open.data as { state: Record<string, unknown> }).state._model_name === "SliderStyleModel"
  )!;
  assertEquals(
    (layout.data as { state: Record<string, unknown> }).state.max_width,
    "200px",
  );
  const styleState = (style.data as { state: Record<string, unknown> }).state;
  assertEquals(styleState._model_module, "@jupyter-widgets/controls");
  assertEquals(styleState.font_size, "14px");
  assertEquals(styleState.handle_color, "#f00");
});

Deno.test("bindNestedStyle registers a LayoutModel comm_open and returns IPY_MODEL_ ref", () => {
  const { runtime, opens } = fakeRuntime();
  // bindNestedStyle passes the state through verbatim; callers (splitCssProps
  // / bindCssProps) supply already-mapped snake_case trait keys.
  const ref = bindNestedStyle(runtime, "LayoutModel", { width: "50%", align_items: "center" });

  assertEquals(ref.startsWith("IPY_MODEL_"), true);
  assertEquals(opens.length, 1);
  const open = opens[0];
  assertEquals(open.target_name, "jupyter.widget");
  const data = open.data as { state: Record<string, unknown> };
  assertEquals(data.state._model_module, "@jupyter-widgets/base");
  assertEquals(data.state._model_name, "LayoutModel");
  assertEquals(data.state.width, "50%");
  assertEquals(data.state.align_items, "center");
});

Deno.test("isCssBlock rejects primitives and arrays", () => {
  assertEquals(isCssBlock({ width: "1px" }), true);
  assertEquals(isCssBlock("100%"), false);
  assertEquals(isCssBlock([1, 2]), false);
  assertEquals(isCssBlock(null), false);
});
