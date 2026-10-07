# 插件贡献泛化框架设计（Plugin Contribution Framework）

Status: Draft（配套 [ADR 0040](architecture/decisions/0040-plugin-contributions.md)）

Date: 2026-10-07

Branch base: `feat/differential-reconcile`（HEAD `c3eee9e`；本文所有行号基于该提交）

Related: [ADR 0033](architecture/decisions/0033-plugin-scoped-mcp-data-file.md)、
[ADR 0034](architecture/decisions/0034-mcp-adjustment-chain.md)、
[ADR 0036](architecture/decisions/0036-plugin-triggers.md)、
[ADR 0037](architecture/decisions/0037-plugin-declaration-approval.md)、
[ADR 0038](architecture/decisions/0038-universal-custom-ui-framework.md)、
[ADR 0039](architecture/decisions/0039-skill-discovery-and-instruction-catalog.md)、
`docs/declarative-extensions-design.md`

---

## 0. 目标与非目标

插件对内核的贡献面目前有两套同构机制：**MCP 服务器发现**（ADR 0033/0034）与**技能贡献**
（ADR 0039 stages 2-3）。二者在事件差量、粘滞语义、单飞排空、强制重跑、审批面上重复实现，
且每新增一种贡献（UI 表单、触发器是既知的后续种类）需要触碰十余处分散代码。本设计给出
一个**种类契约 + 贡献槽 + 共用差分协调器**框架，使二者获得对等的可配置性，并把新增一种
贡献的触碰点从约 11 处收敛到 4 处。

**非目标（红线）：**

1. **指纹字节稳定。`PluginDeclarationFingerprint` 现有各域的哈希输入逐字节不变**
   （`Maieutics/Plugins/PluginDeclarationFingerprint.cs:25-126`）。统一文法只改解析入口，
   不改任何种类的指纹输入。违反即全体现存插件审批被撤销（ADR 0037 decision 5）。
2. **行为保持。** 差分语义逐项不变：reloadEpoch 延迟强制重发现
   （`PluginHostManager.cs:3403-3436`）、粘滞按 (plugin, export)
   （`PluginHostManager.cs:310-311, 3630-3633`；coordinator 侧
   `PluginMcpCoordinator.cs:341-347`）、publish-only 插件存活
   （`PluginHostManager.cs:2312-2321, 3295-3304`）、毒根逐条降级
   （`PluginHostManager.cs:3691-3775`）、adjuster drop 可逆
   （`PluginMcpCoordinator.cs:388-408`）、never-succeeded-stays-new
   （`PluginMcpCoordinator.cs:40-44`；`PluginHostManager.cs:329-335, 3590-3634`）。
   `PluginMcpCoordinatorTests`、`PluginSkillsContributionTests`、
   `PluginDeclarativeExtensionsTests`、`PluginHostInvokeTests` 等既有套件语义不改地保持绿。
3. **不新建程序集。** 在现有可执行项目内以 `Maieutics/Plugins` 下新子域
   （`Maieutics/Plugins/Contributions/`，命名空间 `Maieutics.Plugins.Contributions`）解决。
   AOT 安全：禁反射式泛型序列化；非泛型抽象 + 封闭具体类。
4. **wire 格式向后兼容。** host 帧不变（`PluginRegistration` 三元组
   `PluginHostManager.cs:19`、registry/reload/approval 载荷不变、capability 结果形状不变）。
5. **UI 表单与触发器不迁移。** 第 8 节仅论证框架能容纳它们，实施不碰。

---

## 1. 机制现状对照表

两种类逐轴对照（行号均已在分支 HEAD 核实；标注〔核〕的区间为核对员核实、本设计复核引用）：

