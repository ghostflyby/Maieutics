# MCP prompt 选用交互 — 设计文档

状态：草案 v2（经双视角对抗评审修订；与 ADR 0041 配套）
日期：2026-10-08（v2 同日）
关联：ADR 0026（资源平面与 `mcp://` 逃逸舱）、ADR 0039（skill 选用闭环）、ADR 0040（贡献框架）、`docs/web-frontend-protocol.md`（Skill references 章节）

## 0. 问题

MCP 的 prompts 原语是"服务器定义、用户点名、展开为对话内容"的参数化模板——与 skill 的选用语义同类，但内核只接了 tools 与 resources，prompts 完全缺席。本设计把 skill 交互定案的选用闭环（目录补全 → markdown 链接引用 → 构建期展开 → 类型化错误）同构地推广到 MCP prompts，并把三个真实差异（服务器限定身份、参数化、服务器侧动态生成）设计进去。

v2 修订（评审驱动）：serverId 段文法定案为不透明记号（撤回 RFC 3986 reg-name 的错误主张）；promptName 字符集放宽到含下划线与大写（MCP 规范例名 `code_review` 必须可引用）；取连接通道从 bulk lease 改为 Configuration 拥有的逐服务器 prompt 访问器（`McpServerGeneration.GetPromptAsync`）；reconnecting 期目录行为与 tools/resources 对齐（不保留 last-known-good）；三期参数表单改为前端本地表单（input-request 是 run 生命周期拥有的，选用时无 run）；角色降格的修订缝与防伪造框架定案；两 scheme 合一为单一边界文法。

## 1. 与 skill 的同构骨架（直接复用，不重新设计）

| 环节 | skill（已上线） | MCP prompt（本设计） |
|---|---|---|
| 目录来源 | `SkillCatalog.Current` | 代际内 `prompts/list`（初始 + PromptListChanged 通知重列，与 tools 的 RefreshTools 同机制） |
| 目录端点 | `GET /v1/skills` | `GET /v1/prompts`（含 spec 的 `title` 显示字段） |
| 引用文法 | `[text](skill://name)` | `[text](mcp-prompt://serverId/name?args)` |
| 铸造 | 整链接插入 = 引用（boundary 模型） | 同一模型，**单一交替文法**（见 §7.2） |
| 展开 | turn 构建期经 skill:// 读盘 | turn 构建期经 Configuration 访问器调 `prompts/get` |
| 错误 | `skill_unknown` / `skill_budget_exceeded` | `mcp_prompt_unknown` / `mcp_prompt_unavailable`(含超时，消息注明原因) / `mcp_prompt_argument_invalid` / `mcp_prompt_result_invalid` / 共享预算族 |

编辑器侧机制复用的成立条件（v2 明确）：**两 scheme 是一个交替文法、一个边界模型、一次编码 pass**（前缀不相交，无歧义）；两个先后 pass 会因转义插入移位 UTF-16 偏移而静默丢失已选引用——这是禁止项，写进二期实现约束。

## 2. 差异一：服务器限定身份

prompt 名字空间按服务器隔离（`serverId + name` 是身份），天然消解 skill 的全局重名遮蔽问题。

**引用文法**：`[显示文本](mcp-prompt://<serverId>/<promptName>)`。

- **serverId 段是不透明记号，不是合法 URI authority**（v2 定案）。真实 id 形态是 `plugin:<pluginId>::<module|serverKey>`（今日只有这一种形态；v1 所称"代际分配 id"不存在，撤回）。它含 `:`、可能含任意 JSON 字符串带来的 `/ ? # % 空格` 等字符——通用 URI 解析器会把它误读为 host:port。**操作性不变量：mcp-prompt 引用从不进入任何通用 URI 解析器**（两侧都以字面 `mcp-prompt://` 前缀的正则解析；`ResourceRegistry.TryParseUri` 永不接手；渲染器把 href 当不透明字符串）。这与 `mcp://` 逃逸舱的既有做法一致（`McpResourceProvider` 按 `/` 手工切分）。
- **可引用性（referencable）同时约束 serverId 与 promptName**：serverId 含 `/ ? # % [ ] ( ) \` 或空白、或 promptName 超出引用字符集 → 该 prompt 在 `GET /v1/prompts` 标记 `referencable: false`（照常列出，不可铸造引用）。
- **promptName 字符集（v2 放宽）**：`^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$`——MCP 规范自己的例名是 `code_review`（下划线），生态主流命名必须可引用；上界 64 与 skill 一致，`/ ? # % )` 等仍排除，文法保持严格。名字解析**从右侧切分**（promptName 字符集不含 `/`，`serverId` 可含，故最后一个 `/` 之前全是 serverId）。

