# Anima 与 MOSS-Image 动态打标预设（Dynamic Caption Preset）编写与训练规范指南

> **建议保存路径**：`docs/13-dynamic-lora-presets.md`  
> **预设文件存放目录**：`%LOCALAPPDATA%\AnimaCaptioner\presets\*.json`（或项目根目录 `presets\*.json`）

## 1. 为什么角色 LoRA 与画风 LoRA 需要截然不同的打标策略？

在训练 **Anima**（基于 Qwen3-0.6B 编码器 + 28 层 DiT）或 **[MOSS-Image](https://github.com/OpenMOSS/MOSS-Image)**（基于 5B DiT + Qwen3.5-9B 编码器）的 LoRA 时，扩散模型遵循一条铁律：**凡是被明确写进提示词（Caption）的特征，都会由冻结的 Text Encoder 承担解释责任；凡是没有写进提示词（或仅由触发词代表）的共性特征，才会被压入 LoRA 权重中**。

因此，针对同一个底模，训练 **角色 LoRA** 与训练 **画风 LoRA** 的标注策略完全相反：

| 对比维度 | 角色 LoRA (`CharacterLora`) | 画风 / 画师 LoRA (`StyleLora`) |
| :--- | :--- | :--- |
| **需要让 LoRA 记住的目标** | 角色的**固有外貌特征**（发色、基础发型、刘海、瞳色、兽耳/角/光环、面部标志）。 | 画师的**线条粗细、笔触质感、上色技法、阴影层次与整体色调美学**。 |
| **触发词格式与位置** | **不加 `@` 前缀**（如 `hatsune miku`），置于 `Quality/Meta` 与人数标签（`1girl, solo`）之后的 `Character` 槽位。 | **强制使用 `@` 前缀**（如 `@my_style`），置于 `Artist` 槽位或在句首置顶（MossAD 画风归因法）。 |
| **必须剥离/禁写的词 (`strip_tags`)** | **剥离固定发色、发型、瞳色**（如 `blonde hair, twintails, blue eyes`），严禁在自然语言尾段重复描述发色瞳色。 | **剥离一切画风、媒介与渲染词**（如 `anime coloring, cel shading, watercolor, thick outlines, pastel colors`）及其他画师名。 |
| **必须详尽描述的词 (`must_describe`)** | **详写每一件衣服单品**（即便穿的是官方默认常服也要拆解写出，否则推理时无法换装）、动作姿态、表情、镜头视角、背景与画风。 | **详写画面中的一切实体内容**：角色身份、精确发色发型、瞳色、表情、全套服饰、空间构图、背景道具与画面文字，防止画风污染脸型或背景。 |

## 2. Anima 与 MOSS-Image 官方训练打标规范对比

根据 [Anima 官方技术规范](https://github.com/shootthesound/Fizgig/blob/master/docs/ANIMA.md)、[Kohya/SD-Trainer Anima 实践指南](https://github.com/Rinne414/lora-training-skill) 以及 [OpenMOSS MOSS-Image 官方用户手册](https://github.com/OpenMOSS/MOSS-Image/blob/main/USERGUIDE_ZH.md) 与 [MossAD 57-Artist 微调报告](https://huggingface.co/RicemanT/MossAD-Demo)，两大模型在训练打标上有以下关键差异：

| 规范维度 | CircleStone Labs **Anima** | OpenMOSS **MOSS-Image (5B DiT)** |
| :--- | :--- | :--- |
| **文本编码器与 Token 上限** | **Qwen3-0.6B** + LLM Adapter（**严禁微调 Adapter**），硬上限 **512 Tokens**（超出部分直接截断）。 | **Qwen3.5-9B**（**训练 LoRA 时严禁微调 Text Encoder**），支持高达 **4,096 Tokens** 的长上下文。 |
| **官方推荐 Tag 顺序** | `Quality -> Meta -> Year -> Safety -> Character -> Series -> @Artist -> General` + `. ` + 自然语言尾段。 | `Quality -> Score -> Meta -> Year -> Character -> Series -> @Artist -> General` + 自然语言长描述。 |
| **质量前缀词与无用词** | 推荐 `masterpiece, best quality, safe`。**注意：Anima 训练未包含 `score_*` 标签（除非特殊合成集），默认应剔除 `score_9` 等无效词**。 | 推荐 `best quality, highres`。负面提示词推荐 `nsfw, worst quality, bad anatomy, watermark, jpeg artifact`。 |
| **换行符 (`\n`) 与单行约束** | **强制单行 (`force_single_line_output: true`)**：Kohya 的 `read_caption` 默认执行 `caption.split("\n")[0]`，一旦换行会导致第二行及之后的自然语言完全丢失不参与训练！ | 支持多段长文本；若画面内包含需要渲染的多行文字，可在双引号内使用 `\n` 控制文字排版换行。 |
| **画面文字（OCR）与水印处理** | 支持基础文字描述，建议将 T 恤、招牌、对话框内的文字放入引号中。 | **极强的中英日文字还原能力**：所有非水印可见文字必须**原封不动放入双引号 `"..."` 中**（严禁翻译或转写拼音）；**绝对禁止描述水印**（MOSS-Image 对水印极其敏感，一旦标注或保留水印会顽固焊死在权重中）。 |

## 3. 内置的四大黄金预设一览

系统在 `Core/CaptionPreset.cs` 与 `presets/` 目录下内置了 4 个核心预设：

1. **`anima-character-lora.json`（Anima · 角色 LoRA）**
   * 单行 512 Token 约束，角色触发词不加 `@`，自动剥离 `blonde hair / twintails / blue eyes / ahoge` 等 50+ 个固有外貌词，强制要求 AI 详写服装单品、动作、表情、镜头与背景。
2. **`anima-style-lora.json`（Anima · 画风 LoRA）**
   * 自动为触发词补齐 `@` 前缀（如 `@my_style`），自动清空 `artist` 分区并剥离 `cel shading / anime coloring / thick outlines / painterly` 等画风媒介词，强制要求 AI 详写人物发色、瞳色、服饰与背景。
3. **`moss-character-lora.json`（MOSS-Image · 角色 LoRA）**
   * 4,096 Token 预算，采用 `CompactTagsRichProse`（精简身份标签 + 详尽空间自然语言），剥离角色固有发色瞳色，强制双引号原样转录画面中英日文字，100% 屏蔽水印描述。
4. **`moss-style-lora.json`（MOSS-Image · 画风 LoRA）**
   * 采用 MossAD 验证的前置 `@style` 归因触发策略（`pin_trigger_at_front: true`），剥离全部画风、笔触与渲染词，高密度还原人物五官、服装材质、前景/中景/背景空间布局与双引号内文字。

## 4. 如何编写自定义预设 JSON（完整字段规范与示例）

你可以在 `%LOCALAPPDATA%\AnimaCaptioner\presets\` 目录下新建任意 `.json` 文件（例如 `moss-my-waifu-char.json`），软件启动或刷新预设菜单时会自动加载。

### 4.1 JSON 字段详解表

| 字段名 (JSON Key) | 类型 | 必填 | 作用说明 |
| :--- | :--- | :--- | :--- |
| `id` | `string` | 是 | 预设唯一标识符（英文小写加连字符，如 `"anima-char-miku"`）。 |
| `name` | `string` | 是 | UI 菜单中显示的预设名称（如 `"Anima · 初音未来角色 LoRA"`）。 |
| `description` | `string` | 否 | 预设用途说明。 |
| `model_family` | `string` | 是 | 目标底模家族：`"Anima"`、`"MossImage"`、`"BooruStandard"` 或 `"NaturalLanguageDiT"`。 |
| `training_intent` | `string` | 是 | 训练意图：`"CharacterLora"`（角色）、`"StyleLora"`（画风）、`"ConceptLora"`（服装/概念）或 `"General"`（通用）。 |
| `caption_mode` | `string` | 是 | 输出语法模式：`"Hybrid"`（标签+自然语言）、`"TagsOnly"`（纯标签）或 `"ProseOnly"`（纯自然语言）。 |
| `hybrid_profile` | `string` | 否 | 混合模式配比：`"AnimaStandard"`（标准标签+尾段）或 `"CompactTagsRichProse"`（精简标签+长自然语言，适合 MOSS-Image）。 |
| `trigger_word` | `string` | 否 | 该 LoRA 的触发词（如 `"hatsune miku"` 或 `"@ask_style"`）。留空则不注入固定触发词。 |
| `pin_trigger_at_front` | `bool` | 否 | `true` 将触发词死锁在第 1 个位置（索引 0，适合 MossAD 画风训练）；`false` 则按标准分区槽位放置。 |
| `prefix_style_trigger_with_at` | `bool` | 否 | 在 `StyleLora` 下是否自动为触发词和画师名补齐 `@` 前缀（Anima 与 MOSS-Image 均推荐设为 `true`）。 |
| `quality_prefix_tags` | `string[]` | 否 | 自动保证存在于标签区开头的质量前缀词（如 `["masterpiece", "best quality", "safe"]`）。 |
| `max_token_budget` | `int` | 否 | Token 预算上限（Anima 设为 `512`，MOSS-Image 设为 `4096`）。 |
| `force_single_line_output` | `bool` | 否 | 是否强制将最终标注合并为单行文本（Anima / Kohya 训练必设为 `true`）。 |
| `strip_tag_sections` | `string[]` | 否 | 本地后处理要整区剥离的分类（如画风 LoRA 设为 `["artist"]`）。 |
| `strip_tags` | `string[]` | 否 | 本地后处理要强制剔除的具体标签黑名单（如某角色特定的发饰、泪痣，或画风词）。 |
| `auto_strip_core_hair_eye_in_character_lora` | `bool` | 否 | 当 `training_intent` 为 `"CharacterLora"` 时，是否自动剔除内置的 50+ 个发色/基础发型/瞳色/兽耳标签。 |
| `preserve_exact_visible_text_in_quotes` | `bool` | 否 | 是否强制要求 AI 将画面内的中英日文字原样包裹在 `""` 中输出（MOSS-Image 必设为 `true`）。 |
| `exclude_watermarks_and_signatures` | `bool` | 否 | 是否严禁描述水印与签名，并自动过滤 `watermark` / `signature` 标签。 |
| `must_omit_dimensions` | `string[]` | 否 | 注入 System Prompt 的“禁写维度清单”（告诉 AI 哪些特征归属 LoRA 权重，绝对不要写）。 |
| `must_describe_dimensions` | `string[]` | 否 | 注入 System Prompt 的“必写维度清单”（告诉 AI 哪些特征必须详细拆解，以实现与 LoRA 解耦）。 |
| `system_prompt_template` | `string` | 否 | 自定义提示词模板，支持变量占位符：`{{trigger_word}}`、`{{model_family}}`、`{{training_intent}}`、`{{strip_tags}}`。 |

### 4.2 实战编写示例：为特定角色（含专属特征黑名单）编写 JSON 预设

假设你要为 **MOSS-Image** 训练一个自定义角色 `星野爱 (hoshino ai)` 的角色 LoRA，她除了常规的紫发紫眼外，还有标志性的 **星形瞳孔（`star-shaped pupils`）** 和 **侧马尾发饰（`rabbit hair ornament`）**。如果你希望触发词 `hoshino ai` 自带这些特征，只需这样写 JSON：

```json
{
  "id": "moss-char-hoshino-ai",
  "name": "MOSS-Image · 星野爱角色 LoRA",
  "description": "锁死星野爱的紫发、星形瞳孔与兔子发饰，详写每张图的演出服/私服、舞台灯光与背景",
  "model_family": "MossImage",
  "training_intent": "CharacterLora",
  "caption_mode": "Hybrid",
  "hybrid_profile": "CompactTagsRichProse",
  "trigger_word": "hoshino ai",
  "pin_trigger_at_front": false,
  "prefix_style_trigger_with_at": true,
  "quality_prefix_tags": ["best quality", "highres"],
  "max_token_budget": 4096,
  "force_single_line_output": false,
  "strip_tag_sections": [],
  "strip_tags": [
    "star-shaped pupils",
    "symbol-shaped pupils",
    "rabbit hair ornament",
    "hair ornament",
    "watermark",
    "signature"
  ],
  "auto_strip_core_hair_eye_in_character_lora": true,
  "preserve_exact_visible_text_in_quotes": true,
  "exclude_watermarks_and_signatures": true,
  "must_omit_dimensions": [
    "Purple hair, long hair, sidelocks, purple eyes, star-shaped pupils, and rabbit hair ornament",
    "Any watermarks or artist signatures"
  ],
  "must_describe_dimensions": [
    "Idol stage outfit or casual clothing items, colors, frills, and microphone prop",
    "Stage pose, hand gestures, wink or smile expression",
    "Concert lighting, spotlights, glowsticks, and background stage screen text inside double quotes"
  ],
  "system_prompt_template": "You are captioning a training image for a MOSS-Image Character LoRA of `{{trigger_word}}`. Do NOT mention her purple hair, star-shaped pupils, or rabbit hair ornament. Describe her current outfit, pose, expression, lighting, background, and any visible text in double quotes."
}
```
