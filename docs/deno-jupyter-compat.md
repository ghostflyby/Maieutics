# Deno.jupyter 兼容边界

> **历史文档 — 输出通路部分已被 ADR 0023 取代。** 本文描述的 `Deno.jupyter` 兼容 API 面仍是 REPL 的现行行为;
> 但输出不再经用户 notebook IOPub,而是经 REPL presentation 管线进入自定义 Web 前端协议的事件流
> (`docs/web-frontend-protocol.md`,ADR 0023)。文中提到 IOPub / kernel 通路之处按历史记录保留。

本页记录 Maieutics REPL 对官方 `Deno.jupyter` API 的兼容范围。REPL 不是 `deno jupyter` 内核,而是
运行在 Aves REPL 内核(`jsr:@ghostflyby/aves/repl`)之上的兼容层(`deno/maieutics-deno-repl/repl_worker.ts`
的 `createJupyterApi()`),输出经 `repl.eval.*` 协议 → 展示管线 → 用户 notebook IOPub。

## 受支持的 API 面

| 成员 | 支持 | 说明 |
|---|---|---|
| `$display` | ✅ | `Symbol.for("Jupyter.display")`;带该 symbol 的对象作为**表达式结果**会被自动呈现(调用其 `$display` 得 MIME bundle 显示,不返回给模型),与官方 `deno jupyter` 一致 |
| `display(value, {raw, update, display_id})` | ✅ | `raw` 直接作为 MIME bundle;`update` + `display_id` 定向更新 |
| `format(value)` | ✅ | `Deno.inspect` 文本,或 `$display` 对象调其方法 |
| `broadcast(msgType, content, extra)` | ✅ | 支持 `display_data` / `update_display_data` / `clear_output` / `comm_open` / `comm_msg` / `comm_close`;`extra.metadata` 透传;`extra.buffers` 支持 comm 消息 |
| `md` / `html` / `svg` 标签模板 | ✅ | 对应 MIME 的 display_data |
| `image(path \| Uint8Array)` | ✅ | PNG/JPEG 自动判定,base64 传输 |
| `prompt` / `confirm` / `alert` | ✅ | 经 input_request 通道回前端 |

## 组件编写迁移（2026-11-12）

经典 widget 工厂、widget JSX/TSX 转换和对应浏览器渲染器已删除。REPL 使用普通 TypeScript；
交互输出使用 Vue Markdown/SFC，产品表单使用 `await maieutics.ui.form(...)`。
通用 comm 与二进制传输仍保留；它们不隐含任何 widget 或组件执行引擎。
旧 notebook 组件只恢复为冻结的表单数据或 JSON/文本，不执行保存的组件源码。

参见 [Vue UI 设计](custom-ui-framework-design.md) 和 [ADR 0050](architecture/decisions/0050-retire-custom-component-families.md)。