| 轴 | MCP 服务器发现（ADR 0033/0034） | 技能贡献（ADR 0039 stages 2-3） |
|---|---|---|
| **声明形式** | 三种：① manifest `extensions.McpDiscover` 条目（kind 大小写不敏感、记录规范拼写，`PluginManifest.cs:47-61, 644-646`）；② `mcp` 数据条目指向 JSON 文件（名字精确匹配 `PluginManifest.cs:83-86`，采集做根包含检查 `:579-606`，逐服务器超时可配置 `Mcp/McpServerFile.cs:35-41`）；③ worker 导出 `mcp.discover` 扩展点（动态发现，`PluginHostManager.cs:4022-4045`） | 三种：① manifest `extensions.Skills` 条目（`roots` 经 `${env.*}`/`${var.*}` 插值，`PluginHostManager.cs:3731-3741`；≤32 根 `:3779`；根包含或 read 授权覆盖 `:3761-3770`）；② worker 导出 `Skills` 扩展点（生成器，`:3595-3634`）；③ `skills.publish` 能力（目录门 `PluginCapabilityCatalog` + 清单授权门，`PluginHostManager.cs:2879-2899, 3137-3145`） |
| **值形状** | `McpServerDefinition`：Id、Transport、四个超时、RootsEnabled、ElicitationEnabled、GenerationKey（`Mcp/McpServerGeneration.cs:58-67`）；GenerationKey = SHA-256 长度前缀域序（`McpServerGeneration.cs:69-131`） | `SkillDescriptor`：Name ≤64（`[a-z0-9-]`）、Description ≤1024（超长截断）、BodyText ≤64K（仅计算源；文件体另有 4MB 界）、Source、RootDirectory、Diagnostic=inert 标记（`Skills/SkillDescriptor.cs:39-68`） |
| **键控** | 注册（PluginId, ExportName, ExtensionPoint）为贡献单元；代际按服务器 Id 复用、以 GenerationKey 判等（`PluginMcpCoordinator.cs:459-488`）；同 id 同 key 去重、同 id 异 key 中止整修订（`:428-441`） | 粘滞按 (PluginId, ExportName)（`pluginGeneratedSkills`，`PluginHostManager.cs:310-311, 3630-3633`）；插件面一个槽（`contributedSkillPlugins` `:327`）；条目在目录内按名字键控 |
| **失败语义** | discovery 失败/抛异常不写 candidate，前贡献保持（`PluginMcpCoordinator.cs:352-375`）；merge 冲突中止整修订、前快照保持活跃（`:436-441, 410`）；adjuster drop 只作用合成视图、原始贡献持久、下修订自动恢复（`:388-408`）；commit-on-publish：成果只在修订发布时提交（`:492-505`），被超越修订成果丢弃下修订重跑（`:410, 478-512`） | 逐导出粘滞 last-good（`:3603-3634`）；逐条目 inert 降级（毒根 `:3714-3774`、非法生成条目 `:3797-3847`）；毒根/坏条目不中止 pass；异常只降级该插件（`:3660-3668`）；提交时 gate 下复查 lifetime+审批（`:3641-3654`） |
| **刷新触发** | 注册帧 plain 差分（`PluginMcpCoordinator.cs:96-101`）；强制集：reloadEpoch 排空（`PluginHostManager.cs:3416-3436`，标记 `:3403-3408`，调用点 `:1495-1498, 1546-1549`）与触发（`ForceRediscoverPlugin` `:3362-3368`，由 `HandlePluginTrigger` `:3334-3343` 调用）；审批转换重发布（`:2360`）；Start 种子（`:981-992`） | 四事件点：Start 逐插件渐进种子（`:994-1010`）、注册帧按 Skills 导出集 diff（`SkillExportSetsLock`/`DiffSkillExportSets` `:3278-3327`，帧前快照 `:3189`，未变帧零动作 `:3183-3188`）∪ reload 排空 ∪ 重试集（`:3250-3252`）、审批对称差 ∩ 种类面（`:2312-2321`）、触发单插件（`:3362-3368`）；按插件单飞排空（`skillsReconcileGate` + flight `:291-304, 3446-3527`） |
| **指纹域** | 三域参与：`extensions`（kind + 条目规范 JSON，字面量形态 `PluginDeclarationFingerprint.cs:78-87`）、`data`（名 + 规范 JSON/错误 `:90-97`）、`mcp`（Id + GenerationKey `:100-107`） | `extensions` 域承载（skills 条目以字面量 `${...}` 形态入哈希，ADR 0039 stage 2a）；无专域 |
| **消费视图** | `PluginMcpCoordinator` 修订引擎 → 代际快照 → 连接生命周期 + adjustment 链重投影（`PluginMcpCoordinator.cs:447-548`；`McpAdjustmentChain`） | `SkillCatalog`：volatile 快照（`SkillCatalog.cs:127, 178-180`）、优先级合并 Workspace<User<Plugin（`:644-688`）、每根 watcher+差分泵（`:323-455`〔核〕）、渐进提交钩子（`:126, 301-321`〔核〕） |

**可直接观察到的不对称（来自核对员 gap，本设计吸收）：**

- **超时不对称**：动态发现形式四超时完全固定（30s/2min/5s/30s，stdio 与 http 同，
  `PluginHostManager.cs:4153-4191`）；`mcp` 数据文件形式逐服务器可配置且校验正值
  （`McpServerFile.cs:35-41, 142-215`）。同一值形状、两种来源两种可配置性。
- **插值不对称**：skills roots 走插值（`PluginHostManager.cs:3731-3741`）、triggers 走
  插值（`PluginManifest.cs:418-424`），而 `mcp` 数据条目路径与 `extensions.McpDiscover`
  条目完全无 `${...}` 参与（`PluginManifest.cs:579-606`）。
- **根包含不对称**：数据条目入口路径做 `IsWithinRoot`（`PluginManifest.cs:591`），但
  `mcp.json` 内部 stdio `workingDirectory` 只 `GetFullPath(value, base)`、无包含检查
  （`McpServerFile.cs:173-177`），`..` 可逃出插件根。
- **诊断文案滞后**：`PluginDataName.IsKnown` 含 `ui`（`PluginManifest.cs:83-86`），但两处
  unknown-data-entry 文案只列 `mcp`（`:506-509, 517-520`）。
- **声明比较器不一致**：`pluginGeneratedSkills` 用默认比较器、三个兄弟字典显式
  `StringComparer.Ordinal`（`PluginHostManager.cs:310-311 vs 316-317, 322-323, 327`）；
  元组键默认即 ordinal，行为等价，仅声明风格问题。