## 3. 差异二：参数

`prompts/list` 的每个 prompt 带 `arguments` 声明（name/description/required）。

**基础层（手打可表达、wire 可重放）**：参数走 URL query——

```
[评审 rust 代码](mcp-prompt://plugin:demo::review/code-review?language=rust&depth=deep)
```

- **编码字母表（v2 定案）**：键与值一律百分号编码 RFC 3986 unreserved 之外的全部字符（即 `Uri.EscapeDataString` 语义；TS 侧 `encodeURIComponent` 会漏编 `!'()*`——`f(x)` 会破坏 markdown 链接形——TS 编码器必须显式补编这五个字符）。键是服务器声明的参数名，同样受此约束；声明名编码后不可往返的 prompt → `referencable: false`。
- query 键值 = `prompts/get` 的 arguments 映射；重复键 → 解析失败按 `mcp_prompt_argument_invalid`；多余参数（不在服务器声明中）→ 同错误（fail-closed）。
- 无参 prompt 裸引用；缺 required 参数 → 构建期 `mcp_prompt_argument_invalid`，**消息逐项列出 name + description 并指明要修订的引用**（v2：一期用户唯一的参数发现通道就是这条错误，必须自足）。

**增强层（三期，v2 改为前端本地表单）**：选中带 required 参数的 prompt 时，前端用 `GET /v1/prompts` 已携带的 arguments 声明**本地渲染表单**，填写完成生成带 query 的引用插回 cell。不使用 input-request 体系——它是 run 生命周期拥有的（无活跃 run 时 `FrontendElicitationPresenter` 立即取消），选用时没有 run；v1"复用现有体系"的主张撤回，"不自造第二套表单通道"修正为"表单是纯前端物，数据在目录里，零服务器往返、零新帧"。

**二期补全的降级**（v2 明确）：二期只对 `referencable && 无 required 参数` 的 prompt 提供直接插入；带参项以置灰+参数说明（detail 面板渲染 arguments 声明）展示，选中给出"手打 query"的提示文案。每个阶段的降级路径显式声明，不留给实现者猜。

## 4. 差异三：服务器侧动态生成（最实质的差异）

skill 展开是本地读盘（确定性、瞬时）；prompt 展开是 kernel → 服务器的 `prompts/get` 网络往返，返回 `GetPromptResult`（消息列表，可带 role，消息内容块可为 text/image/audio/embedded）。

**取连接（v2 重设计）**：不使用 bulk 的 `AcquireDynamicMcpLeasesAsync`（不接受 serverId、Frontend 命名空间不可依赖 Plugins、且它在 profile 获取期调用而 BuildTurnAsync 在其之前）。改为：`McpServerGeneration` 增加 `GetPromptAsync(name, arguments, ct)`（内部 `TryAcquire` + `RequestTimeout`，形状对齐 `ReadResourceAsync`）；`MaieuticsRuntimeConfiguration` 在 `GetResourceServers()` 旁暴露 `TryGetMcpPromptAsync(serverId, …)`（Configuration 是 Frontend 与 MCP 之间的既有边界）。serverId 无对应活跃代际 → `mcp_prompt_unknown`；TryAcquire null（retired/reconnecting）→ `mcp_prompt_unavailable`。

