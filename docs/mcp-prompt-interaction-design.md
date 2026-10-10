# MCP prompt 选用交互 — 设计文档

状态：草案（与 ADR 0041 配套）
日期：2026-10-08
关联：ADR 0026（资源平面与 `mcp://` 逃逸舱）、ADR 0039（skill 选用闭环）、ADR 0040（贡献框架）、`docs/web-frontend-protocol.md`（Skill references 章节）

## 0. 问题

MCP 的 prompts 原语是"服务器定义、用户点名、展开为对话内容"的参数化模板——与 skill 的选用语义同类，但内核只接了 tools 与 resources，prompts 完全缺席。本设计把 skill 交互定案的选用闭环（目录补全 → markdown 链接引用 → 构建期展开 → 类型化错误）同构地推广到 MCP prompts，并把三个真实差异（服务器限定身份、参数化、服务器侧动态生成）设计进去。

## 1. 与 skill 的同构骨架（直接复用，不重新设计）

| 环节 | skill（已上线） | MCP prompt（本设计） |
|---|---|---|
| 目录来源 | `SkillCatalog.Current` | 代际发现时随 `ListTools`/`ListResources` 一起 `prompts/list`（ListChanged 通知驱动刷新，与工具面同机制） |
| 目录端点 | `GET /v1/skills` | `GET /v1/prompts` |
| 引用文法 | `[text](skill://name)` | `[text](mcp-prompt://serverId/name)` |
| 铸造 | 整链接插入 = 引用（boundary 模型） | 同一模型，同一提及转义 |
| 展开 | turn 构建期经 skill:// 读盘 | turn 构建期经 lease 调 `prompts/get` |
| 错误 | `skill_unknown` / `skill_budget_exceeded` | `mcp_prompt_unknown` / `mcp_prompt_unavailable` / `mcp_prompt_argument_invalid` / `mcp_prompt_result_invalid` / `skill_budget_exceeded`（复用预算族） |

编辑器侧（补全/引用模型/chip/提交编码）**零新机制**——`skillReferences` 的 boundary 模型按文法参数化即可服务两种 URI scheme。

## 2. 差异一：服务器限定身份

prompt 名字空间按服务器隔离（`serverId + name` 是身份），天然消解 skill 的全局重名遮蔽问题。

**引用文法**：`[显示文本](mcp-prompt://<serverId>/<promptName>)`。理由：

- `serverId` 段复用 ADR 0026 逃逸舱的既有风格（`mcp://<serverId>/<encoded uri>`），`mcp-prompt://` 是独立 scheme——它与 `mcp://` 的消费者不同（turn 构建路径 vs 资源平面），且 scheme 保留表需要分别登记；
- `serverId` 是 `plugin:<id>::<module>` 形态（数据文件来源）或代际分配的 id，**含 `:` 但不含 `/`**，作为 URI host 合法（reg-name 允许冒号），文法无歧义；
- promptName 约束为 skill 名字同款 charset（`^[a-z0-9][a-z0-9-]{0,63}$`）——MCP 规范允许更宽的名字，超出者不进入可引用目录（照常列于 `GET /v1/prompts` 标记 `referencable: false`），文法保持严格。

**生命周期语义**：引用是活引用（展开时 `prompts/get` 现取）。服务器移除/改名 prompt → 下一次展开 `mcp_prompt_unknown`；服务器离线 → `mcp_prompt_unavailable`（同 reconnecting 窗口的资源读取语义）。

## 3. 差异二：参数

`prompts/list` 的每个 prompt 带 `arguments` 声明（name/description/required）。

**基础层（手打可表达、wire 可重放）**：参数走 URL query——

```
[评审 rust 代码](mcp-prompt://plugin:demo::review/code-review?language=rust&depth=deep)
```

- query 键值 = `prompts/get` 的 arguments 映射；百分号编码；
- 无参 prompt 裸引用；缺 required 参数 → 构建期 `mcp_prompt_argument_invalid`（消息列出缺失项）；
- 多余参数（不在服务器声明中）→ 同错误（fail-closed：不猜服务器会怎么处理未知参数）。

**增强层（前端表单，二期/三期）**：选中带 required 参数的 prompt 时走**现有 input-request 体系**（`input.request` 帧 + schema，REPL stdin / MCP elicitation 的同一机制）弹参数表单，填写完成由前端生成带 query 的引用插回 cell。不自造第二套表单通道。

## 4. 差异三：服务器侧动态生成（最实质的差异）

skill 展开是本地读盘（确定性、瞬时）；prompt 展开是 kernel → 服务器的 `prompts/get` 网络往返，返回 `GetPromptResult`（**消息列表，可带 role**）。

**取连接**：展开器经 `AcquireDynamicMcpLeasesAsync` 取该 serverId 的 lease（现成通路）；`TryAcquire` 为 null（retired/reconnecting）→ `mcp_prompt_unavailable`。预算复用该服务器定义的 `RequestTimeout`。