- **插件贡献无聚合上限**：声明 ≤32 根×每根 256、生成每导出 ≤256、发布 ≤256，但导出数与
  条目数不受插件面总量界约束；`SkillCatalog.UpdatePluginContribution`
  （`SkillCatalog.cs:206-216`）与 `RebuildSnapshot`（`:633-709`）对插件平面不加总量界。
- **manifest 支失败码瞬态窗口**：`RecordDescriptorDeclarationsLock` 对空声明即刻删条目
  （`PluginHostManager.cs:3995-4008`），合成注册要等下一次 registrations 重建才消失
  （`:3975-3990`），窗口内 manifest 支发现以 `unknown_manifest_plugin` 失败
  （`:4064-4065`）。

---

## 2. 现状新增一种贡献的触碰点 → 目标 4 处

以"新增一种贡献种类"为口径（UI 表单、触发器即此口径），今天需要触碰（归组后约 11 处）：

| # | 触碰点 | 现状代码 |
|---|---|---|
| 1 | extensions 文法种类目录（常量 + `IsKnown`/`Canonicalize` + 未知 kind 文案） | `PluginManifest.cs:36-62, 630-636` |
| 2 | data-entry 文法名字目录（`PluginDataName` + 两处未知名文案） | `PluginManifest.cs:74-87, 506-509, 517-520` |
| 3 | `PluginDescriptor` 解释后字段 + `PluginManifest.TryLoad` 解释块 | `PluginManifest.cs:9-26, 354-395` |
| 4 | 指纹：通用 `extensions`/`data` 域免费覆盖，但专域（如 `mcp`）要改 `Compute`/`IsApprovalExempt` | `PluginDeclarationFingerprint.cs:100-108, 132-143` |
| 5 | 宿主声明快照字典 + `RecordDescriptorDeclarationsLock` | `PluginHostManager.cs:263-280, 3995-4008` |
| 6 | 合成注册形态 `DeclarativeRegistrationsLock` | `PluginHostManager.cs:3975-3990` |
| 7 | 注册帧种类专属导出集 diff | `PluginHostManager.cs:3239-3243, 3278-3327` |
| 8 | 审批对称差 ∩ 种类专属 face | `PluginHostManager.cs:2312-2321, 3295-3304` |
| 9 | 强制/重试集按种类一份 | `PluginHostManager.cs:329-335, 3250-3252, 3362-3368` |
| 10 | 协调/粘滞状态机（MCP 全套修订引擎 / Skills 四字典+单飞+组合提交） | `PluginMcpCoordinator.cs` 全文；`PluginHostManager.cs:306-335, 3539-3669, 3891-3909` |
| 11 | 能力发布分支（`capability.invoke` 内 per-kind if 链） | `PluginHostManager.cs:2912-3019` |
| （另） | SDK 符号（`ExtensionPoint` 符号 + 类型化接口） | `deno/maieutics-plugin-sdk/mod.ts:85-103, 227-261` |
| （另） | wire 目录（`ReplExtensionPointName` / `ReplCapabilityName` + capability catalog） | `Maieutics/Control/ReplControlMessages.cs:178-226` |

目标：新增种类只触碰 **4 处**——

1. **种类契约**（kernel 侧一处注册，吸收 #1-#10 的种类差异面）；
2. **SDK 符号**（Deno 侧，若该种类有 worker 形式）；
3. **wire 载荷**（extension-point/capability 目录各一条常量注册）；
4. **消费适配器**（该种类真实差异所在的消费端引擎）。

#4 的默认路径是**不需要触碰**：`extensions`/`data` 通用域已免费覆盖新种类（ADR 0038
decision 7 的先例）；本设计将"新种类不得引入新指纹专域"定为规则（见 §3.4）。

---

## 3. 共用契约设计（kernel 侧）

新子域 `Maieutics/Plugins/Contributions/`，命名空间 `Maieutics.Plugins.Contributions`，
程序集不变（仍是 `Maieutics` 可执行项目）。以下为接口草图（表达形状与不变量，非完整实现）。

### 3.1 描述符层：非泛型抽象 + 封闭具体类（AOT 安全）

```csharp
namespace Maieutics.Plugins.Contributions;

/// <summary>一种贡献条目的框架侧视图。抽象基类只携带差分需要的最小面；
/// 领域值形状不重新定义——封闭具体类包裹既有领域记录，消费端引擎继续拥有自己的形状。</summary>
public abstract class ContributionDescriptor
{
    protected ContributionDescriptor(string identityKey, string? diagnostic)
    { IdentityKey = identityKey; Diagnostic = diagnostic; }

    /// <summary>差分与相等性的标识键：MCP = 服务器 Id（Ordinal）；Skills = 技能名（Ordinal）。</summary>
    public string IdentityKey { get; }
    /// <summary>非 null 即 inert 条目：留在列表里可见、不进消费面（skills 的惰性诊断先例，
    /// PluginHostManager.cs:3911-3919）。</summary>
    public string? Diagnostic { get; }
}

/// <summary>封闭具体类之一：包裹 McpServerDefinition（Mcp/McpServerGeneration.cs:58）。</summary>
public sealed class McpServerContribution : ContributionDescriptor
{
    public McpServerContribution(Mcp.McpServerDefinition definition)
        : base(definition.Id, diagnostic: null) => Definition = definition;
    public Mcp.McpServerDefinition Definition { get; }
}

/// <summary>封闭具体类之二：包裹 SkillDescriptor（Skills/SkillDescriptor.cs:39）。</summary>
public sealed class SkillEntryContribution : ContributionDescriptor
{
    public SkillEntryContribution(Skills.SkillDescriptor skill)
        : base(skill.Name, skill.Diagnostic) => Skill = skill;
    public Skills.SkillDescriptor Skill { get; }
}
```