**发现容错（v2 定案）**：初始 `prompts/list` 与 PromptListChanged 重列的失败一律降级为空 prompt 清单（对齐 `RefreshResourceCatalogAsync` 捕获 McpException 的做法）——**绝不让无 prompts 能力的服务器（MethodNotFound）失败连接创建**。通知接线三件套（`PromptListChangedNotification` 处理器、`SubscriptionsListenNotifications.PromptsListChanged`、RefreshTools 旁的重列调用）在 SDK 2.2.0 均已存在。

**reconnecting 期目录（v2 对齐现状）**：不保留 last-known-good——reconnecting 服务器的 prompts 目录为空（与 tools/resources 完全一致：`GetInfo` 返回空工具表、资源目录为空）。前端可用自己的缓存展示带状态标记的目录，但内核不造新状态。风险 7 的"与工具面同机制同竞态"承诺由此为真。

**构建期服务器发起的 elicitation（v2 定案：拒绝）**：构建期 prompt 请求在飞行中时，内核对该连接的 elicitation 请求一律按 cancel 回答（现有归属器按"飞行中工具调用"归因，构建期无工具调用，跨会话错归是真实风险）。一期文档写明此行为；服务器确需参数交互的正当路径是三期本地表单。

**消息的注入语义（关键定案，v2 加固）**：`GetPromptResult.Messages` 经 SDK 的 `ToChatMessages()` 桥接后，**统一降格为 user 侧框架化部件**注入：

1. 与 skill 展开的信任语义一致：用户点名的内容经用户消息通道进入（ADR 0032 立场不变）；
2. 不伪造对话历史：prompt 消息不是本会话发生过的话；权威 transcript 记录"用户选用的模板展开了这些内容"，可重放、可审计；
3. role 以**内核组装的不可伪造框架**呈现：每条消息一个部件，头部是内核写入的定界行（含 role 与序号），**消息正文首部的 `[role]` 形文本被转义**——模板正文不能伪造框架行重新框定后续内容（内容不可信纪律）。
4. **修订缝如实记录（v2）**：真实的替代是 `AgentTurn` 增加不进已提交轮次的 pre-run 消息（requestMessages 与 committed turns 本就分野，`AgentSession.cs:260-261`），这是 Maieutics.Agent 内部改动而非不可行；触发条件=实测表明 assistant 结构化模板在降格下有可测退化。二者都写进 ADR，修订时无需重新论证可行性。

**结果界限**：
- 单 prompt 展开结果上限 4 MiB，超限 → **`skill_budget_exceeded`（413，预算族）**——与 skill 路径的 `resource_too_large` → 413 映射一致（v2 定案；`mcp_prompt_result_invalid` 只留给结构性不可用）。传输是 JSON-RPC 请求/响应，全量缓冲后检查（无流式；慢而大 → RequestTimeout 先到 → unavailable，快而大 → 413，两种顺序都存在，如实写）。
- 共享预算池：skill 正文与 prompt 展开共享 8 MiB/turn；**固定顺序 skill 先、prompt 后（各按文中首现序）**，与引用在 cell 中的位置无关——协议文档明示，用户可预期哪个引用赢得预算。
- **去重身份（v2 定案）**：prompt 展开的去重键是 `(serverId, promptName, 规范化 query)`——同 prompt 不同参数是不同内容，各展开一次（skill 的按名字去重不适用）；规范化=键值对按编码后的字节序数排序（顺序不敏感）。
- **非文本内容块（v2 定案）**：text 保留；image/audio/embedded resource 块**忽略并逐块记诊断**（不文本化——invariant 26；不 502——spec 合法内容不硬失败整 turn）。二期评估 embedded 的 objects:// 摄取路径。

## 5. 内核落位（不新建贡献种类）

按 ADR 0040 的框架图，prompts 不是插件贡献种类（不来自插件声明/worker，而来自服务器连接）。落位：

