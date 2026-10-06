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

## 两条从训练器源码里核实出来的硬约束

这两条不是"建议"，违反了会**静默出错**——训练照跑，不报任何警告。我直接读了训练器源码确认。

### 1. caption 必须是**单行**

`Anima-Standalone-Trainer` 的 `library/train_util.py` 第 885 行：

```python
else:
    # if caption is multiline, use the first line
    caption = caption.split("\n")[0]
```

**注释就写着"多行时只用第一行"。** 也就是说 `.txt` 里若有换行，**第二行起的内容在训练时被整段丢弃，没有任何提示**。

还有第二重问题：官方模型卡的「Dataset tags」一节写明，`ye-pop` / `deviantart` 这两个数据集标签的格式就是**首行 + 换行**。所以 caption 里出现换行，除了被截断，还会让模型把首行误读成数据集标签。

> 本程序已经防住了这一条：提示词框里按回车产生的段落标记会被折成空格（`GetCaptionText()`），实际写盘永远是单行。现有 53 个 caption 实测**含内部换行的 0 个**。

### 2. 训练侧的 token 上限是 **512**，超长会**静默截断**

`library/anima_train_utils.py` 第 101-110 行，两个分词器的默认值都是 512：

```python
"--qwen3_max_token_length", type=int, default=512,
"--t5_max_token_length",     type=int, default=512,
```

而这两个值最终传给分词器时是**强制截断**——`library/strategy_anima.py` 第 57-77 行，两条路径完全一样：

```python
qwen3_encoding = self.qwen3_tokenizer(text, return_tensors="pt",
    truncation=True, padding="max_length", max_length=self.qwen3_max_length)
...
t5_encoding = self.t5_tokenizer(text, return_tensors="pt",
    truncation=True, padding="max_length", max_length=self.t5_max_length)
```

**`truncation=True` 意味着超出的部分被直接砍掉，不报错、不警告。** 而且后果有方向性：按本文推荐的顺序，**散文排在最后**——所以一旦超长，被丢掉的正好是散文，标签反而全都留着。你会看到「训练正常跑完」，但模型从没读到那段描述。

这一点要和**推理端**区分开——推理时两个分词器是 `max_length=99999999`（不截断，512 那里只是补齐下限 `if out.shape[1] < 512: pad`）。本文档讲训练集，所以按**硬上限**对待。

### 附带一条：`shuffle_caption` 用不了

`anima_train_network.py` 第 63-66 行有断言，TE 缓存开启时（Anima 标准配方）：

```python
assert (...), "when caching Text Encoder output, shuffle_caption, token_warmup_step
              or caption_tag_dropout_rate cannot be used"
```

这解释了为什么 caption 里**不用靠打乱标签顺序**来增强——标准配方下打乱是关掉的。也意味着标签顺序在训练里是**照读**的。

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
| **标签在前、散文在后**（惯例，非强制） | 官方 example.png 和模型卡范例都是这个形态，且用**句号+空格**过渡（`…safe. An anime girl…`）。但官方同时说 *"mix tags and natural language in **arbitrary order**"*，作者 #9 也说训练数据里 tags→caption 和 caption→tags **两种都有**。所以这是惯例，不是要求 |
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

| 文体 | 字符/token | 512 token 能放 | 留一成余量 |
|---|---|---|---|
| **标签列表** | **3.23** | ≈1650 字符 | **≈1485 字符** |
| 自然语言散文 | 4.0–4.3 | ≈2050 字符 ≈ 340 词 | ≈1845 字符 |

标签更"贵"，因为短词多、标点多。**按标签密度（3.23）预算才是安全的一侧**——同样字符数下它算出的 token 更多。标签区 + 散文混排时用 3.23。

**注意 512 在训练侧是硬上限，不是下限。** 推理端两个分词器都是 `max_length=99999999`（`if out.shape[1] < 512: pad` —— 那里 512 是补齐下限），但**训练器把 512 传给分词器时带 `truncation=True`，超出部分直接砍掉**（`strategy_anima.py:57-77`，两条路径都是）。本文档讲训练集，所以按**上限**对待。

你现有 53 个 caption（实测）：**90–248 token，平均 164**，最长 248 —— 离上限有**一倍以上余量**，即使每张加 2–4 句散文也还在预算内（每张加 50 token 约到 300）。

> 程序里已经加了这条提示：提示词原文超过约 1485 字符时，中栏底部会显示
> `⚠ 约 N tokens，超过训练上限 512，末尾会被静默截断`。实测在 1431 token 的样例上正确触发。

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

> ⚠️ 如果你想试，**不能靠在一个 `.txt` 里写多行来实现**——训练器只读第一行（见上文源码），第二行起会被静默丢掉。作者那六种变体是在**他自己的 `diffusion-pipe` 管线**里做的，不是靠多行 caption。社区提到的做法是**复制图片、每份配一种格式**。

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

## 触发词放哪：一处真实的分歧

你的 `@sayori style` 该放哪？社区里有**两种都有实据**的做法：

| 主张 | 出处 | 理由 |
|---|---|---|
| **放最前** | 多个 LoRA 训练指南；`Nana7mi0721` 的模板全部以 `@trigger` 开头 | 配合 `keep_tokens=N` 形成稳定锚点 |
| **放官方 character 段**（即质量/人数之后） | `Rinne414` 指南 | 官方顺序里 character 就在那个位置，触发词本质上就是"角色/风格"位 |

**我的判断：对你这种全数据集统一的情况，两种都合法，关键是别混用。**

有一条实证让这个选择变得不那么重要：**`shuffle_caption` 在 Anima 标准配方下是关掉的**（源码断言，见上文），所以"置首 + `keep_tokens` 防打乱"这个原始理由**在这条链路上不成立**。而且官方顺序里 `@artist` 本来就排在 general 之前——你的 `@sayori style` 现在紧跟 `solo` 之后，位置是合规的。

**所以：保持现状即可，不必改。** 但要保证 53 张**全都一样**——中途换位置会让模型把"位置"也当成一个变量。

我核对了你的实际数据，**53/53 全部一致**：触发词始终紧跟人数段，前面只出现「安全词 + 人数词」，没有例外。

```text
sensitive, 1girl, @sayori style                     ← 位置 2
sensitive, 1girl, solo, @sayori style               ← 位置 3（人数段多一个 solo）
explicit, 1girl, 1boy, solo focus, @sayori style    ← 位置 4（两个人数词）
```

那个"位置 2/3/4"的差别**不是不一致**，而是人数段本身长度不同——这正符合官方顺序（`[…] [1girl/1boy…] [character] [series] [artist] [general]`）。

## 一句话总结

| 情况 | 用什么 |
|---|---|
| 单人、可穷举、有正典标签 | **纯标签**（你的 38 张属于这类） |
| 多人但有互动（接触/归属/空间/因果） | **标签 + 2–4 句散文**（你的 15 张属于这类） |
| 取景与机位 | **永远用标签**，别指望散文 |
| 风格描述 | **style LoRA 不要写** |

**不确定时的默认：跟着标签走，散文只补标签说不清的那部分。**