AOT 论证：框架层从不序列化 `ContributionDescriptor`；源生成 JSON 上下文继续只针对封闭
领域类型（`McpServerDefinition`、`SkillDescriptor`、manifest 原始 `JsonElement`）。
消费适配器用模式匹配下沉到自己的封闭类型——无反射、无泛型实例化。

### 3.2 种类契约

```csharp
/// <summary>一种贡献种类在 kernel 侧的全部知识。每种类一个封闭子类，
/// 在 ContributionKindCatalog 静态注册（禁运行时改注册表）。</summary>
public abstract class ContributionKindContract
{
    // —— 目录元数据 ——
    public abstract string KindName { get; }                    // 规范名（如 "McpDiscover"/"Skills"）
    public abstract ContributionKindMetadata Metadata { get; }  // §5 可配置性旋钮
    /// <summary>大小写策略与诊断文案由目录统一：Canonicalize 大小写不敏感、记录规范拼写
    /// （PluginManifest.cs:47-61 的泛化）；未知名文案由目录枚举全部已知名自动生成
    /// （消除 PluginManifest.cs:506-509, 517-520 只列 mcp 的滞后）。</summary>

    // —— 声明解析：manifest 条目 → 描述符列表，惰性诊断随条目走 ——
    /// <summary>extensions 文法路由：本种类认领哪些条目（按规范 kind 名）。</summary>
    public abstract bool OwnsExtensionKind(string canonicalKind);
    /// <summary>data-entry 文法路由：本种类目录化的数据名，无则 null。</summary>
    public abstract string? DataEntryName { get; }
    /// <summary>把本种类认领的声明解释成描述符。失败不抛——逐条目 inert 诊断
    /// （skills 毒根先例）；结构级失败仍走 manifest 严格失败（现 TryLoad 语义）。</summary>
    public abstract IReadOnlyList<ContributionDescriptor> ParseDeclared(
        ContributionDeclarationContext context);

    // —— 计算形式：invoke 请求构造 + 输出校验（无计算形式则 HasComputeForm=false）——
    public abstract bool HasComputeForm { get; }
    public abstract JsonElement BuildComputeRequest(in ContributionComputeContext context);
    /// <summary>worker 输出校验：MCP = JSON 数组逐项 TryToMcpDefinition、任一非法整次失败
    /// （PluginHostManager.cs:4033-4045）；Skills = ParseGeneratedSkills 逐条目 inert
    /// （:3791-3858）。失败语义由种类自己声明——这是两种类真实的语义差异，不下沉。</summary>
    public abstract ContributionValidationResult ValidateComputed(JsonElement output);

    // —— 发布形式：capability 载荷校验（无发布形式则 HasPublishForm=false）——
    public abstract bool HasPublishForm { get; }
    public abstract string? PublishCapability { get; }   // "skills.publish"；ui 种类将来为 "ui.models"
    public abstract ContributionValidationResult ValidatePublished(JsonElement payload);

    // —— 强制重跑与重试语义（never-succeeded-stays-new 是否启用本种类 kernel 侧重试集）——
    public abstract bool UsesKernelRetrySet { get; }     // Skills=true；MCP=false（retry 在 coordinator 内）
}

/// <summary>封闭目录：规范名/数据名 → 契约；大小写不敏感查找；诊断文案自动生成。
/// 取代 PluginExtensionKind 与 PluginDataName 的全部消费点。</summary>
public static class ContributionKindCatalog
{
    public static IReadOnlyList<ContributionKindContract> Kinds { get; }
    public static ContributionKindContract? ByExtensionKind(string kind);      // OrdinalIgnoreCase
    public static ContributionKindContract? ByDataEntryName(string name);      // Ordinal 精确
    public static string CanonicalizeOrDiagnostic(string kind, out string? canonical);
    public static string KnownExtensionKindsText();   // "known kinds: McpDiscover, Skills — ..."
    public static string KnownDataEntryNamesText();   // "known names: mcp, ui"
}
```

### 3.3 贡献槽

每 `(pluginId, kind)` 一槽，三部组成，gate 下组装提交：

```csharp
/// <summary>槽状态三部。declared 每轮重算；generated 按 (导出/注册) 键粘滞 last-good；
/// published 整体替换。组合顺序固定：declared → generated（键序）→ published
/// （PluginHostManager.cs:3646-3651 的泛化）。</summary>
public sealed class ContributionSlot
{
    public string PluginId { get; }
    public string KindName { get; }
    public IReadOnlyList<ContributionDescriptor> Declared { get; }        // 每轮重算
    public IReadOnlyDictionary<SlotSourceKey, IReadOnlyList<ContributionDescriptor>> Generated { get; }
    public IReadOnlyList<ContributionDescriptor>? Published { get; }      // null = 从未发布
    public bool HoldsFace { get; }   // 持槽即有面：publish-only 插件被撤销时仍清其发布部分
}

/// <summary>槽表：gate（宿主既有 gate）保护；组合提交原子；代际 token 复核拒绝与停止路径
/// 竞争的迟到提交（PluginHostManager.cs:3636-3654 的泛化）。取代 Skills 四字典
/// （pluginDeclarativeSkills/pluginGeneratedSkills/pluginPublishedSkills/contributedSkillPlugins）
/// ——比较器统一为 Ordinal（行为等价，消除 §1 声明风格不一致）。</summary>
public sealed class ContributionSlotTable { /* UpdateSlot / RemoveSlot / HoldsFace / ClearAll */ }
```

