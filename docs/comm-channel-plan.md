# 双向 Jupyter comm 通道 — 执行计划

Status: 已实施(2026-08-24)

分支:`feat/comm-channel`(基于 `feat/deno-jupyter-compat`)。

## 架构决策

- **comm 走专属 WebSocket 路径,控制总线零改动**。comm 是 Jupyter 自己的管道,不该挤进脚本工具/插件
  控制总线(后者有 64 队列上限、1MB 上限、JSON 文本帧、进程身份握手,且 `comm.msg` 数据现被丢弃)。
  ASP.NET 多路径天然支持。控制总线的既有 base64 偏离不在本次范围。
- **专属 WS 端点承载 Jupyter wire 消息,二进制原生透传**:前端 comm 消息在 `Maieutics.Jupyter.Kernel`
  已按 ZMQ 帧解析出 `JupyterWireMessage`(JSON content + `Buffers` 原生 `byte[]`),经组合根把整条
  wire 消息投递给 REPL 子进程的专属 WS,子进程内拆帧。无 base64、无 JSON 再封装、无协议转换——Jupyter
  wire 层是 comm 数据的唯一权威表示(invariant 14:二进制保持二进制直到目标表示要求编码)。
- **会话身份复用现有组件**:专属 WS 端点用 `ReplControlPeerProcess.TryGetIdentity`(进程身份,Unix
  `SO_PEERCRED` / Windows named-pipe)+ `ReplControlSessionRegistry`(pid→sessionId),与
  `ReplControlHost` 同构但不复用其 `connections`/`comms` 注册表(那些绑定 `/ws` 单连接模型)。
- **懒启动**:入站 comm 消息触发 REPL 子进程创建(同 `repl_execute` 懒启动),复用 `DenoReplRegistry`
  的默认会话路径。
- **`Maieutics.Jupyter.Kernel` 只加最小入站事件出口**,不加 comm 知识;comm 的"前端↔REPL 转发"逻辑
  全部在组合根 `MaieuticsAgentKernelApplication`。保可复用性与协议 5.5 兼容。
- **shell 串行处理保持**(invariant 4):入站 comm 消息由 kernel host 串行交给 application;application
  侧投递用 bounded queue 异步,不阻塞 shell。

## 改动点

### 1. Kernel 库 — 入站 comm 事件出口(最小)

- `Maieutics.Jupyter.Kernel/IJupyterKernelApplication.cs`:新增可选接口 `IJupyterCommSink`:
  `OnCommOpenAsync` / `OnCommMsgAsync` / `OnCommCloseAsync`,各接收 `JupyterCommMessage` 与
  `JupyterExecutionContext?`。
- `Maieutics.Jupyter.Kernel/JupyterKernelHost.cs`:`HandleShellRequestAsync` 增加
  `comm_open`/`comm_msg`/`comm_close` 分支:构造 `JupyterCommMessage`(含 Buffers),
  `application is IJupyterCommSink` 则调用,否则静默丢弃(现状不变)。
- 新增 `JupyterCommMessage`:`CommId` / `TargetName` / `Data`(JsonElement)/ `Buffers`。
- 新增单测:comm 消息落到 `IJupyterCommSink`(含 Buffers);无 sink 时静默;非 comm 消息不回归。

### 2. 组合根 — 前端→REPL 桥(核心)

- `Maieutics/Jupyter/MaieuticsAgentKernelApplication.cs`:实现 `IJupyterCommSink`;
  `OnCommOpen/OnCommMsg/OnCommClose` → 经注入的 `Func<string, ValueTask>` 确保默认 REPL 子进程
  (懒启动)→ 投递 wire 消息到该子进程的 comm WS。
- `Maieutics/Control/ReplControlHost.cs`:新增独立于 `/ws` 的 comm 端点映射
  (`Map("/comm", HandleCommWebSocketAsync)`),复用 `AuthorizeAsync` 的进程身份 +
  `ReplControlSessionRegistry` 定位 session;每 session 维护一个 comm WS 连接(子进程单连接);
  暴露 `PushCommMessageAsync(sessionId, JupyterWireMessage, ct)` 供 kernel application 调用。
  会话断开/关闭时广播 comm_close。
