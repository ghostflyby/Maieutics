# Deno.jupyter 兼容边界补全 — 执行计划

Status: 执行计划(待实施)

Date: 2026-08-24

## 背景与现状(研究结论)

REPL 的 `Deno.jupyter` 是一个兼容 shim,实现在 `deno/maieutics-deno-repl/repl_worker.ts` 的
`createJupyterApi()`,经 `installHostEnvironment()` 注入 `globalThis.Deno.jupyter`。它运行在
Aves REPL 内核(`jsr:@ghostflyby/aves/repl`)之上,输出经 `repl.eval.*` 协议 → `ReplEvalWebSocketHost`
→ `DenoReplExecutionCollector` → `JupyterDenoReplPresentationSink` → 用户 notebook IOPub。

对照官方 Deno.jupyter 文档 API(display / format / broadcast / md / html / svg / image / `$display` /
Displayable / DisplayOptions),现状:

| 成员 | 现状 | 差距 |
|---|---|---|
| `$display` | ✅ `Symbol.for("Jupyter.display")` | 无 |
| `display(value, {raw, update, display_id})` | ✅ | 默认语义与官方不同(见下) |
| `format(value)` | ✅ inspect + `$display` 对象 | 无 |
| `broadcast` | ⚠️ 仅 display_data / update_display_data / clear_output | 其他类型抛错;metadata 部分透传;buffers 抛错 |
| `md` / `html` / `svg` | ✅ 标签模板 | 无 |
| `image(path \| Uint8Array)` | ✅ PNG/JPEG 判定 | 无 |
| Displayable 作为表达式结果 | ✅ 由 `format` 消费 | 无测试 |
| `prompt` / `confirm` / `alert` | ✅ 走 input_request | 无 |

真实缺口很小,且其中大部分是**有意不做**(见"明确不做"):comm 双向与任意 iopub broadcast 依赖
Kernel 层 comm 支持,而 AGENTS.md 明确"Unless explicitly requested, do not add … comm …"。官方
`broadcast` 的文档化用途就是 display 系 live update,shim 已覆盖。

因此本计划主体不是功能开发,而是:(1) 把兼容边界精确化(错误信息、语义标注);(2) 补全官方 API 面的
集成测试覆盖(当前 md/html/svg/image/`$display`/默认 display 路径**零测试**);(3) 把边界写入正式文档,
使"哪些能用、哪些不能、为什么"可被未来开发者与模型消费。

## 目标

1. 脚本内可用的 `Deno.jupyter` 达到官方文档 API 面的完整语义兼容(display / format / broadcast
   display 系 / md / html / svg / image / `$display` / Displayable 结果)。
2. 每一个官方 API 面成员都有真 Deno 进程级集成测试覆盖。
3. 兼容边界(支持面、差异、明确不做项与原因)成为仓库正式文档,并可从错误信息定位到该文档。

## 已定决策

- 继续扩充现有 shim,不引入真 `deno jupyter` 内核,不复用 `Maieutics.Jupyter.Client` 作为第二条通道。
  单一 `repl.eval.*` 通道保持不变。
- 兼容目标 = 官方 `Deno.jupyter` 命名空间 API 的语义,不是 wire 级内核兼容,也不包含 `deno jupyter`
  CLI / kernelspec / 连接文件面(架构已定:ADR 0014 / 0018 的 Aves + sideband 路线)。
- `display()` 默认语义保持现状(`raw: false`,走 `format()`),与官方默认 `raw: true` 的差异**标注不强行对齐**:
  官方 raw 语义要求调用方自己传 MediaBundle,而 shim 默认对普通对象更宽容,且与 `DenoReplFunctions` 工具
  描述中显式 `{ raw: true }` 的用法一致;强行对齐是破坏性变更且无场景驱动。
- 非 display 系 `broadcast` 保持拒绝,但错误信息从"not supported"改为指向兼容边界文档,说明需要 comm
  支持(见"明确不做")。
- 协议版本保持 1:本计划不新增协议字段,无 wire 变更。

## 改动点

### 1. `deno/maieutics-deno-repl/repl_worker.ts` — 兼容边界精确化(小改)

- `createJupyterApi().broadcast`(行 344–369):对 `display_data` / `update_display_data` / `clear_output`
  之外的消息类型,把错误信息改为明确说明该类型超出兼容边界、需要 comm 支持,并引用兼容边界文档路径。
  保持现有行为(拒绝 + 不中断 REPL),仅改进可诊断性。
- `display`(行 331–342):在代码注释标注与官方默认 `{ raw: true }` 的语义差异与保持现状的理由。
- 类型约束已由 `typeof Deno.jupyter` 提供(行 323),上述改动自动进入 `deno check` 覆盖。

