# AnimaCaptioner 通用多模式架构升级与工具链演进设计方案

> **目标仓库**：`a1544676809/AnimaCaptioner`  
> **协同仓库**：`a1544676809/AnimaAIProxy`  
> **建议放置路径**：`docs/dev/architecture-upgrade-plan.md`

## 1. 背景与核心目标

`AnimaCaptioner` 目前围绕 Anima 数据集的“前置结构化标签（Danbooru/Booru 风格）+ 尾段自然语言描述（Prose Tail）”混合标注范式构建。随着不同训练架构与微调范式（如纯标签系的 Illustrious / NoobAI / Kohya，纯自然语言系的 Flux / SD3.5 / Qwen-Image，以及标签+自然语言混合系的 Anima / Hunyuan）的普及，项目需要在保持现有非破坏性编辑与高可靠性优势的基础上，演进为**通用多模式数据集标注与自动打标工作台**。

与此同时，同源项目 `a1544676809/AnimaAIProxy`（在其 `Vendor/AnimaCaptioner` 目录中复用了本项目的词库底层）已经演化出一套更加先进的工具链与多轮流式执行架构（包括 `VocabService` 的批量解析与扫描、`PageReader` 网页正文提取、`ToolCallAssembler` 流式工具拼装、`RequestShaper` 多厂商协议整形等）。

本次架构升级的核心目标如下：
1. **文档与解析层支持三模式自适应与强制限定**：原生支持 **纯标签（`TagsOnly`）**、**纯自然语言（`ProseOnly`）**、**标签+自然语言混合（`Hybrid`）** 三种标注模式。
2. **图片自动打标（AI Captioning）支持模式强约束**：单图 AI 助手（`TagAgent`）与批量打标（`BatchPlan`）可根据用户限定的打标模式，动态切换提示词策略、裁剪暴露给大模型的工具 Schema，并在落盘/应用前执行输出结构强校验。
3. **吸收 `AnimaAIProxy` 先进工具链，大幅提升打标效率**：将 `vocab_resolve`、`vocab_scan`、`read_page`、批量 `view_regions` 及两阶段极速打标流水线（Two-Stage Fast Batching）引入 `AnimaCaptioner`，将单图 Agent 往返轮次降低 60% 以上，批量打标吞吐量提升 3~5 倍。
4. **抽取共享核心层，消除跨项目手动拷贝**：统一 `AnimaCaptioner` 与 `AnimaAIProxy` 的词库服务与工具执行契约，消除 `Vendor/AnimaCaptioner` 手工同步负担。

## 2. 总体架构与模块演进蓝图

升级后的核心模块分为四层：**文档与格式策略层**、**统一词库与工具宿主层**、**多模式打标编排层** 和 **UI 与工作流适配层**。

| 架构分层 | 涉及现有文件 (`AnimaCaptioner`) | 新增 / 移植自 `AnimaAIProxy` 的组件 | 核心职责 |
| :--- | :--- | :--- | :--- |
| **文档与格式策略层** | `Core/PromptText.cs`, `Core/PromptDoc.cs`, `Core/TagSections.cs`, `Core/TagOrder.cs` | `Core/CaptionMode.cs`, `Core/CaptionFormatPolicy.cs` | 按 `TagsOnly`、`ProseOnly`、`Hybrid` 三种模式执行无损解析、边界切分、分区排序与序列化。 |
| **词库与外部工具层** | `Core/VocabDb.cs`, `Core/TagSearch.cs`, `Core/ImageRegion.cs`, `Core/SearchClient.cs` | `Core/VocabService.cs` (`vocab_resolve`, `vocab_scan`), `Core/PageReader.cs`, `Core/ArticleExtractor.cs` | 提供批量标签别名归一、全文标签提取、多区域并行裁剪（`view_regions`）及网页正文读取（`read_page`）。 |
| **LLM 协议与循环层** | `Core/LlmClient.cs`, `Core/ToolStream.cs` | `Core/ToolCallAssembler.cs`, `Core/RequestShaper.cs`, `Core/ProviderProfile.cs`, `Core/ToolLoop.cs` | 统一本地 `llama-server` 与云端 API（OpenAI / Claude / Gemini）的流式工具调用拼装与推理内容过滤。 |
| **多模式打标编排层** | `Core/AgentKit.cs`, `Core/TagAgent.cs`, `Core/BatchPlan.cs`, `AgentHost.cs` | `Core/CaptioningPipeline.cs` | 根据目标打标模式动态裁剪工具 JSON Schema、组装 System Prompt、执行两阶段批量打标与输出结构校验。 |

