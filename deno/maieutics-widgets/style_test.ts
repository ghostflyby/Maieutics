import { assertEquals } from "@std/assert";
import { bindNestedStyle, isCssBlock, normalizeCssProps } from "./style.ts";
import { WidgetRuntime } from "./runtime.ts";

function fakeRuntime(): { runtime: WidgetRuntime; opens: Array<Record<string, unknown>> } {
  const opens: Array<Record<string, unknown>> = [];
  const runtime = new WidgetRuntime(async (messageType, content) => {
    if (messageType === "comm_open") opens.push(content);
  });
  return { runtime, opens };
}

Deno.test("normalizeCssProps maps camelCase CSS to ipywidgets snake_case", () => {
  assertEquals(
    normalizeCssProps({
      alignItems: "center",
      justifyContent: "space-between",
      maxWidth: "200px",
      width: "100%",
      display: "flex",
      zIndex: "10",
    }),
    {
      align_items: "center",
      justify_content: "space-between",
      max_width: "200px",
      width: "100%",
      display: "flex",
      z_index: "10",
    },
  );
});

Deno.test("normalizeCssProps passes unknown keys through untouched", () => {
  assertEquals(normalizeCssProps({ someCustom: "x" }), { someCustom: "x" });
});

Deno.test("bindNestedStyle registers a LayoutModel comm_open and returns IPY_MODEL_ ref", () => {
  const { runtime, opens } = fakeRuntime();
  const ref = bindNestedStyle(runtime, "LayoutModel", { width: "50%", alignItems: "center" });

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

Deno.test("bindNestedStyle registers a StyleModel for style props", () => {
  const { runtime, opens } = fakeRuntime();
  const ref = bindNestedStyle(runtime, "StyleModel", { fontSize: "14px", color: "red" });

  assertEquals(ref.startsWith("IPY_MODEL_"), true);
  const open = opens[0];
  const data = open.data as { state: Record<string, unknown> };
  assertEquals(data.state._model_name, "StyleModel");
  assertEquals(data.state.font_size, "14px");
  assertEquals(data.state.color, "red");
});

Deno.test("isCssBlock rejects primitives and arrays", () => {
  assertEquals(isCssBlock({ width: "1px" }), true);
  assertEquals(isCssBlock("100%"), false);
  assertEquals(isCssBlock([1, 2]), false);
  assertEquals(isCssBlock(null), false);
});
