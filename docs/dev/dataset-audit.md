# 本训练集的审计结果

> 针对 `sayori-style-v1`（53 张）逐条核对出来的结论。**这是审计报告，不是软件文档**——
> 它讲的是"这一份数据现在什么样、哪里建议改"，所以不随程序分发、也不进帮助窗口。
>
> 判据本身（安全档位怎么分、颜色标签怎么拼）在用户文档
> [安全标签的边界](../08-safety-ratings.md)、
> [配件的颜色与重复](../10-accessory-color-and-repeat.md) 里，
> 这里只放**对账结果**。

## 安全档位现状

对 53 张的实际统计：

| 档位 | 数量 |
|---|---|
| `safe` | **0** |
| `sensitive` | 18 |
| `nsfw` | 9 |
| `explicit` | 25 |
| 缺失 | 1（`ingame_05`） |

三件需要处理的事：

1. **`safe` 一档是空的。** 53 张里没有任何一张标 `safe`，所以模型没有"安全样貌"的样例。
   对风格 LoRA 未必是问题，但如果以后要生成 SFW 内容，这个词是没训过的。

2. **`ingame_05` 完全缺安全标签。** 内容为面包房场景、端着托盘、穿着完整，按判据流程
   应打 `safe`。这是唯一一处空档，也正好是补上 `safe` 样例的位置。

3. **现有标注比 Danbooru 标准整体保守一档。** 按判据逐条核对，53 张里 43 张一致、
   9 张分歧，且分歧**方向一致**——都是标得比 Danbooru 更严：

   | 文件 | 现标 | 按 Danbooru | 判据 |
   |---|---|---|---|
   | `090_04_Azuki_3_white` | nsfw | sensitive | `cleavage cutout` / `clothing cutout` |
   | `112_05_Mapple_6_white` | nsfw | sensitive | `cleavage cutout` |
   | `143_06_Cinnamon_17_white` | nsfw | sensitive | `cleavage` + `cleavage cutout` |
   | `4_shigure` | nsfw | sensitive | `cleavage` + `clothing cutout` |
   | `coconut` | nsfw | sensitive | `cleavage` / `midriff` / `navel` / `micro shorts` |
   | `022_01_Chocola_20_white` | sensitive | safe | 无任何性化标签 |
   | `ingame_10` | sensitive | safe | 无任何性化标签 |
   | `1_chocola` | explicit | nsfw | `clothes removed`（无器裸露、无性行为） |
   | `6_shigure` | explicit | nsfw | `bare ass` + `panty pull`（非器裸露） |

   **这不是错误，是两套标准的选择问题。** 关键是**内部一致**：
   如果一部分图按 Danbooru 判、一部分按更严的判，安全词在训练里就变成噪声，
   模型学不到清晰边界。要么把上表 9 张改成 Danbooru 档位，要么沿用现有更严的惯例
   并把分界线写下来（例如"镂空也算 nsfw"），但不能两者混用。

   > 这 9 处可以用 `corecheck safety` 随时复核：它只**报告**分歧，不会自动改写任何文件。
   > 改动词表里的任何一条都会让这个数字变化，所以它也是判据的回归测试。
   >
   > 顺带说明一处判据与实现的差异：判据表里写「非情欲的接吻 → `safe`」，而程序里
   > `kiss` 归 `sensitive`。这是按官方兜底规则 *When in doubt, use `rating:sensitive`*
   > 做的**有意从严**，不是笔误。

## 拆分四段对现有文件的影响

拆段是否重排已有标注，实测过两件事：

- 53 个 caption 里除安全词外**没有任何控制词**（质量 / 元信息 / 年代都是 **0** 个），
  安全词 53/53 全在 index 0；
- 所以安全词在旧方案（合并段）和新方案（独立段）里都排第一位，拆段对现有文件
  **逐字节恒等**——`sections` 回归的「53/53 恒等」仍然成立。

## 标签 vs 散文：本数据集该补哪里

### 真正缺的是「多人 + 互动」的描述

统计 53 个 caption，这里要分清两件事：

| 类别 | 数量 |
|---|---|
| 只写 `2girls` / `multiple girls` 这类**多人数标签** | **7** |
| 写 `1girl` + `1boy`（画面里也是两人，但没有多人数标签） | **10** |
| **画面里实际有 2 人以上** | **17** |
| 其中**存在互动**（接触或性行为） | **15** |

> ⚠️ 只看 `2girls` 会漏掉一半。`1girl` + `1boy` 同样表示两个人——`neko4_h10*`
> 系列 6 张都是这种写法。

这 15 张正是**标签最不够用**的地方——`kiss`、`holding hands`、`hand on another's cheek`
这类标签说不清谁对谁、往哪个方向：

| 文件 | 现有互动标签 |
|---|---|
| `neko4_cs04b_cut` | `kiss`、`hand on another's cheek`、`touching face` |
| `neko4_h02c2d` | `holding hands` + `sex`/`hetero`/`vaginal` |
| `neko4_h02l` | `holding hands` + `after sex`/`cum`/`facial` |
| `neko4_h10c6e` | `hug` + `sex`/`hetero`/`vaginal` |
| 其余 11 张 | 只有 `hetero`/`sex`/`vaginal`/`cum` 等行为标签，**没有任何说明谁对谁、姿势如何的标签** |