### 2. 测试 — 官方 API 面全覆盖(主体工作)

**`Maieutics.Jupyter.Tests/DenoKernelIntegrationTests.cs`**(真 `deno jupyter` 进程级测试已有先例,
如 `RealDenoSeparatesStreamsDisplayResultAndError`、`RealDenoDisplayUpdatesAreTypedAndMalformedUpdatesDoNotDisconnect`),
新增:

- `md` / `html` / `svg` 标签模板 → 对应 MIME 的 display_data;
- `image(路径)` 与 `image(Uint8Array)` → `image/png`(或按内容 `image/jpeg`)base64 display;
- `$display` 对象作为末行表达式结果 → 自动呈现(如 `Deno.jupyter.html` 返回值直接作为表达式);
- `display(obj)` 不传 options → 默认 `format` 路径,产出 `text/plain` inspect;
- `display` 无 display_id 的 `update: true` → 现有 malformed 路径保持(已有测试),不回归;
- `broadcast` 非法消息类型 → 明确错误信息、执行报错但不断开连接(仿现有 malformed update 测试);
- `update_display_data` 携带 `metadata` → 透传到展示层。

**`Maieutics.Jupyter.Tests/MaieuticsHostIntegrationTests.cs`**(Agent 工具路径):
- `repl_execute` 工具内 `Deno.jupyter.display`(含 md/html)经工具调用的展示行为,补充现有覆盖
  (现有行 182–188 已覆盖 display/update,新增 md/html/image 各一条)。

### 3. 文档 — 兼容边界正式化

- 新增 `docs/deno-jupyter-compat.md`(仓库 docs 惯例,参照 `cross-worker-reactive-collections-wip.md`):
  - 受支持的 API 面与行为表;
  - 与官方 Deno.jupyter 的差异表(默认 raw 语义、broadcast 类型限制、buffers 不支持);
  - 明确不做项与原因(comm 双向 / anywidget / 二进制 buffers → AGENTS.md comm 禁令,需先 ADR 评审);
  - 重新评审的触发条件(出现真实 comm/anywidget 需求)。
- `deno/maieutics-deno-repl/README.md`:在 "Deno.jupyter.image(path)" 处(行 44)补充指向
  `docs/deno-jupyter-compat.md` 的链接。
- `docs/architecture/decisions/0014-deno-repl-ipc-and-http-control.md`(Draft):补一小节引用兼容边界
  文档,说明 REPL 的 `Deno.jupyter` 是 shim 而非真内核,comm 事件桥仍是"设计内未实现"。

## 明确不做(本次范围外)

- **ipywidgets 协议**(target_name `jupyter.widget` 注册/版本协商):只透传 comm 消息(anywidget 可用的前提)。
- **`broadcast` 二进制 buffers 往返**:~~wire 层 `JupyterMessage` 支持 buffers,但 presentation 层~~ ——
  **已实现**:comm 消息的 `extra.buffers` 走专属 `/comm` 通道二进制原生透传(见 `docs/comm-channel-plan.md`)。
  仅 display 系消息的 buffers 仍不支持(官方也未用于 display)。
- **任意 iopub 消息类型的通用 broadcast**:无消费方(仅 comm 与 display 系有真实用途)。
- **`display()` 默认对齐官方 `raw: true`**:破坏性,无场景驱动(见"已定决策")。
- **真 `deno jupyter` 内核 / kernelspec / CLI 面**:架构已定,不回归。
- **协议版本变更**:无 wire 改动。

## 验证

- `deno task --config deno/maieutics-deno-repl/deno.json check`(repl_worker.ts 类型,含 `typeof Deno.jupyter`)
- `deno fmt --check deno/maieutics-deno-repl`
- `dotnet test Maieutics.slnx`(重点:`DenoKernelIntegrationTests`、`MaieuticsHostIntegrationTests`,
  复用现有 `CreateDeadline` / `LocalJupyterKernelManager` 模式)
- `dotnet build Maieutics.slnx --no-restore -warnaserror`
- `git diff --check`

## 风险

- 真 Deno 集成测试依赖 esbuild-wasm 缓存与进程启动,已有测试先例可复用;新测试沿用同一
  `CreateDeadline` 超时框架,避免拖慢套件。
- `typeof Deno.jupyter` 类型随 Deno 版本漂移;锁定当前测试通过的 API 面,版本升级时由 `deno check`
  与集成测试兜底。
- 错误信息文案与文档路径耦合;文档路径变更时需同步 `repl_worker.ts` 中的引用。