- `McpServerGeneration`：连接生命周期内第三面 `prompts/list`（初始+通知重列，失败降级空清单）；`GetPromptAsync` 原语（TryAcquire + RequestTimeout）。代际快照缓存 prompt 清单（含 title）。
- `MaieuticsRuntimeConfiguration`：`GetMcpPrompts()` 聚合各活跃代际（对齐 `GetMcpServers`）；`TryGetMcpPromptAsync` 访问器（Frontend 的唯一入口，不越命名空间边界）。
- `Maieutics.Frontend`：`FrontendPromptMarkers`（文法解析：严格形+提及转义，右侧切分，单类双 scheme 交替文法或与 skill 合一——实现时定，但**编码 pass 必须单次**）+ `FrontendPromptExpansion`（访问器获取、参数校验、消息降格+框架防伪、预算）。`BuildTurnAsync` 在 skill 标记之后、于其余文中解析 prompt 引用。
- 资源保留表：`mcp-prompt` 登记为 **null（不可分配）**——与 `file` 同类：无内置 provider 认领，防 custom provider 声明；将来若要资源面可读再改 BuiltIn。

## 6. Wire 与协议文档

- `GET /v1/prompts` → `{prompts: [{serverId, name, title?, description, arguments?: [{name, description, required}], referencable, state}], diagnostics}`。
- 错误码族（HTTP 映射，v2 对齐既有税制）：`mcp_prompt_unknown` 404、`mcp_prompt_unavailable` 409（消息注明原因：reconnecting / 超时）、`mcp_prompt_argument_invalid` 400、`mcp_prompt_result_invalid` **400**（v2 改：仓库税制无 5xx 先例，`agent_provider_error` 也是 400；不引入新类）、`skill_budget_exceeded` 413（语义broaden为 turn 参考内容预算，消息文本同步泛化，别名意图记入错误注册表）。**服务器侧参数拒绝（JSON-RPC -32602）映射为 `mcp_prompt_argument_invalid`**（v2 定案：声明缓存与服务器实际校验的竞态窗口里，服务器裁决优先，错误族一致）。
- 协议文档新增 "Prompt references" 章节（对齐 Skill references 行文，含固定预算顺序与 query 编码字母表）。

## 7. 分期

1. **一期（内核 + 文档）**：代际 prompts 发现（容错）+ `GET /v1/prompts` + 文法 + 展开（访问器/超时/参数校验/消息降格+防伪框架/预算/去重键）+ 错误族 + 协议文档 + 保留 scheme（null）。
2. **二期（前端基础）**：补全（触发字符定案见下）、无参引用插入、boundary 模型**单交替文法**改造、带参项置灰+参数说明。
3. **三期（前端增强）**：required 参数的**前端本地表单**（数据来自目录声明，零新帧）。

**触发字符（v2 定案）**：prompt 补全与 skill 补全**同用 `$`**——二者是同一交互形状（点名选用引用），`/` 已被 slash 命令补全占用（`completion.ts` 注册 `%`/`/`），同一触发字符下按目录分组（skills / prompts by server），项类型区分。

## 8. 风险与未决

| # | 风险 | 处置 |
|---|---|---|
| 1 | serverId / promptName 超出引用字符集 | referencable: false（两者都覆盖）；名字右侧切分消除歧义 |
| 2 | 展开期间服务器挂起/超大结果 | RequestTimeout（→unavailable）与 4 MiB cap（→413）两种先后都存在；全量缓冲是传输现实 |
| 3 | 预算抢占 | 固定顺序 skill→prompt 各按首现序；协议明示 |
| 4 | image/audio/embedded 块 | 一期忽略+逐块诊断；embedded 的 objects:// 路径二期评估 |
| 5 | serverId 非法 URI authority | 不透明记号不变量：字面前缀正则解析，永不进通用 URI 解析器（含渲染器 href） |
| 6 | 降格注入丢 assistant 语义 | 修订缝已记录（AgentTurn pre-run 消息）；触发条件=可测退化；不阻塞一期 |
| 7 | ListChanged 通知与代际竞态 | 与工具面同机制同竞态面，不引入新处理 |
| 8 | 构建期服务器 elicitation | 一律 cancel（防跨会话错归）；正当参数交互走三期本地表单 |
| 9 | 通用渲染器对 mcp-prompt 链接的渲染 | 不透明 href 可能被 VSCode/共享 notebook 的 markdown 渲染器丢弃或误显示——接受为已知限制（引用的裁判是内核文法，不是渲染器），协议文档注明 |