## 3. 核心设计一：三模式文档模型与解析/序列化策略

### 3.1 问题分析
目前 `Core/PromptText.cs` 在解析任意 `.txt` 标注文本时，默认采用启发式规则探测标签与自然语言的分界线（例如遇到句号 `.` 结尾的质量前缀，或遇到首字母大写、词数 $\ge 3$ 且不在 SQLite 词库中的短语时，将其切入 `ProseTail`）。
* 在 **纯标签模式** 下，未收录进 `seed.json` 的较长触发词（Trigger Words）或自然短语风格标签会被误判为 `ProseTail` 并排到末尾。
* 在 **纯自然语言模式** 下，段落开头带有逗号的子句会被误拆分为独立标签，并被 `Core/TagSections.cs` 按分类重排顺序，破坏自然语言句意。

### 3.2 `CaptionMode` 与格式策略契约

新增 `Core/CaptionMode.cs`，在项目设置（`AppSettings.cs`）与文档实例（`PromptDoc.cs`）中引入显式模式配置：

```csharp
namespace AnimaCaptioner.Core;

public enum CaptionMode
{
    /// <summary>
    /// 纯标签模式：全量按逗号分隔标签处理，ProseTail 恒为空，不执行句子启发式切分。
    /// 适用场景：Illustrious, NoobAI, Pony, SDXL, Kohya 标准标签数据集。
    /// </summary>
    TagsOnly,

    /// <summary>
    /// 纯自然语言模式：全量作为自然语言段落处理，Tags 列表恒为空，不拆解逗号也不重排顺序。
    /// 适用场景：Flux, SD3.5, Qwen-Image 等纯自然语言描述数据集。
    /// </summary>
    ProseOnly,

    /// <summary>
    /// 标签 + 自然语言混合模式：前置结构化分类标签（按 Anima 规范排序）+ 尾段自然语言描述。
    /// 适用场景：Anima 基础/微调数据集、混元等混合架构。
    /// </summary>
    Hybrid
}

public sealed record CaptionFormatOptions(
    CaptionMode Mode = CaptionMode.Hybrid,
    string TagSeparator = ", ",
    string HybridDelimiter = ". ",
    bool EnforceAnimaSectionOrder = true,
    bool PinTriggerWordsFirst = false,
    bool EscapeParentheses = false
);
```

### 3.3 `PromptText` 与 `PromptDoc` 行为改造

1. **`PromptText.Parse(string text, CaptionFormatOptions options)`**：
   * 当 `Mode == CaptionMode.TagsOnly` 时：仅按逗号（`,` / `，`）切分 Token，进行转义还原、空白归一与去重，所有词条均进入 `Tags` 列表，`ProseTail = string.Empty`。若开启 `PinTriggerWordsFirst`，首个自定义触发词固定在索引 `0`，不参与 `TagOrder.Canonicalize` 的跨区重排。
   * 当 `Mode == CaptionMode.ProseOnly` 时：不做任何逗号标签切分，`Tags` 为空列表，规范化换行与首尾空白后完整存入 `ProseTail`。
   * 当 `Mode == CaptionMode.Hybrid` 时：保持现有 Anima 混合切分逻辑，同时支持优先识别显式分隔符（如句号分隔 `. ` 或空行分隔 `\n\n`），再回退到词库 + 句子特征启发式切分。
