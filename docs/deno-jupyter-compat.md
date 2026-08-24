# Deno.jupyter 兼容边界

本页记录 Maieutics REPL 对官方 `Deno.jupyter` API 的兼容范围。REPL 不是 `deno jupyter` 内核,而是
运行在 Aves REPL 内核(`jsr:@ghostflyby/aves/repl`)之上的兼容层(`deno/maieutics-deno-repl/repl_worker.ts`
的 `createJupyterApi()`),输出经 `repl.eval.*` 协议 → 展示管线 → 用户 notebook IOPub。

## 受支持的 API 面

| 成员 | 支持 | 说明 |
|---|---|---|
| `$display` | ✅ | `Symbol.for("Jupyter.display")`;带该 symbol 的对象作为表达式结果会被自动呈现 |
| `display(value, {raw, update, display_id})` | ✅ | `raw` 直接作为 MIME bundle;`update` + `display_id` 定向更新 |
| `format(value)` | ✅ | `Deno.inspect` 文本,或 `$display` 对象调其方法 |
| `broadcast(msgType, content, extra)` | ✅ | 支持 `display_data` / `update_display_data` / `clear_output` / `comm_open` / `comm_msg` / `comm_close`;`extra.metadata` 透传;`extra.buffers` 支持 comm 消息 |
| `md` / `html` / `svg` 标签模板 | ✅ | 对应 MIME 的 display_data |
| `image(path \| Uint8Array)` | ✅ | PNG/JPEG 自动判定,base64 传输 |
| `prompt` / `confirm` / `alert` | ✅ | 经 input_request 通道回前端 |

## comm 通道

脚本经 `maieutics.comm`(注入的命名空间)与前端双向交换 comm 消息:

- `maieutics.comm.open(commId, targetName?, data?)` / `msg(commId, data?, buffers?)` /
  `close(commId, data?)`:脚本 → 前端。`buffers` 为原生 `Uint8Array[]`,无 base64。
- `maieutics.comm.on(event, handler)`:订阅前端 → 脚本的 comm 事件(`open` / `msg` / `close`)。
- 也可用 `Deno.jupyter.broadcast("comm_open|comm_msg|comm_close", content, { buffers })` 从脚本发 comm。

comm 走专属 WebSocket 路径(`/comm`),与控制总线隔离,二进制原生透传。消息编码为固定二进制格式
(`[kind:1][commIdLen:2][commId][targetNameLen:2][targetName][dataLen:4][data][bufferCount:2][bufLen:4][buf]...`),
C# 侧 `ReplControlHost.CommCodec` 与 Deno 侧 `maieutics-repl-client/comm.ts` 同构。

## 与官方 Deno.jupyter 的差异

- **`display()` 默认语义**:官方默认 `{ raw: true }`;本实现默认 `raw: false`(先经 `format()`)。官方 raw
  要求调用方自行传 MediaBundle,本实现对普通对象更宽容,且与工具描述中显式 `{ raw: true }` 的用法一致。
  需要官方默认行为时请显式传 `{ raw: true }`。
- **`broadcast` 类型受限**:仅上表列出的消息类型被支持。其他 iopub 消息类型(如 `execute_result`、
  `kernel_info_reply` 等)会抛 `TypeError`,因为那是内核职责,不是 REPL 脚本可广播的面。
- **`deno jupyter` CLI / kernelspec / 连接文件面不存在**:架构不采用真 `deno jupyter` 内核(ADR 0014/0018),
  Maieutics 本身就是 Jupyter kernel。

## 明确不做

- **ipywidgets 协议**(target_name `jupyter.widget` 注册/版本协商):只透传 comm 消息,不实现 widget 内核侧
  协议。`@anywidget/deno` 这类纯 relay 库可工作;现代 ipywidgets 需 kernel 侧协议,不做。
- **comm 之外的任意 iopub 广播**:见上表。

## 触发重新评审

出现真实 widget 需求(需 comm + 二进制 buffers 之外的内核侧协议)时,先走 ADR 评审再扩展。
