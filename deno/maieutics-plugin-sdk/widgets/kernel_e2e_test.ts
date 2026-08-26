import { assertEquals } from "@std/assert";
import { createReplKernel } from "@ghostflyby/aves/repl";
import { createTsxTransform } from "./transform.ts";
import { bindWidgetHost } from "./index.ts";

const DISPLAY = Symbol.for("Jupyter.display");

/**
 * Full-pipeline verification: a TSX cell compiled by createTsxTransform,
 * run through the Aves REPL kernel (which applies the AST rewrite + async
 * IIFE), and evaluated — a known control element must yield a displayable
 * widget model and broadcast its comm_open.
 */

interface OpenCall {
  comm_id: string;
  target_name: string;
  data: { state: Record<string, unknown> };
}

Deno.test("tsx cell renders a control to a displayable widget model", async () => {
  const opens: OpenCall[] = [];
  bindWidgetHost({
    broadcast: async (messageType: string, content: Record<string, unknown>) => {
      if (messageType === "comm_open") opens.push(content as unknown as OpenCall);
    },
    onComm: () => {},
  });

  const kernel = await createReplKernel({ transform: createTsxTransform() });
  const execution = kernel.execute(
    "const { jsx } = await import('maieutics-widgets/jsx-runtime'); " +
      "const slider = jsx('IntSlider', { value: 5 }); slider;",
  );
  const result = await execution.result;

  assertEquals(result.ok, true);
  const value = result.data as {
    commId: string;
    [DISPLAY]: () => Promise<Record<string, unknown>>;
  };
  assertEquals(typeof value[DISPLAY], "function");
  assertEquals(opens.length, 1);
  assertEquals(opens[0].target_name, "jupyter.widget");
  assertEquals(opens[0].data.state._model_name, "IntSliderModel");
  assertEquals(opens[0].data.state.value, 5);

  const bundle = await value[DISPLAY]();
  const view = bundle["application/vnd.jupyter.widget-view+json"] as { model_id: string };
  assertEquals(view.model_id, value.commId);

  await kernel.dispose();
});