2. **`PromptText.Serialize(IReadOnlyList<TagItem> tags, string proseTail, CaptionFormatOptions options)`**：
   * `TagsOnly`：仅序列化 `tags`（若存在历史遗留的 `proseTail`，提供一键“扫描提取为标签（`vocab_scan`）”或“丢弃自然语言尾段”的转换工具）。
   * `ProseOnly`：仅输出 `proseTail` 正文。
   * `Hybrid`：按 `TagSections`（`[Quality / Meta / Year / Safety] -> [Character] -> [Series] -> [Artist] -> [General]`）拼接标签段，通过 `HybridDelimiter` 连接 `proseTail`。

## 4. 核心设计二：支持模式限定的图片打标引擎（AI Captioning）

用户在对单张图片或整个数据集执行图片打标时，可在 UI 或批量计划（`BatchPlan`）中显式指定打标产出模式：**仅打标签（`TagsOnly`）**、**仅打自然语言（`ProseOnly`）** 或 **标签+自然语言混合（`Hybrid`）**。

为了杜绝大模型“不听指令”（例如要求只打标签却在 `propose` 中塞入自然语言，或要求纯自然语言却输出一堆逗号标签），我们在 **Prompt 组装**、**Tool Schema 动态裁剪** 和 **Proposal 落地校验** 三层同时施加约束：

### 4.1 三模式动态工具裁剪与 Schema 约束 (`Core/AgentKit.cs`)

| 打标模式 | 暴露给模型的工具列表 | `propose` / `label_images` 参数 Schema 动态定义 | `AgentHost` 落地校验规则 |
| :--- | :--- | :--- | :--- |
| **限定纯标签 (`TagsOnly`)** | `current_image`, `view_regions`, `vocab_search`, `vocab_resolve`, `vocab_scan`, `web_search`, `read_page`, `propose` | 仅包含 `add_tags` (`string[]`, required), `remove_tags` (`string[]`), **移除 `prose` 属性** | 强制忽略任何 `Prose` 变更；对 `AddTags` 自动执行本地 `VocabService.Resolve` 别名归一与分类染色。 |
| **限定纯自然语言 (`ProseOnly`)** | `current_image`, `view_regions`, `web_search`, `read_page`, `propose`（**隐藏所有 `vocab_*` 工具**） | 仅包含 `prose` (`string`, required), `prose_mode` (`"overwrite" \| "append"`), **移除 `add_tags` / `remove_tags` 属性** | 强制忽略任何标签增删；校验 `Prose` 是否包含完整句子结构，拒绝纯逗号罗列串。 |
| **限定混合模式 (`Hybrid`)** | 全量工具集 (`current_image`, `view_regions`, `vocab_*`, `web_search`, `read_page`, `propose`) | 同时包含 `add_tags`, `remove_tags`, `prose`，并增加 `hybrid_profile`（`anima_standard` / `compact_tags_rich_prose`） | 严格遵循 `docs/09-tags-vs-prose.md` 分工：身份/版权/画师/基础外观入 `Tags`，复杂姿态/互动/光影/构图入 `Prose`。 |

### 4.2 模式专属 System Prompt 体系 (`Core/LlmClient.cs` & `Core/AgentKit.cs`)

将现有的 `KeywordSystemPrompt`、`ProseSystemPrompt` 与 `TagAgent` 系统提示词整合为结构化的 `CaptionPromptBuilder`：
* **`BuildTagsOnlyPrompt()`**：要求模型系统性扫描人数、角色身份、作品版权、发色发型、瞳色、面部表情、服装配饰、身体姿态、构图视角与背景环境；规定必须先通过 `vocab_resolve` 批量校验候选标签，严禁臆造未在词库中存在的长句标签。
* **`BuildProseOnlyPrompt()`**：要求模型直接观察图像（必要时使用 `view_regions` 查看细节），用准确、客观、富有画面层次感的自然语言描述主体外貌、动作张力、服饰穿戴细节、空间位置关系、光影色调与背景环境；严禁使用主观评价词（如“非常漂亮”、“令人惊叹”）、严禁臆测画面外的剧情，也不要罗列 Danbooru 标签（如 `1girl, solo`）。
* **`BuildHybridPrompt()`**：明确划分标签与自然语言的边界——标签区负责锚定可枚举的标准特征（质量分、安全评级、角色名、作品名、画师、核心外观 Tag），自然语言区负责描述标签难以表达的复杂交互（如“左手轻轻掀起帽檐，眼神看向画面右侧的暖色街灯，雨滴沿着透明雨伞边缘滑落”），避免标签区与自然语言区出现高度冗余的机械重复。

