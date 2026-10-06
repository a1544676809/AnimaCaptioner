# 标签还是自然语言：LoRA 训练集怎么分工

> 哪些内容该写成 Danbooru 标签、哪些该写成自然语言散文、散文要守什么规范。

## 先看作者本人怎么做的

这是最有分量的一条证据。作者 `tdrussell` 在 [HF #9](https://huggingface.co/circlestone-labs/Anima/discussions/9) 回答「数据集是什么格式」时说：

> For the captioning, every image has multiple caption variants and it trains on all of them.
> **Full tag list, tag list with dropout, tags followed by caption, caption followed by tags,
> short caption only, long caption only.**
>
> Loras are usually very light finetunes so probably a wide variety of captioning styles could work.
> **It's not like you have to exactly follow how the base model was trained.**

两件事由此确定：

1. **没有唯一正确格式。** 训练时同图配了六种变体，纯标签、纯散文、以及两种混合顺序**都在训练数据里**。
2. **LoRA 不必照抄。** 作者明确说 LoRA 是轻量微调，格式宽容度很高。

他自己那个 style LoRA（[CivitAI 2536147](https://civitai.com/models/2536147)）用的是**纯自然语言**——captioning 脚本 [`bulk_caption_generic.py`](https://gist.github.com/tdrussell/37f3099a34f1ee7343c7ae321feb1aa5) 里既没有 tagger 也没有词表，默认 prompt 就是 `Write a descriptive caption for this image.`；模型页写着 *"Natural language prompts work best."*

## 但真实数据集几乎都是纯标签

作者本人是特例。我拉了一个公开的 Anima LoRA 数据集（[kongbai-84/soultide_lora_Anima](https://huggingface.co/kongbai-84/soultide_lora_Anima)，HF #220 里作者自己分享的 90+ 角色 LoRA），797 条 caption 实测：

| 特征 | 结果 |
|---|---|
| 含句号+大写开头的散文句 | **0 / 797** |
| 以标点结尾 | 227 / 797（28.5%） |
| 用 `@画师` | 2 / 797 |
| 逗号分隔的条目 | 平均 12 条，最多 37 |
| 长度 | 平均 272 字符（最短 4、最长 977） |

也就是说：**全是一行逗号分隔的标签，一条散文都没有**。

## 分工：按「标签能不能表达」切

把两边证据合起来，分界线其实很清晰：

### 用标签（离散、可穷举、有正典写法）

| 内容 | 例子 | 为什么 |
|---|---|---|
| 人数 / 人称 | `1girl` `2girls` `solo` | 官方第二节，有固定词表 |
| 角色 / 作品 | `maomao (kusuriya no hitorigoto)` | 正典名唯一，散文写法模型认不出 |
| 画师 | `@sayori style` | **必须带 `@`**，官方说否则"效果非常弱" |
| 外观 | `silver hair` `blue eyes` `twintails` | Danbooru 有正典标签，离散 |
| 服装 / 道具 | `serafuku` `frilled apron` | 同上 |
| 姿势 / 表情 | `standing` `sitting` `smile` | 单标签能稳定表达 |
| 取景 / 机位 | `upper body` `from below` `dutch angle` | **这类必须用标签，见下方警告** |
| 场景元素 | `classroom` `cherry blossoms` | 离散名词 |
| 质量 / 年代 / 安全 | `masterpiece` `year 2025` `sensitive` | 官方控制词 |

### 用自然语言（连续、有因果、有归属）

| 内容 | 例子 | 为什么标签做不到 |
|---|---|---|
| **空间关系** | `Place Alice on the left, Marisa on the right.` | 标签没有"谁在谁左边"的语法 |
| **动作归属** | `Her left hand rests on his shoulder.` | `hand on shoulder` 说不清是谁的手搭谁 |
| **接触关系** | `She grips his wrist while looking up at him.` | 多人的肢体归属靠标签必然混乱 |
| **视线指向** | `Keep both faces readable and turned toward each other.` | `looking at viewer` 只对镜头，不对彼此 |
| **因果链** | `as a gust of wind sends petals into her face` | 标签是名词，没有因果 |
| **光照方向** | `Use soft window light from the left.` | 有 `backlighting` 但没有"从左" |
| **景深 / 清晰区** | `Keep her face sharp against a blurred background.` | 无对应标签 |
| **色彩氛围** | `Warm palette: amber, dusty rose.` | 标签只能给单个颜色词 |
| **比例 / 尺度** | `The desk reaches her waist.` | 没有这种标签 |
| **画面内文字** | `a sign that reads "ANIMA"` | 内容任意，无法穷举 |

社区文档 [comfyui-good-anima](https://github.com/ShiroEirin/comfyui-good-anima) 把这条界线总结成三层，和我上面推出的划分一致：

```text
hard_tags（正典标签） → soft_phrases（视觉短语） → nltags_block（成句描述）
拼装： hard_tags + ", " + soft_phrases + ", " + nltags_block
```

它的规则里有一条值得单独抄下来：**`nltags_block` 不重复已在 `hard_tags` 里出现的外观/服装。**

## 一个必须知道的冲突：散文会盖掉取景标签

[HF #140](https://huggingface.co/circlestone-labs/Anima/discussions/140) 里 `Jamerrone` 做了对照实验（纯标签 / 纯散文 / 混合），结论是：

> Tags only = simpler image, sharp lines, flatter colours, **high quality, and no or few issues.**
> Long LLM natural language: highly detailed … **it always has minor issues and always has broken hands.**
> Hybrid = … it gives you **80% of the subject control** you get with only tags …
> It can, however, very easily separate the subject from the background … **because I described the
> background; it ignored all the angle tags like "upper body", "close-up", etc. … It kept generating a wide shot.**

**这是对标注最实际的一条**：一旦用散文描述背景，取景类标签会被无视。同帖 `rconhf` 补充了长度上限：

> Anima really doesn't like long prompts. once you go past ~2–3 paragraphs, it tends to fall apart
> into mush. structure breaks down, and **hands are usually the first thing to go.**

## 自然语言要守的规范

### 官方模型卡的原文（`## Natural language prompting tips`）

| 项 | 原文 |
|---|---|
| 长度 | *"more descriptive is better. **Aim for at least 2 sentences.** Extremely short prompts can give unexpected results."* —— **下限**，没给上限 |
| 大小写 | *"Follow standard English capitalization rules for **character and series names**."* |
| 顺序 | *"You can mix tags and natural language in **arbitrary order**."* |
| 质量/画师位置 | *"You can put quality / artist tags **at the beginning** of a natural language prompt."* |
| 写法 | *"**Name a character, then describe their basic appearance.**"* —— 多角色时尤其重要 |

官方 `example.png` 里那段真实生成用的提示词，就是这套规范的样板：

```text
masterpiece, best quality, score_7, safe. An anime girl wearing a black tank-top and denim
shorts is standing outdoors. She's holding a rectangular sign out in front of her that reads
"ANIMA". She's looking at the viewer with a smile. The background features some trees and blue
sky with clouds.
```

四个结构特征：质量前缀以**句号**收尾（不是逗号）、然后**四句纯散文**、**一个标签都没有**、现在时第三人称。

### 从证据里能定下来的规则

| 规则 | 依据 |
|---|---|
| **英文**。中文必须翻。 | 作者 #52：*"T5 wasn't trained on Chinese… the model only understands English."* 实测 T5 词表 **0 个 CJK 字符** |
| **标签在前、散文在后** | 官方 example.png；社区三层约定；作者自己 LoRA 用 `caption_prefix` 前置 |
| **标签区用 `", "`（逗号+空格）** | 见下方实测 |
| **散文 2–4 句** | 官方下限 2 句；#140 报告 2–3 段就崩 |
| **散文别重复标签已有的外观/服装** | 社区约定明写；重复等于双重加权 |
| **别在散文里描述背景** | #140：会盖掉 `upper body` / `close-up` |
| **只写画面里有的，不写"没有"** | #141 的 system prompt：*"Describe only what is present. Never list absences."* |
| **不用模糊词** | 同上：*"No hedging such as 'appears', 'seems', 'likely', or 'maybe'."* |
| **多角色给稳定代号** | #141：`woman-1`/`woman-2` 按主导性分配，分配后不换 |
| **不写文学比喻、心理活动、世界观** | 社区约定：*"不写世界观解释、心理活动、纯文学比喻"* |
| **每句必须可执行** | 社区约定：*"删除不改变画面的句子"* |

### 长度预算（我实测的）

用 Anima 自己打包的 T5-XXL 分词器（`comfy/text_encoders/t5_tokenizer`）实测：

| 内容 | 字符 | T5 tokens |
|---|---|---|
| 官方推荐前缀 | 40 | 11 |
| 官方负向 | 112 | **36** |
| **官方 example.png 整段** | 295 | **74** |
| 2 句散文 | 96 | 24 |
| 4 句散文 | 222 | 52 |

**两种文体的换算率不同**，这点影响预算：

| 文体 | 字符/token | 512 token 能放 |
|---|---|---|
| **标签列表** | **3.23** | ≈1650 字符 |
| 自然语言散文 | 4.0–4.3 | ≈2050 字符 ≈ 340 词 |

标签更"贵"，因为短词多、标点多。按散文预算时用 4.0，按标签预算时用 3.23。

512 是**下限不是上限**（`if out.shape[1] < 512: pad`），但 T5-XXL 自己的 `model_max_length` 就是 512，训练数据的 caption 也都是这个形状，所以超过就属于**分布外**。建议按 **≤400 词** 预算。

你现有 53 个 caption（实测）：**90–248 token，平均 164** —— 离上限很远，即使加 2–4 句散文也很宽松（每张加 50 token 也才到 300 左右）。

## 关于 `", "` 的实测修正

[HF #184](https://huggingface.co/circlestone-labs/Anima/discussions/184) 主张必须用逗号+空格，理由是 Qwen 词表里有 `",a"` 这类 token（ID 15012）。我核实过：

- **`",a" → 15012` 确实存在**（Anima 用的 `qwen25_tokenizer/vocab.json`，151643 条），`, a` 这条 merge 规则也在 `merges.txt` 里。
- 但我用同一个词表实测两种写法，**token 数量相同（各 19 个）**，差异在**token 身份**：

| 写法 | 分词结果 |
|---|---|
| `masterpiece, best quality, ...` | `master`,`piece`,`,`,`Ġbest`,`Ġquality`… |
| `masterpiece,best quality,...` | `master`,`piece`,**`,b`**,`est`,`Ġquality`… |

无空格时逗号会和后一个字母粘成 `,b` / `,s` 这种合并 token。

所以 **#184 的结论对（要用 `", "`），但机制说反了**——不是"不加空格会产生不同数量"，而是"不加空格会粘出另一种 token"。它自己也承认这是观察而非受控实验：*"I've observed that certain tags seem to lose some of their effectiveness when spaces are omitted."*

你的数据集**这点已经是对的**：53 个文件里逗号后缺空格的 **0 处**。反倒是那个公开数据集有 **62.6%（499/797）** 犯了这毛病。

## 要不要做「多份 caption 变体」

作者说基础模型用了六种变体。社区实践里 [HF #126](https://huggingface.co/circlestone-labs/Anima/discussions/126) 有人报：

> I've trained multiple loras with: **Tags only** / **Tags 50% + Natlang 50%**

而 #96 里 `Espamholding` 的建议是：

> I haven't tested yet, but I expect that **duplicating the images and captioning each duplicate in different styles** will help

**我的判断：对 53 张的 style LoRA，不值得做。** 理由：作者说 LoRA 格式宽容度高；变体会让每张图重复训练，等于改变有效 epoch；而收益没人给出受控证据。**先把单一格式做对。**

## 一个真实的格式缺陷（公开数据集里发现的）

那个 797 条的数据集还有两个毛病，写在这里是为了提醒你**别照抄它**：

- **62.6% 的 caption 逗号后没空格**（上述分词问题）
- **28.5% 以标点结尾**，其中有不少是这种写法：`…, golden eyes,a black and gold choker, a white off-shoulder top…` —— 逗号后**紧跟冠词**（`a`/`an`/`the`，占 10.9%）。这不是 Danbooru 标签形式，而是自然语言碎片混进了标签区——正是本文要区分的那条界线被踩了。

你的数据集比它干净：**散文 0 处、逗号缺空格 0 处、以标点结尾 0 处**。

## 针对你这个 style LoRA 的建议

你是 **style LoRA**（`@sayori style`），数据同风格。这带来两条 style LoRA 特有的取舍：

### 1. 不要加风格类描述

[HF #96](https://huggingface.co/circlestone-labs/Anima/discussions/96) 里 `Espamholding`：

> For a style lora, you might actually want to **avoid style-modifying captions** if the images are
> all in the same style (other than your made up artist triggerword/s ofc) and just let the model learn.

`Nuke1229` 同帖：*"for quality tags, I don't include them."* —— 你的 53 个 caption 现在**质量/年代/meta 标签全为 0**，这符合上面的建议。

### 2. 你真正缺的是「多人 + 互动」的描述

我统计了你的 53 个 caption。这里要分清两件事：

| 类别 | 数量 |
|---|---|
| 只写 `2girls` / `multiple girls` 这类**多人数标签** | **7** |
| 写 `1girl` + `1boy`（画面里也是两人，但没有多人数标签） | **10** |
| **画面里实际有 2 人以上** | **17** |
| 其中**存在互动**（接触或性行为） | **15** |

> ⚠️ 只看 `2girls` 会漏掉一半。`1girl` + `1boy` 同样表示两个人——你的 `neko4_h10*` 系列 6 张都是这种写法。

这 15 张正是**标签最不够用**的地方——`kiss`、`holding hands`、`hand on another's cheek` 这类标签说不清谁对谁、往哪个方向：

| 文件 | 现有互动标签 |
|---|---|
| `neko4_cs04b_cut` | `kiss`、`hand on another's cheek`、`touching face` |
| `neko4_h02c2d` | `holding hands` + `sex`/`hetero`/`vaginal` |
| `neko4_h02l` | `holding hands` + `after sex`/`cum`/`facial` |
| `neko4_h10c6e` | `hug` + `sex`/`hetero`/`vaginal` |
| 其余 11 张 | 只有 `hetero`/`sex`/`vaginal`/`cum` 等行为标签，**没有任何说明谁对谁、姿势如何的标签** |

按上表分工，它们该补的正是自然语言：

```text
（现有标签）… ,
（补）Place the left girl leaning toward the right one, her hand cupped against the
other's cheek. Keep both faces sharp and turned toward each other.
```

而其余 **36 张单人图保持纯标签就够**——它们的取景（35 处 `looking at viewer`、10 处 `full body`、10 处 `dutch angle`）用标签远比散文可靠。

> 多人但**没有互动**的 2 张（`fanbox_003`、`neko4_cs04a`）也归这类：画面里两人各做各的，多人数标签已经够用，不必加散文。

### 3. 一个只能靠图判断的点

`neko4_cs04b_cut` 标的是 `sensitive`，但有 `kiss` + `hand on another's cheek`。**kiss 在官方判据里归 `sensitive`**（`heavy kissing` 才到 `nsfw`），所以这个标注是对的——但它同时说明：**接触类内容即使不升级档位，也必须描述清楚**，否则模型学不到"两个人在互动"。

## 一句话总结

| 情况 | 用什么 |
|---|---|
| 单人、可穷举、有正典标签 | **纯标签**（你的 38 张属于这类） |
| 多人但有互动（接触/归属/空间/因果） | **标签 + 2–4 句散文**（你的 15 张属于这类） |
| 取景与机位 | **永远用标签**，别指望散文 |
| 风格描述 | **style LoRA 不要写** |

**不确定时的默认：跟着标签走，散文只补标签说不清的那部分。**