按分工，它们该补的正是自然语言：

```text
（现有标签）… ,
（补）Place the left girl leaning toward the right one, her hand cupped against the
other's cheek. Keep both faces sharp and turned toward each other.
```

而其余 **36 张单人图保持纯标签就够**——它们的取景（35 处 `looking at viewer`、
10 处 `full body`、10 处 `dutch angle`）用标签远比散文可靠。

> 多人但**没有互动**的 2 张（`fanbox_003`、`neko4_cs04a`）也归这类：画面里两人各做各的，
> 多人数标签已经够用，不必加散文。

### 一处只能靠图判断的点

`neko4_cs04b_cut` 标的是 `sensitive`，但有 `kiss` + `hand on another's cheek`。
`kiss` 在判据里归 `sensitive`，所以这个标注是对的——但它同时说明：
**接触类内容即使不升级档位，也必须描述清楚**，否则模型学不到"两个人在互动"。

### style LoRA 特有的两条取舍

1. **不要加风格类描述。** [HF #96](https://huggingface.co/circlestone-labs/Anima/discussions/96)
   里 `Espamholding`：*"For a style lora, you might actually want to avoid style-modifying
   captions if the images are all in the same style."* `Nuke1229` 同帖：*"for quality tags,
   I don't include them."* —— 53 个 caption 现在**质量/年代/meta 标签全为 0**，符合这条建议。

2. **触发词位置保持现状即可。** 两种做法（放最前 / 放官方 character 段）都有实据，
   关键是别混用。核对了实际数据，**53/53 全部一致**：触发词始终紧跟人数段，
   前面只出现「安全词 + 人数词」，没有例外。

   ```text
   sensitive, 1girl, @sayori style                     ← 位置 2
   sensitive, 1girl, solo, @sayori style               ← 位置 3（人数段多一个 solo）
   explicit, 1girl, 1boy, solo focus, @sayori style    ← 位置 4（两个人数词）
   ```

   那个"位置 2/3/4"的差别**不是不一致**，而是人数段本身长度不同——这正符合官方顺序
   （`[…] [1girl/1boy…] [character] [series] [artist] [general]`）。

### 多份 caption 变体：对 53 张不值得做

作者的基础模型用了六种变体，但那是他自己的 `diffusion-pipe` 管线。对 53 张的 style LoRA：
作者说 LoRA 格式宽容度高；变体会让每张图重复训练，等于改变有效 epoch；而收益没人给出
受控证据。**先把单一格式做对。**

## 长度与分隔符的实测数字

- 53 个 caption：**90–248 token，平均 164**，最长 248 —— 离 512 上限有**一倍以上余量**，
  即使每张加 2–4 句散文也还在预算内（每张加 50 token 约到 300）。
- **含内部换行的 0 个**（这一点若出错会被训练器静默丢掉第二行起的内容）。
- **逗号后缺空格的 0 处**，**以标点结尾的 0 处**，**散文 0 处**。

## 与公开数据集的对照

公开数据集（[kongbai-84/soultide_lora_Anima](https://huggingface.co/kongbai-84/soultide_lora_Anima)，
HF #220 里作者自己分享的 90+ 角色 LoRA）797 条 caption 的实测特征表在用户文档
[标签还是自然语言](../09-tags-vs-prose.md) 里——那张表留在那边，因为它支撑
"真实数据集几乎都是纯标签"这条分工依据。

这里只记**它的毛病**（用来对照本数据集）：

- **62.6%（499/797）的 caption 逗号后没空格**（会粘出 `,b` / `,s` 这类合并 token）
- **28.5% 以标点结尾**，其中不少是 `…, golden eyes,a black and gold choker, a white
  off-shoulder top…` 这种写法——逗号后**紧跟冠词**（`a`/`an`/`the`，占 10.9%）。
  这不是 Danbooru 标签形式，而是自然语言碎片混进了标签区。

本训练集比它干净：散文 0 处、逗号缺空格 0 处、以标点结尾 0 处。

## 配件标签对账（蝴蝶结 / 花边类）

以 Anima 索引（108,257 条）逐条核对。蝴蝶结 / 花边类标签共 **139 个实例**：

- **117 个实例（44 个不同标签）索引里存在** → 不用动
- **22 个实例（10 个不同标签）索引里不存在** → 建议改写

| 现在写的 | 次数 | 建议 |
|---|---|---|
| `white back bow` | 6 | `back bow` + `white bow` |
| `white hair bow` | 6 | `hair bow` + `white bow` |
| `blue hair bow` | 3 | `hair bow` + `blue bow` |
| `blue back bow` | 1 | `back bow` + `blue bow` |
| `pink hair bow` | 1 | `hair bow` + `pink bow` |
| `pink tail bow` | 1 | `tail bow` + `pink bow` |
| `light blue hair bow` | 1 | `hair bow` + `blue bow` |
| `light blue bow` | 1 | `blue bow`（`light blue` 不是标签）|
| `polka dot hair bow` | 1 | `polka dot bow` |
| `magenta bowtie` | 1 | `magenta` 不在索引里 → `purple bowtie` / `pink bowtie`，或写散文 |

两个**边界情况**——索引里存在，但引用数低到基本没训过，建议也按上面拆开：

| 标签 | post_count |
|---|---|
| `red hair bow` | 71 |
| `white hair ribbon` | 55 |

同一类型的问题还有两组，**不在上面这个 139 的统计里**（它们不含 bow/ribbon/frill），
但都属于「颜色挂在一个没有颜色的家族上」：

| 现在写的 | 次数 | 状态 | 说明 |
|---|---|---|---|
| `gold bell` | 12 | Danbooru 有 **222** 篇，**Anima 索引里没有** | `bell` 133,045 · `neck bell` 53,511 · `jingle bell` 44,712 在 Anima 索引里**都没有颜色变体**；`gold bell` 是唯一例外 |
| `silver bell` | 6 | **完全不存在** | 同上。顺带一提 `@silver bell` 是画师名（184 篇），不是这个东西 |
| {颜色} name tag | 9（8 个不同）| 全部不存在 | `name tag` 20,864 · `badge` 11,567，**都没有颜色变体** |

> 一处要留意：`gold bell` 会被程序的「校验」判成**正常** —— 因为它在 Danbooru 表里
> 确实有 222 篇。但 222 篇和 `red hair bow` 的 71 篇是同一量级，等于没训过。
> **「校验通过」不等于「这个词有用」。**

### 「多实例」的文件

同一个词族出现 2 次以上、而且属性不一样的文件。括号里是那一族实际写成了什么：

| 文件 | 实际情况 | 处理 |
|---|---|---|
| `azuki.txt` | bow ×3：`yellow bow` + `white hair bow` + `white back bow` | 2 位置 × 2 颜色 = 4 种读法 → `hair bow` + `back bow` + `yellow bow` + `white bow`，**配对不写** |
| `ingame_08.txt` | ribbon ×3：`white ribbon` + `neck ribbon` + `red ribbon` | 位置相同、两种颜色 → 三条都留，配对不写 |
| `022_01_Chocola_20_white.txt` | bow ×4：`hair bow` + `black bow` + `polka dot hair bow` + `red hair bow` | 位置同、颜色两种、样式一种 → `hair bow` + `black bow` + `red bow` + `polka dot bow` |
| `5_vanilla.txt` | bowtie/bow ×4 + belt ×3：`red belt` + `blue belt` + `gold belt buckle` | 腰带是两条不同颜色 → `red belt` + `blue belt` + `belt buckle` + `gold belt`（1,023）|
| `fanbox_002.txt` | `blue hair bow` + `bow` + `ribbon` + `hair ribbon`，**以及 `blue thighhighs` + `purple thighhighs`** | 两腿不同色 → **用成对机制**：加 `asymmetrical legwear`（42,979），比散文可靠 |
| `1_vannila.txt` | `light blue bow` + `white bow` + `hair ribbon` + `white ribbon` | 只改 `light blue bow` → `blue bow` |
| `052_02_Vanilla_19_white.txt` | `blue hair bow` + `blue bow` + `blue bowtie` + `striped bow` | 颜色其实只有蓝一种 → 拆位置标签即可 |
| `3_vanilla.txt` | `light blue hair bow` + `blue bowtie` + `tail bow` + `tail ornament` | 只改 `light blue hair bow` → `hair bow` + `blue bow` |
| `neko4_h03o.txt` | tail ×5：`striped cat tail` + `tail` + `cat tail` + `striped tail` + `fluffy tail` | **不是配对问题**：同一根尾巴写了五遍，过度标注 → `cat tail` + `striped tail` 就够 |
| `1_chocola.txt` | bow ×6：`hair bow` + `white bow` + `pink bow` + `pink bowtie` + `bow panties` + `bow bra` | **全部存在，一个字都不用改** |
| `neko4_h02l.txt` | bow ×4：`bow` + `hair bow` + `blue bow` + `pink bow` | **全部存在**；只有裸词 `bow` 是父词，可留可删 |

两句能直接对照的：**`fanbox_002.txt` 的腿袜有现成的成对机制**（`asymmetrical legwear`），
**`azuki.txt` 的蝴蝶结没有** —— 所以前者不必写散文，后者只能接受配对缺失。

顺带这一轮又查出几个不存在的写法（都不含 bow/ribbon/frill，所以不在这 139 的统计里）：

| 现在写的 | 次数 | 建议 |
|---|---|---|
| `striped cat tail` | 3 | `cat tail` + `striped tail`（4,430）|
| `orange fox tail` | 1 | `fox tail` + `orange tail`（945）|
| `thin tail` | 1 | `thin` 不是标签 → `tail`，或写进散文 |
| `white fishnet thighhighs` | 1 | `fishnet thighhighs` + `white thighhighs` |
| `gold belt buckle` | 1 | `belt buckle` + `gold belt`（1,023）|