## 5. 核心设计三：移植 `AnimaAIProxy` 先进工具，实现高效打标

`AnimaAIProxy` 中的工具实现相比 `AnimaCaptioner` 具有明显的代差优势。我们将按以下清单把 `AnimaAIProxy` 的高阶组件移植并整合进 `AnimaCaptioner`：

### 5.1 升级词库工具链：引入 `VocabService`（`vocab_resolve` + `vocab_scan`）

* **现状瓶颈**：`AnimaCaptioner` 的 `AgentKit.cs` 只有单次查询的 `search_tags(query)`。当大模型在图中看到 15~20 个视觉要素时，往往需要连续发起 5~8 次 `search_tags` 工具调用来确认拼写，导致单图打标耗时长达数十秒。
* **移植与优化方案**：
  1. 将 `AnimaAIProxy/Core/VocabService.cs` 迁入 `AnimaCaptioner/Core/VocabService.cs`，直接复用底层的 `VocabDb` 与 `TagSearch`。
  2. 新增 **`vocab_resolve(tags: string[])`** 工具：模型在观察图片后，一次性提交 20 个英文或中文候选特征词，本地 SQLite 毫秒级批量返回每个词的精确命中结果、别名重定向（Alias $\rightarrow$ Canonical Tag）、词频与所属分区（Category），未命中的词自动附带 Top-3 相近候选词。**单次工具调用即可完成整张图的标签核验。**
  3. 新增 **`vocab_scan(text: string)`** 工具：直接对一段自然语言描述或中英混合草稿进行 Aho-Corasick / Token 窗口扫描，自动提取出文中隐含的所有标准 Danbooru 标签。在 **混合打标（`Hybrid`）** 或 **自然语言转标签** 场景下，模型只需写出一段视觉观察描述，即可由 `vocab_scan` 瞬间提取出基础标签集合。

### 5.2 升级视觉工具：支持多区域并行放大 `view_regions`

* **现状瓶颈**：`Core/ImageRegion.cs` 的 `view_region(x, y, w, h)` 每次只能裁剪一个局部区域。遇到多人插画或细节丰富的画面（需同时确认面部瞳色、手部持物、服装徽章），模型必须串行往返多轮。
* **优化方案**：扩展 `Core/ImageRegion.cs`，支持 `view_regions(regions: [{x, y, w, h, label}])`（单次最多支持 4 个归一化矩形框）。在本地完成并发裁剪与 EXIF 方向校正后，作为多张局部图块（或带编号边框的拼接图）一次性回传给视觉模型，减少多模态长上下文的重复发送开销。

### 5.3 移植网页正文提取：`PageReader` 与 `ArticleExtractor` (`read_page`)

* **现状瓶颈**：`AnimaCaptioner` 仅有 `Core/SearchClient.cs`（`web_search`），只返回搜索引擎摘要片段；遇到新番角色或特定画师风格时，摘要往往不包含完整的角色外貌 Tag（如发饰位置、瞳孔特征、代表性服饰）。
* **移植方案**：将 `AnimaAIProxy/Core/PageReader.cs` 与 `AnimaAIProxy/Core/ArticleExtractor.cs` 移植到 `AnimaCaptioner/Core/`，在 `AgentKit.cs` 中注册 `read_page(url, max_chars)` 工具，使 Agent 在搜索到 Wiki / 萌娘百科 / Danbooru Wiki 条目后能直接提取正文设定。

### 5.4 引入两阶段极速批量打标流水线（Two-Stage Fast Batching）