MCP 侧同一三部形状的**既存实现**是 `PluginMcpCoordinator`：它的
`contributions`（`PluginMcpCoordinator.cs:75-76`）按注册键的粘滞袋 = generated 部分
（注册三元组即 (pluginId, exportName) 的等价键控），其发布走自己的修订 gate（
`ReferenceEquals(latestRevision, revision)` 守卫 + 单引用原子交换，`:492-505`）。
框架不搬移这个状态：MCP 消费适配器把 coordinator 注册为槽形状的实现方，槽抽象用于
face 判定、清理与目录化，**不改 coordinator 的修订语义**。

### 3.4 指纹专域规则（红线落实）

`PluginDeclarationFingerprint.Compute` 的域序列**冻结为现状顺序**：perm.* → workers →
deps → isolation → caps → extensions → data → mcp → triggers → inspections
（`PluginDeclarationFingerprint.cs:54-124`）。规则：

1. 统一文法只改"谁产出生成 `extensions`/`data` 域输入的值"——生成的值必须与现状逐字节
   相同（extensions 域 = 规范 kind + 原始条目规范 JSON 字面量形态；data 域 = 名 + 采集
   内容规范 JSON 或错误文本）。
2. **新种类不得引入新指纹专域**：新种类的声明面走通用 `extensions`/`data` 域
   （ADR 0038 decision 7 已验证该路径）。专域意味着字节布局变化 ⇒ 全量审批撤销，
   属持久格式破坏，超出本框架范围。
3. C 期的插值对等不触碰指纹：skills roots 先例——extensions 域哈希**字面量**形态
   （`${...}` 原样），运行期展开值只影响 GenerationKey 等派生面（ADR 0039 stage 2a；
   ADR 0037 "paths enter the fingerprint in their resolved form" 仅适用于 triggers 的
   展开监视路径与 data 域的采集内容，两者本框架均不改）。

---

## 4. 共用差分协调器与种类适配器

### 4.1 事件差量引擎只写一份

```csharp
/// <summary>kernel 侧唯一的事件差量引擎。输入是宿主既有四类事件，输出是按种类的
/// 差量投递。保持的语义（逐项对应现状行号见 §0 红线 2）：
/// - 注册帧：按种类导出集 diff（SkillExportSetsLock/DiffSkillExportSets 泛化；
///   MCP 的 mcpSnapshot 过滤泛化为其导出集来源 = ReplExtensionPointName.McpDiscover）；
///   未变帧零动作，但 reload 强制与重试集追加（PluginHostManager.cs:3250-3252 的并集语义）。
/// - 审批：previouslyBlocked ∪ blocked 上取成员翻转者 ∩ HoldFace(种类)
///   （PluginHostManager.cs:2317-2321 泛化）。
/// - 强制集：MarkReloadForce（gate 下、reconcile 串行内、重载帧发出前，:3403-3408）→
///   DrainReloadForces（epoch 前进或倒退即排；null epochs 全量排；:3416-3436）→
///   种类投递器按自己的形态消费（MCP=forced 重发布；Skills=单插件 pass）。
/// - 触发：单插件强制（ForceRediscoverPlugin 泛化为逐种类投递）。
/// - never-succeeded-stays-new：UsesKernelRetrySet 的种类维护重试集，首个全成功 pass 清除；
///   MCP 不用（其 retry 在 coordinator 修订引擎内）。
/// - 按插件单飞排空：Running/Pending flight + 排空循环 + finally re-arm
///   （:3446-3527 泛化为种类无关调度器；gate 永不跨 await 持有的规则不变）。</summary>
public sealed class ContributionCoordinator
{
    public void OnRegistryFrame(ContributionRegistryFrame frame);
    public void OnApprovalTransition(IReadOnlySet<string> previouslyBlocked, IReadOnlySet<string> blocked);
    public void MarkReloadForce(string pluginId);
    public IReadOnlyList<string> DrainReloadForces(IReadOnlyDictionary<string, int>? epochs);
    public void OnTrigger(string pluginId);
    public void Seed(IEnumerable<string> pluginIds);   // Start 渐进种子（:994-1010）
    public void Reset();                               // 宿主换代清空（:955-957）
}

/// <summary>种类投递端：协调器算出的差量由适配器消费。</summary>
public interface IContributionDelivery
{
    ContributionKindContract Kind { get; }
    bool HoldsFace(string pluginId);
    Task ReconcileAsync(string pluginId, ContributionReconcileInput input, CancellationToken token);
}
```

### 4.2 两个种类的适配（消费端引擎保留）

