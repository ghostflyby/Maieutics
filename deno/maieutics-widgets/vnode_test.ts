import { assertEquals, assertThrows } from "@std/assert";
import { walkWidgets } from "./vnode.ts";
import { CONTROLS } from "./controls.ts";

/** A minimal preact-style vnode builder matching esbuild's automatic-jsx output. */
function h(
  type: string,
  props: Record<string, unknown> = {},
  ...children: unknown[]
): { type: string; props: Record<string, unknown> } {
  return {
    type,
    props: children.length > 0
      ? { ...props, children: children.length === 1 ? children[0] : children }
      : props,
  };
}

Deno.test("walkWidgets flattens a tree into ordered instances", () => {
  const vnode = h(
    "Box",
    { layout: "x" },
    h("IntSlider", { value: 5, min: 0, max: 10 }),
    h("Button", { description: "Go" }),
  );
  const instances = walkWidgets(vnode);
  assertEquals(instances.length, 3);
  assertEquals(instances[0].kind, "Box");
  assertEquals(instances[1].kind, "IntSlider");
  assertEquals(instances[2].kind, "Button");
  assertEquals(instances[1].props.value, 5);
  assertEquals(instances[0].children.length, 2);
});

Deno.test("walkWidgets handles single child, arrays, and text children", () => {
  const single = walkWidgets(h("Text", {}, "some text"));
  assertEquals(single.length, 1);
  assertEquals(single[0].kind, "Text");

  const array = walkWidgets([h("Button"), h("Button")]);
  assertEquals(array.length, 2);
});

Deno.test("walkWidgets rejects unknown control tags", () => {
  const error = assertThrows(
    () => walkWidgets(h("BogusWidget", {})),
    Error,
  );
  assertEquals(error.message.includes("Unknown ipywidgets control '<BogusWidget>'"), true);
});

Deno.test("controls catalog covers the factories exposed by index", () => {
  for (
    const name of [
      "IntSlider",
      "FloatSlider",
      "Button",
      "Text",
      "ToggleButton",
      "IntRangeSlider",
      "Box",
    ]
  ) {
    assertEquals(CONTROLS.has(name), true, `missing ${name}`);
  }
  const slider = CONTROLS.get("IntSlider");
  assertEquals(slider?.modelName, "IntSliderModel");
  assertEquals(slider?.viewName, "IntSliderView");
});