针对 `Core/BatchPlan.cs` 的大批量图片打标场景，引入无需多轮工具往返的 **Fast Pipeline 模式**：
1. **Stage 1（单轮多模态结构化输出）**：将压缩后的图像负载（`ImagePayload`）发送给视觉大模型，要求在单轮响应中直接输出结构化 JSON：`{ "raw_tags": [...], "prose": "..." }`（取决于所选 `CaptionMode`）。
2. **Stage 2（本地 `VocabService` 零延迟对齐与装配）**：C# 本地直接调用 `VocabService.Resolve(raw_tags)` 与 `VocabService.Scan(prose)`，自动将非规范词映射为标准标签，按 `TagSections` 排序，并根据是否有低置信度未命中词决定是直接写入提案/文件，还是仅针对疑难词触发 1 轮轻量级 LLM 修正。相比全量多轮 Agent 循环，**API 耗时与 Token 消耗可降低 70% 以上**。

## 6. 分阶段实施路线图（Roadmap & PR 拆分计划）

为了保证主分支稳定并通过 `tests/corecheck.csproj` 全部回归测试，我们将升级拆分为 4 个独立的渐进式阶段（每个阶段对应一个独立、可测试的 PR）：

### Phase 1：多模式文档模型与解析器重构（Foundation）
* **新增文件**：`Core/CaptionMode.cs`、`Core/CaptionFormatPolicy.cs`、`tests/CaptionModes.cs`。
* **修改文件**：
  * `Core/PromptText.cs`：新增带 `CaptionFormatOptions` 的 `Parse` 与 `Serialize` 重载，完整支持 `TagsOnly`、`ProseOnly`、`Hybrid` 三种模式。
  * `Core/PromptDoc.cs` & `Core/AppSettings.cs`：保存当前数据集或草稿的默认 `CaptionMode` 与分隔符配置。
* **验收标准**：纯标签文本中含空格的自定义触发词不再被误判为 `ProseTail`；纯自然语言段落中的逗号不再被拆分为 Tag；现有 `tests/RoundTrip.cs` 与 `tests/Prose.cs` 100% 通过。

### Phase 2：移植 `AnimaAIProxy` 词库与网页工具（Tooling Upgrade）
* **新增/移植文件**：
  * 从 `AnimaAIProxy` 移植 `Core/VocabService.cs`、`Core/PageReader.cs`、`Core/ArticleExtractor.cs`、`Core/ToolCallAssembler.cs`。
* **修改文件**：
  * `Core/ImageRegion.cs`：新增批量区域裁剪 `CropRegions`。
  * `Core/AgentKit.cs` & `AgentHost.cs`：注册 `vocab_resolve`、`vocab_scan`、`read_page`、`view_regions` 工具，并兼容原有 `search_tags` / `view_region`。
* **验收标准**：新增 `tests/VocabBatch.cs` 验证 `vocab_resolve` 与 `vocab_scan` 的准确率与耗时；Agent 工具调用往返轮次在基准测试中显著下降。

### Phase 3：模式限定的图片打标引擎与两阶段批量流水线（Captioning Engine）
* **新增文件**：`Core/CaptionPromptBuilder.cs`、`Core/FastBatchCaptioner.cs`。
* **修改文件**：
  * `Core/AgentKit.cs` & `Core/TagAgent.cs`：支持按 `CaptionMode` 动态生成工具 Schema（如 `TagsOnly` 移除 `prose` 参数，`ProseOnly` 移除 `add_tags`/`remove_tags` 参数并隐藏词库工具）。
  * `Core/BatchPlan.cs` & `MainWindow.xaml(.cs)`：在 AI 助手面板与批量操作栏中提供 `打标模式：[混合 (Anima) | 仅标签 (Tags) | 仅自然语言 (Prose)]` 切换器。
* **验收标准**：在三种限定模式下分别运行单图与批量打标，输出格式严格符合目标模式，零跨模式污染。

### Phase 4：抽取共享 `Anima.Core` 契约，反哺 `AnimaAIProxy`（Unification）
* 将 `VocabDb`、`VocabService`、`TagSearch`、`TagSections`、`SafetyRating`、`ToolCallAssembler`、`RequestShaper` 整理为共享项目或 Git Submodule / NuGet 包，替换 `AnimaAIProxy/Vendor/AnimaCaptioner` 下的手工拷贝文件，实现两库同源演进。