| | MCP 投递适配器 | Skills 投递适配器 |
|---|---|---|
| 差量→动作 | plain 集 + forced 集合成 `PublishRegistry(snapshot, forced)`（`PluginHostManager.cs:3375-3395` 现状调用形态不变） | 单插件 pass：声明重走 + 逐导出粘滞 invoke + 槽组合提交（`ReconcilePluginSkillsAsync` 现状逻辑迁入适配器） |
| 保留的真实差异 | `PluginMcpCoordinator` 修订引擎、代际生命周期、merge 冲突中止、adjustment 链、commit-on-publish——**全部原地不动** | `SkillCatalog` FS watcher/目录行走/优先级合并/渐进提交——**全部原地不动** |
| 槽实现 | coordinator 的按注册粘滞袋（§3.3） | `ContributionSlotTable` 槽表 |

框架统一的是**贡献与协调层**（§3.2 契约、§3.3 槽、§4.1 协调器）；代际/连接生命周期与
FS watcher/目录行走是两消费端引擎的真实差异，不下沉（任务骨架第 4 条）。

---

## 5. 可配置性对等

`ContributionKindMetadata` 成为种类元数据，两种类共享框架级旋钮：

```csharp
public sealed record ContributionKindMetadata(
    int? MaxDeclaredEntriesPerDeclaration,   // Skills: 32（根/条目，:3779）；MCP: null（现状无界）
    int? MaxComputedEntries,                 // Skills: 256（:3785）；MCP: null（现状无界）
    bool StickyPerSourceKey,                 // 两种类均 true；键 = (pluginId, 导出/注册)
    bool UsesKernelRetrySet,                 // Skills: true；MCP: false
    IReadOnlyList<TimeSpan>? DefaultTimeouts,// MCP: [30s, 2min, 5s, 30s]（:4153-4191 现状固定值）；Skills: null
    bool DeclarationInterpolation);          // Skills: true（roots）；MCP: 目标 true（对等化，见下）
```

**MCP 声明条目获得插值对等**（C 期）：

- `mcp` 数据条目路径经 manifest 变量表展开后再走既有采集管线（`CollectDataEntry` 增加
  变量表参数；展开后仍执行 `IsWithinRoot`，逃逸即错误标记 → 粘滞 last-good 路径）。
  指纹安全：`data` 域哈希的是**采集内容**的规范 JSON/错误（`PluginDeclarationFingerprint.cs:90-97`），
  路径本身不入哈希，插值只改变找到哪个文件，与现状的环境依赖同性质。
- `extensions.McpDiscover` 条目值在解释期（`TryToMcpDefinition` 入口）展开 `${env.*}`/
  `${var.*}`：指纹走 `extensions` 域的**字面量**形态（与 skills roots 同一先例），展开值
  进入 `McpServerDefinition` → GenerationKey 随环境变化 → 代际按 key 重接，语义正确且
  指纹字节稳定。
- **禁止**把 extensions 条目解释出的服务器并入 `descriptor.McpServers`：那会把它们带入
  `mcp` 专域哈希，改变现存含 extensions 条目插件的指纹（红线 1）。
- **超时对等**：extensions 条目允许显式四超时覆盖，缺省取元数据默认
  （= 今日固定值）。缺省路径下 `CreateGenerationKey` 的哈希输入与今日逐字节相同
  （`McpServerGeneration.cs:124-127`），现存动态服务器 GenerationKey 不变；显式覆盖者
  本来就是新声明（审批重批），符合 ADR 0037 语义。

框架级共享旋钮（两种类同源）：`MaxComputedEntries`、粘滞开关、重试集开关、默认超时表、
插值开关。声明侧（manifest）不加新顶层节；可配置性经由种类自己的文法形状表达
（skills 的 `roots` 数组、mcp 条目的可选 `timeouts` 成员），由契约的
`ParseDeclared`/`ValidateComputed` 解释。

---

## 6. 迁移分期

每期独立可交付、行为保持、以既有测试套件为回归门。

### A 期：共用引擎抽取

- 新建 `Maieutics/Plugins/Contributions/`：`ContributionDescriptor`（含两个封闭类）、
  `ContributionSlot`/`ContributionSlotTable`、`ContributionCoordinator`（单飞排空 +
  导出集 diff + 审批面 + 强制/重试集）。
- Skills 侧四字典迁移到槽表（比较器统一 Ordinal，行为等价）；`ReconcilePluginSkillsAsync`
  逻辑原样迁入 Skills 适配器；宿主的四事件点改调 `ContributionCoordinator`。
- MCP 侧本期的**唯一**变化：其 kernel 侧表面（`mcpSnapshot` 过滤、forced 集传递、
  `ForceRediscoverPlugin` 双投递）改经协调器；`PluginMcpCoordinator` 文件不动。

**行为保持论证**：纯状态重组——同样的 gate、同样的键、同样的组合顺序与提交复查点；
协调器是 `SkillExportSetsLock`/`DiffSkillExportSets`/`HasSkillFaceLock`/flight/
`skillsRetryPending` 的一对一搬移（含"未变帧 + reload/retry 追加"的并集语义与
"gate 永不跨 await"规则）。无任何可观察语义面变化。