**消息的注入语义（本设计的关键定案）**：`GetPromptResult.Messages` 经 SDK 的 `ToChatMessages()` 桥接为 MEAI `ChatMessage` 后，**统一降格为 user 侧框架化部件**注入——每条消息成为 `TextContent`（role 信息保留在框架文本头部，如 `[assistant] …`），追加在 turn 的文本/附件/skill 部件之后，与 skill 展开部件同列。理由：

1. 与 skill 展开的信任语义一致：这是**用户点名的内容**，经用户消息通道进入，属 turn 内容里信任等级最高的一类（ADR 0032 立场不变：内容路径无安全，执行边界把关）；
2. 不伪造对话历史：prompt 消息不是本会话发生过的话，作为 assistant 轮次注入 transcript 会污染权威历史；降格为带 role 标注的 user 侧内容，transcript 记录的是"用户选用的模板展开了这些内容"，可重放、可审计；
3. 服务器可以让 assistant 消息承载多步指令（如 code-review 的分节指示）——role 标注保留可辨识性，模型不会丢失"这是模板给出的引导"这一信息。

**结果界限**：展开后总文本进入既有 turn 预算（8 MiB skill 族预算改名为通用的 turn 参考内容预算，skill 与 prompt 展开共享——两者都是"用户点名的参考内容"，同一预算池防止叠加绕过）；单 prompt 展开结果上限 4 MiB（与单 skill 正文同界）。消息内嵌的 `EmbeddedResource` 内容块：一期**忽略并记诊断**（不展开二级资源），二期评估是否走 `objects://` 摄取路径。

## 5. 内核落位（不新建贡献种类）

按 ADR 0040 的框架图，prompts 不是插件贡献种类（不来自插件声明/worker，而来自服务器连接）。落位：

- `McpServerGeneration`：发现时第三面 `prompts/list`（与 tools/resources 并列）；`ListChanged` 的 prompt 版通知驱动重列（与工具面同机制）。代际快照内缓存 prompt 清单（名字+参数声明），`GET /v1/prompts` 聚合各活跃代际。
- `MaieuticsRuntimeConfiguration`/`PluginHostManager`：`GetMcpPrompts()` 状态面（对齐 `GetMcpServers`），`/mcp` 命令族后续可加 `prompts` 子命令。
- `Maieutics.Frontend`：`FrontendPromptMarkers`（文法解析，严格形+提及转义，与 `FrontendSkillMarkers` 同纪律）+ `FrontendPromptExpansion`（lease 获取、`prompts/get`、参数校验、消息降格、预算）。`BuildTurnAsync` 在 skill 标记之后解析 prompt 引用。
- 资源保留表：`mcp-prompt` 进 `ReservedSchemeOwners`（BuiltIn）与 custom mirror——防止 custom provider 声明该 scheme。

## 6. Wire 与协议文档

- `GET /v1/prompts` → `{prompts: [{serverId, name, description, arguments?: [{name, description, required}], referencable, state}], diagnostics}`（活跃代际聚合；reconnecting 服务器的 prompts 标 `state: "reconnecting"`，仍列出——用户能看到但选用会得到 typed unavailable）。
- 错误码族（HTTP 映射）：`mcp_prompt_unknown` 404、`mcp_prompt_unavailable` 409、`mcp_prompt_argument_invalid` 400、`mcp_prompt_result_invalid` 502（服务器返回了不可用结构）、`skill_budget_exceeded` 413（复用，语义为 turn 参考内容预算）。
- 协议文档新增 "Prompt references" 章节（对齐 Skill references 的行文）。

## 7. 分期

1. **一期（内核 + 文档）**：代际 prompts 发现 + `GET /v1/prompts` + 文法 + 展开（lease/超时/参数校验/消息降格/预算）+ 错误族 + 协议文档 + 保留 scheme。
2. **二期（前端基础）**：`/` 触发的 prompt 补全（目录按服务器分组展示）、无参引用插入；boundary 模型参数化支持双 scheme。
3. **三期（前端增强）**：required 参数的 input-request 表单选用（复用现有体系）。

## 8. 风险与未决

| # | 风险 | 处置 |
|---|---|---|
| 1 | 服务器 prompt 名字超出引用 charset | 目录标记 `referencable: false`，不进引用面（文法保持严格） |
| 2 | 展开期间服务器挂起 | RequestTimeout 预算兜底；超时 → `mcp_prompt_unavailable`，整 turn 拒绝（不截断） |
| 3 | 展开结果与 skill 正文叠加超预算 | 共享 turn 参考内容预算池（8 MiB），先到先得 |
| 4 | prompt 返回消息内嵌资源块 | 一期忽略+诊断；二期评估 objects:// 路径 |
| 5 | serverId 含 `:` 的 URI host 合法性 | reg-name 允许冒号（RFC 3986）；kernel/TS 两侧正则都以字面 `mcp-prompt://` 开头匹配，不依赖通用 URI 解析器 |
| 6 | 降格注入丢 assistant 语义 | role 以文本标注保留于框架头部；若后续实验显示模型需要真实 role，是可修订项（记录为未决，不是定案） |
| 7 | ListChanged 通知与代际竞态 | 与工具面同机制同竞态面，不引入新处理 |