- `Maieutics/MaieuticsHost.cs`:把 `MaieuticsAgentKernelApplication` 与 `ReplControlHost` 桥接
  (注入 `Func<string, ValueTask>` 到 registry 懒启动;注册 `IJupyterCommSink`)。

### 3. Deno 脚本侧 — 专属 comm WS 客户端 + 事件

- `deno/maieutics-repl-client/mod.ts`:新增 `comm` 客户端:连接专属 `/comm` WS(复用
  `connectIpcWebSocket` 的 Unix/TCP/Windows named-pipe 传输,二进制帧天然支持),
  `comm.open/msg/close` 双向;`on` 事件 API(`CustomEvent` detail 含 `commId`/`data`/`buffers`
  原生 `Uint8Array[]`)。
- `deno/maieutics-deno-repl/repl_worker.ts`:`createJupyterApi().broadcast` 扩展为支持
  `comm_open`/`comm_msg`/`comm_close`(buffers 原生传入,不再 base64);错误信息指向兼容边界文档。
- `deno/maieutics-deno-repl/repl_actor.ts` + `protocol.ts`(repl.eval):`ReplActorEvent` 增加
  `commOpen`/`commMsg`/`commClose` 事件类型(worker 内传递);`repl_client.ts` 转发到 comm WS。
- `deno/shared/ipc_websocket.ts`:`connectIpcWebSocket` 增加 path 参数支持 `/comm`(现有签名已带
  path);确认二进制帧在三个传输的收发(Unix/TCP 原生支持;Windows named-pipe 帧类型 0x1→0x2 需实现)。

### 4. 测试

- Deno 单测:`deno task test`(comm WS 往返、buffers 原生 `Uint8Array`、事件分派)。
- 集成(`Maieutics.Jupyter.Tests`):真 Deno REPL + 真 kernel 端到端——前端发 `comm_open` → REPL
  收到;脚本 `Deno.jupyter.broadcast("comm_msg", …, { buffers })` → 前端收到二进制;`comm_close`
  双向;REPL 懒创建。
- 回归:`dotnet test Maieutics.slnx`、`deno fmt --check deno`、
  `deno task --config deno/maieutics-deno-repl/deno.json check`、`git diff --check`、
  `dotnet build Maieutics.slnx --no-restore -warnaserror`。

### 5. 文档与兼容边界

- 新增 `docs/comm-channel-plan.md`(本文件);更新 `docs/deno-jupyter-compat.md`(comm 双向已实现、
  broadcast 支持 comm 类型);`docs/deno-jupyter-compat-plan.md` 的"明确不做"更新;
  ADR 0014 的"future work: Jupyter wire mapping"标记已实施;`deno/maieutics-deno-repl/README.md`
  权限说明补 `/comm` 端点的 `--allow-net` 说明(如未覆盖)。

## 明确不做

- 不引入 ipywidgets 协议(target_name `jupyter.widget` 注册/版本协商);只透传 comm 消息(anywidget
  可用的前提)。
- 不改控制总线的 base64 偏离(独立问题,不在本次)。
- 不改 repl.eval 协议;不动 `Maieutics.Jupyter.Client`。
- 不做 `deno jupyter` 内核/CLI 面;保持 shell 串行,不引入 comm 与 execute 并发。

## 风险

- Windows named-pipe 二进制帧:需在 `ipc_websocket.ts` 的 named-pipe 传输加 0x2 帧支持,测试以
  Unix 为主,Windows 保持实现一致,CI 含 Windows 时验证。
- Kernel 可复用性:`IJupyterCommSink` 可选接口,非 comm 应用不受影响;`default` 静默丢弃不变。
- 懒启动时序:入站 comm_open 到达时 REPL 未创建 → 同步等待子进程就绪(复用现有
  `WaitForConnectionAsync` 模式),超时则协议错误。