**测试策略**：A 期开工前先落**指纹黄金夹具**（代表性 manifest 集的
`PluginDeclarationFingerprint.Compute` 输出快照进测试数据）；既有套件
（`PluginMcpCoordinatorTests`、`PluginSkillsContributionTests`、
`PluginDeclarativeExtensionsTests`、`PluginHostInvokeTests`、`PluginHostIntegrationTests`、
`PluginUiCapabilityTests`）**不改语义**跑绿；为槽表/协调器新增镜像现状语义的单元测试
（未变帧零动作、pending coalesce、re-arm、重试集清除时点、publish-only 清理）。

### B 期：文法与目录统一

- `ContributionKindCatalog` 取代 `PluginExtensionKind`/`PluginDataName` 的全部消费点；
  `ParseDeclared` 委托接手 `TryLoad` 内 per-kind 解释块（`PluginManifest.cs:354-395` 形状
  的泛化）；合成注册、快照字典、发布分支改由契约驱动。
- 指纹输入字节不变的论证：统一文法产出的描述符字段值与现状逐字节相同——extensions 域
  哈希原始条目的规范 kind + 规范 JSON（`ReadExtensions` 的 `entry.Clone()` 原样保留，
  `PluginManifest.cs:639-647`），data 域哈希采集内容，触发器域哈希展开路径，`mcp` 域哈希
  Id+GenerationKey——契约只是**解析入口**换了归属，喂给 `PluginDescriptor` 的值相同。
  黄金夹具在 B 期全程不得移动。
- 本期唯一**有意的可观察变化**：unknown-data-entry 诊断文案由目录生成，同列 `mcp` 与
  `ui`（修复 §1 文案滞后；今日 `ui` 声明不会误诊断，仅文案误导）。相关文本断言
  （若有）随文案一并更新，语义断言不动。

**测试策略**：同 A 期套件 + `PluginManifestTests` 扩展目录驱动用例（未知 kind/未知
data 名文案、规范拼写记录、大小写不敏感路由）；指纹黄金夹具全绿。

### C 期：可配置性对等

- 元数据旋钮接线；MCP 数据条目路径插值与 extensions 条目插值 + 可选超时覆盖（§5）。
- **行为保持论证**：缺省路径的解析结果与 GenerationKey 输入与今日逐字节相同（
  `McpServerGeneration.cs:69-131` 的哈希输入在缺省值下不变）；插值只在新声明里可观察，
  新声明本就要求重批（ADR 0037）；错误路径走既有粘滞标记形态。mcp.json stdio
  `workingDirectory` 无包含检查（§1 缺口）**不在本期静默修补**——单列加固项。
- Skills 聚合预算（§1 缺口）为**显式决策项**：在 `UpdatePluginContribution` 处加每槽
  条目预算会丢弃超额条目（可观察行为变化），单独评审、单独测试，不夹带进框架迁移。

**测试策略**：新增插值（env/var 展开、逃逸降级）、超时覆盖（缺省 key 与今日相等、
覆盖 key 变化 → 代际重建）、元数据旋钮行为用例；既有套件继续不改语义跑绿。

每期收官运行仓库验收：`dotnet test Maieutics.slnx`、
`dotnet build Maieutics.slnx --no-restore -warnaserror`、`git diff --check`；
涉及 AOT 面的收尾加受支持 RID 的 publish 检查。

---

## 7. 触碰点收敛的验证口径

以"新增一种贡献种类（不妨假想一种 `notices`）"走查目标框架：

1. kernel：在 `Contributions/` 写一个封闭 `NoticesContributionsKind`（目录元数据 +
   `ParseDeclared` + 视需要 `ValidateComputed`/`ValidatePublished`）并在
   `ContributionKindCatalog` 注册一行——**1 处**；
2. SDK：`deno/maieutics-plugin-sdk/mod.ts` 加 `ExtensionPoint.Notices` 符号与类型化接口
   （若该种类有 worker 形式）——**2**；
3. wire：`ReplExtensionPointName`/`ReplCapabilityName` 目录各一条常量——**3**；
4. 消费适配器：该种类真实差异所在的消费端（如通知面板的合成视图）——**4**。

§2 表中 #4（指纹）零触碰（通用 extensions/data 域覆盖），#5-#10 由槽表 + 协调器 + 契约
吸收，#11 发布分支由 `PublishCapability` 路由吸收。达到 ≤4。

---

## 8. UI 与触发器作为未来种类的容纳性论证（不实施）

**UI 表单（ADR 0038）**：其声明形式是 `ui` 数据条目（`PluginDataName.Ui`，
`PluginManifest.cs:78-81, 354-369`），运行形式是 `ui.models` 能力帧
（`PluginHostManager.cs:2912-2975`）。映射到框架：kind 契约
`DataEntryName="ui"`、`HasComputeForm=false`（无 worker 计算形式）、
`HasPublishForm=true` + `PublishCapability="ui.models"`；槽 = declared（表单模板）+
published（活动模型帧）；无导出集 → 注册帧 diff 对其零动作（§4.1 的种类声明导出点集
为空即退化正确）；face = declared ∨ 持槽。指纹已由通用 `data`/`caps` 域覆盖
（ADR 0038 decision 7 明示零指纹代码变化）。框架无需为容纳 UI 增加任何新抽象。

**触发器（ADR 0036）**：声明式、展开后监视路径入指纹专域。映射：kind 契约声明
"declaration-only"（`HasComputeForm=false`、`HasPublishForm=false`、槽只有 declared
部分）；其与现状的差异面仅在于触发器指纹走**展开形态专域**——按 §3.4 规则 2，这属于
"已存在的专域"（`triggers` 域今日已在序列中，`PluginDeclarationFingerprint.cs:110-122`），
迁移时该域的哈希贡献函数原样保留；种类刷新触发 = 指纹变化的 reload 路径（审批快照），
不经注册帧 diff。消费者适配器 = 触发器注册表重挂。

结论：两未来种类的形状（无计算形式、无发布形式、仅声明、专域哈希、无导出集）都在
契约的可选成员与槽的三部可空性内；**实施顺序上本设计不迁移它们**（红线 5），
仅在契约评审时以其为反例校验表达力。

---

## 9. 风险清单

| # | 风险 | 缓解 |
|---|---|---|
| 1 | 指纹字节漂移（重组 `PluginDescriptor` 构造、规范 JSON、域序） | A 期开工先落黄金夹具；域序冻结 + 新种类禁专域（§3.4）；B 期夹具全绿为合入门 |
| 2 | 误把 MCP 修订语义搬进共用引擎（代际/merge/commit-on-publish 是消费端真实差异） | 红线：coordinator 文件在 A/B 期不动；适配器只传 plain/forced 集（现状 `RepublishRegistry` 形态） |
| 3 | `unknown_manifest_plugin` 瞬态窗口被统一行为放大（快照字典删条目 vs 合成注册滞后一拍，§1） | 保持窗口语义并在协调器测试中显式文档化该瞬态；禁止"顺手"重排删条目与重建顺序 |
| 4 | mcp.json stdio `workingDirectory` 无根包含（`..` 逃逸，§1） | 非本框架范围；单列加固项；C 期新插值路径必须保持含限 deny-wins，不得复制该缺口 |
| 5 | 动态形式超时不对称在 C 期对等时改变现存 GenerationKey | 缺省值逐字节等于今日固定值（`McpServerGeneration.cs:124-127`）；仅显式覆盖者 key 变化（新声明 → 重批） |
| 6 | 插值把 extensions 条目服务器引入 `mcp` 专域 → 存量插件指纹撤销 | §5 明令禁止；`descriptor.McpServers` 内容维持"仅数据文件来源" |
| 7 | Skills 插件面无聚合上限（快照重建成本不受硬界） | 显式决策项不夹带（§6 C 期）；框架层不静默加界 |
| 8 | 诊断文案变化（B 期目录生成，同列 mcp+ui） | 枚举为唯一有意可观察变化；文本断言随文案更新，语义断言不动 |
| 9 | 并发语义回归（gate 跨 await、代际 token、审批复查时点、单飞 re-arm） | 协调器为逐行搬移；镜像语义单测覆盖 pending coalesce/re-arm/复查点；`skillsReconcileGate` 永不跨 await 的注释契约随之迁移 |
| 10 | wire 兼容破坏 | host 帧三元组、registry/reload/approval 载荷、capability 结果形状全部不变；A-C 期均不触碰 `ReplControlMessages` 的既有成员（只增） |
| 11 | AOT：抽象基类上误用反射序列化 | 框架层不序列化 `ContributionDescriptor`；JSON 上下文保持封闭类型；收尾跑 RID publish 检查 |
| 12 | commit-on-publish 语义被误解为缺陷而"修复"：并发 plain 发布可让 forced 重跑成果延迟一拍；`EnqueueRegistry` 只继承 forced 集不继承未提交 discovery（`PluginMcpCoordinator.cs:154-162`） | 保持现状语义（文档注释 `:40-44` 即契约）；§1 记录为已知特性，不改 |
| 13 | 并行小缺口（`skill://` 平面对 inline BodyText 无自身尺寸复验、`SkillPromptComposer` 分组非优先级序、比较器声明不一致） | 各自单列修复项，不混入框架迁移（仓库变更纪律：不混无关格式化/重构） |

---

## 10. 附录：指纹域 → 种类契约的归属对照

| 指纹域 | 今日输入（`PluginDeclarationFingerprint.cs`） | 本框架后的产生方 |
|---|---|---|
| `perm.*`（8 项） | `:54-61` 权限授权 | 不变（非贡献面） |
| `workers`/`deps`/`isolation`/`caps` | `:63-76` | 不变（非贡献面；caps 为通用域，新发布能力自动覆盖） |
| `extensions` | `:78-87` 规范 kind + 原始条目规范 JSON（字面量） | 契约解析入口产出**相同的原始条目值**；字节由黄金夹具锁定 |
| `data` | `:90-97` 名 + 采集内容规范 JSON/错误 | 同上；`CollectDataEntry` 增加变量表参数后，采集内容仍为唯一哈希面 |
| `mcp` | `:100-107` Id + GenerationKey | 不变；且规则：extensions 来源永不并入（风险 6） |
| `triggers` | `:110-121` 展开监视路径等 | 不变（触发器迁移为未来种类时原样保留） |
| `inspections.contentReadAll` | `:124` | 不变 |
