# 配件的颜色与重复：蝴蝶结这类元素怎么写

> 颜色为什么不能单独写、同一个配件出现好几次怎么区分、样式能不能和颜色拼成一个标签、
> 位置／颜色／样式三者全不同时该怎么办。

## 一句话规则

**颜色永远紧贴它修饰的名词、合成一个整体**，不能拆成两个标签。
**重复先靠位置词分开，位置相同再靠数量词，数量词也说不清就写散文。**
**标签给的是一个集合，给不了一组配对** —— 三者全不同时，默认不要去写那个配对。
**左右和「第几个」永远无法用标签表达。**

---

# 一、颜色

## 1. 裸颜色词在词库里不存在

`black` `white` `red` `blue` `green` `yellow` `orange` `purple` `pink`
`brown` `grey` `magenta` —— 在 Anima 索引（108,257 条）里**全部查不到**。

唯一例外是 `gold`（167 篇）。

所以 `black, bow` 这种写法是无效的，两个词都不是标签。必须写成 `black bow`。

## 2. `{颜色} {名词}` 存在就直接用

通用名词的颜色是最全的：

| 名词 | 裸词 | 可用的颜色变体 |
|---|---|---|
| `bow` | 1,288,738 | `red bow` 224,208 · `black bow` 158,398 · `blue bow` 128,029 · `white bow` 99,721 · `pink bow` 86,964 · `yellow bow` 51,773 · `purple bow` 42,885 · `green bow` 42,325 · `orange bow` 20,973 · `brown bow` 8,820 · `grey bow` 4,882 · `gold bow` 167 |
| `ribbon` | 1,204,331 | 同一套颜色，量级略小：`red ribbon` 194,744 · `black ribbon` 144,368 · `blue ribbon` 81,827 · `white ribbon` 64,730 · `pink ribbon` 50,226 · `yellow ribbon` 39,150 · `purple ribbon` 27,038 · `green ribbon` 25,757 · `orange ribbon` 6,206 · `brown ribbon` 4,386 · `grey ribbon` 2,475 · `gold ribbon` 273 |
| `bowtie` | 340,448 | `red bowtie` 68,405 · `black bowtie` 42,974 · `blue bowtie` 35,521 · `pink bowtie` 16,256 · `purple bowtie` 11,230 · `white bowtie` 11,122 · `yellow bowtie` 10,437 · `green bowtie` 10,097 · `orange bowtie` 3,641 · `grey bowtie` 1,369 · `gold bowtie` 164 |
| `hairband` | 520,272 | 颜色齐全：`black hairband` 96,192 · `white hairband` 39,667 · `red hairband` 28,489 · `blue hairband` 20,832 · `yellow hairband` 11,745 · `pink hairband` 10,379 · `purple hairband` 7,246 · `green hairband` 5,233 · `brown hairband` 3,547 · `gold hairband` 3,244 · `grey hairband` 2,009 · `silver hairband` 421 |
| `flower` | 714,115 | `white flower` 97,442 · `red flower` 75,573 · `pink flower` 60,875 · `blue flower` 44,983 · `yellow flower` 34,611 · `purple flower` 34,324 |

## 3. 名词本身没颜色时，把颜色挂到**有颜色的类别词**上

这是最容易踩的一处。实测结果很干脆：

> **位置词 + bow/ribbon 几乎没有颜色变体。**

以 `hair bow` / `hair ribbon` / `neck ribbon` 结尾的标签，整个索引里只有 5 个，
而且全是「状态」而不是颜色：

| 标签 | post_count | 性质 |
|---|---|---|
| `undone neck ribbon` | 990 | 状态 |
| `loose neck ribbon` | 485 | 状态 |
| `no hair bow` | 108 | 缺失 |
| `red hair bow` | 71 | 颜色（几乎没训过） |
| `torn hair ribbon` | 59 | 状态 |
| `unworn hair bow` | 55 | 未佩戴 |
| `white hair ribbon` | 55 | 颜色（几乎没训过） |

所以下面这些**全都不存在**：

| 不存在的写法 | 训练集里用了 | 正确写法 |
|---|---|---|
| `white hair bow` | 6 次 | `hair bow` + `white bow` |
| `blue hair bow` | 3 次 | `hair bow` + `blue bow` |
| `white back bow` | 6 次 | `back bow` + `white bow` |
| `blue back bow` | 1 次 | `back bow` + `blue bow` |
| `pink hair bow` | 1 次 | `hair bow` + `pink bow` |
| `pink tail bow` | 1 次 | `tail bow` + `pink bow` |
| `light blue hair bow` | 1 次 | `hair bow` + `blue bow` |
| `light blue bow` | 1 次 | `blue bow` |
| `polka dot hair bow` | 1 次 | `polka dot bow` |

规律是：**位置标签负责"长在哪"，通用颜色标签负责"什么颜色"**，两个标签各管一件事。

## 4. 整个家族一个颜色都没有 → 颜色写进散文

有些词族完全没有颜色变体，这时**不要自己造标签**：

| 词族 | 裸词 | 颜色变体 |
|---|---|---|
| `bell` / `neck bell` / `jingle bell` | 有 | **0 个**（唯一例外见第五节：Danbooru 侧有 `gold bell`，222 篇，Anima 索引里没有）|
| `name tag` | 20,864 | **0 个** |
| `badge` | 11,567 | **0 个** |
| `striped` / `plaid` / `polka dot` / `vertical stripes` | 有 | **0 个** |
| `back bow` / `neck ribbon` / `waist bow` / `hat bow` / `ear bow` | 有 | **0 个** |

明暗词（`light` / `dark`）的适用范围也很窄。`light blue hair`（29,871）成立，
`light blue dress` 只有 257、`light brown skirt` 只有 90 —— 而 `light blue bow` **不存在**。
蝴蝶结上没有明暗前缀。

大小同理：`big bow` / `small bow` / `wide ribbon` / `thin ribbon` **都不存在**，
只有 `large bow`（7,518）、`huge bow`（1,646）、`long ribbon`（1,328）。

---

# 二、重复出现：三层，从位置到散文

## 第一层：位置词（这是主要手段）

位置词一上，两个蝴蝶结天然就分开了，不需要任何额外说明。

| 位置 | bow | ribbon |
|---|---|---|
| 头发 | `hair bow` 561,434 | `hair ribbon` 663,813 |
| 脖子 | — | `neck ribbon` 141,181 |
| 帽子 | `hat bow` 54,724 | `hat ribbon` 62,206 |
| 背后 | `back bow` 26,801 | `back ribbon` 608 |
| 尾巴 | `tail bow` 9,938 | `tail ribbon` 7,384 |
| 耳朵 | `ear bow` 9,715 | `ear ribbon` 5,393 |
| 腰 | `waist bow` 8,780 | `waist ribbon` 1,207 |
| 手腕 | `wrist bow` 2,641 | `wrist ribbon` 6,657 |
| 腿 | — | `leg ribbon` 14,625 |
| 手臂 | `arm bow` 195 | `arm ribbon` 10,331 |
| 大腿 | `thigh bow` 625 | `thigh ribbon` 3,304 |
| 脚踝 | `ankle bow` 1,084 | `ankle ribbon` 4,956 |
| 袖子 | `sleeve bow` 3,856 | `sleeve ribbon` 2,094 |
| 胸口 | `chest bow` 1,356 | — |

「挂在某个物件上」这一组也很全：

`dress bow` 11,707 · `bow hairband` 12,620 · `footwear bow` 8,857 · `bow legwear` 3,743 ·
`bow choker` 3,434 · `bow skirt` 1,713 · `bow apron` 187 · `glove bow` 962 ·
`shirt bow` 807 · `belt bow` 215 · `obi bow` 154 · `scarf bow` 306 ·
`bow bra` 13,935 · `bow panties` 45,367 · `bow bikini` 4,362 · `bow swimsuit` 202 ·
`bow bloomers` 147 · `bow gloves` 329 · `bow button` 231 · `bow-shaped hair` 7,253

**不存在的位置词**：`neck bow` · `chest ribbon` · `skirt bow` · `apron bow` · `front bow` 之外的前后向词

所以「头饰一个蝴蝶结 + 脖子上一个」= `hair bow` + `neck ribbon`，两个标签，位置各自清楚。
「背后一个大蝴蝶结」= `back bow`（+ `large bow` 如果要说明大小）。

## 第二层：数量词（位置相同、确实有好几个）

| 标签 | post_count | 备注 |
|---|---|---|
| `multiple hair bows` | 9,545 | 头发上不止一个 |
| `multiple bows` | 1,518 | **Anima 原生词表（cat6）** |
| `hair ribbons` | 252 | 复数形式 |
| `multiple hat bows` | 192 | 帽子上不止一个 |

同一套 `multiple *` 在其他配件上也齐全：`multiple rings` 13,181 ·
`multiple belts` 8,448 · `multiple earrings` 6,649 · `multiple piercings` 2,640 ·
`multiple bracelets` 2,837 · `multiple hairpins` 2,251。
身体部位另有 `extra *` 家族：`extra ears` 53,789 · `extra tails` 328 · `extra arms` 13,025。

注意 **`multiple ribbons` 不存在** —— 复数只做了 bow 那几条。

## 第三层：散文（标签说不清的部分）

标签是**二值的**：只有「有没有」，没有「第几个」。所以下面两种只能写散文：

1. 同一位置有两个颜色不同、且要指明各自在哪一侧
2. 要说明「哪一个配哪一个」（发饰那个和衣服上那个是一套）

原因：`left bow` / `right bow` / `asymmetrical bow` / `asymmetrical ribbon` /
`second bow` / `two bows` / `extra bow` **全部不存在**。
不对称词表是**封闭列表**，只覆盖这些：

| `asymmetrical *` | post_count | | `mismatched *` | post_count |
|---|---|---|---|---|
| `asymmetrical legwear` | 42,979 | | `mismatched legwear` | 16,912 |
| `asymmetrical hair` | 36,571 | | `mismatched pupils` | 9,837 |
| `asymmetrical bangs` | 32,994 | | `mismatched gloves` | 9,144 |
| `asymmetrical gloves` | 17,025 | | `mismatched footwear` | 4,408 |
| `asymmetrical clothes` | 13,438 | | `mismatched bikini` | 4,227 |
| `asymmetrical sleeves` | 9,286 | | `mismatched sleeves` | 2,292 |
| `asymmetrical footwear` | 6,446 | | `mismatched earrings` | 1,316 |
| `asymmetrical horns` | 5,457 | | `mismatched wings` | 749 |

**两个家族里都没有 bow / ribbon。**

## 三者同时都不同：位置、颜色、样式各不一样

例如「白色蕾丝蝴蝶结在头上、粉色荷叶边蝴蝶结在颈上」。这时先要认清一件事：

> **标签能表达的是一个集合，不是一组配对。**

实测（合并表 331,836 条 + Anima 索引 108,257 条）：

| 问题 | 实测结果 |
|---|---|
| 有没有标签同时带位置和颜色？ | 全表 **1 条**：`white hair bow`（34 帖，**不在** Anima 索引里）。等于没有 |
| 有没有标签同时带两种颜色？ | 9 条，全是作品名／标题／画师命名这类专名（`pokemon black and white`、`black vs white`…），没有一条是给物件配色的 |
| 有没有「第几个」？ | `two bows` / `second bow` 这类 **0 条** |
| 「好几个」怎么表达？ | `multiple hair bows` 9,545 · `multiple bows` 1,518 · `multiple belts` 8,448 · `multiple earrings` 6,649 · `multiple hairpins` 2,251 …… 但**没有 `multiple ribbons`** |
| 「只有一件」怎么表达？ | `single *` 共 137 条：`single thighhigh` 64,582 · `single earring` 49,987 · `single glove` 45,703 · `single sock` 10,623 …… 但**没有 `single bow` / `single ribbon`** |
| 左右？ | `left` / `right` 加 bow / ribbon **全 0** |
| 「不对称」？ | 83 条里**没有** bow / ribbon 成员 |

所以上面那个例子，标签能给的是：

```
hair bow + neck ribbon + white bow + pink bow + lace-trimmed bow + frilled ribbon
```

**集合是全的，但「白色那个在头上」丢了** —— 2 个位置 × 2 个颜色 = 4 种读法，标签收不窄。

### 默认：配对这件事不写

判断依据仍是 caption paradox —— **你标的是推理时想控制的量**。
style LoRA 里「哪个蝴蝶结挂哪儿」几乎不是你会在推理时指定的东西，而散文不是免费的：
一旦散文开始描写背景，`upper body` / `close-up` 这类取景标签会被无视（HF #140 的对照实验），
超过 2–3 段还会先崩手。

**所以默认做法是：每个属性各写它真实的标签，到此为止。**

### 确实要配对时：一句话，每件一个小句

- **只写一句**，不要让它长成一段。
- **框景、背景、光照全部留在标签里**，不写进这句散文。
- 形容词在名词前，和标签同序：`a white lace bow in her hair and a pink frilled one at her throat`。
- **别写左右** —— 见第四节：翻转增强开着的话，写了也是错的。

上限要有预期：**这是唯一能表达配对的通道，但没有可靠证据说它一定生效。**
最接近的对照实验是 lilting.ch 的左右控制 —— 他们把方向信息全交给散文之后，
**方向控制依然没实现**（跨 epoch / seed 仍然随机）。他们自陈那次只跑了 636 步
（官方建议 12,000+）且学习率偏高，所以既不能说散文无效，也不能说它有效。
按「尽力而为、推理时验证」对待，别按「控制」对待。

### 更值得先试的两条替代路

1. **降属性**：只留信息量最大的那一个（通常是位置），颜色交给散文或干脆不写。
   一件饰品一次占三个标签，对 style LoRA 是负担。
2. **换成现成的成对机制**：如果是「一对东西的两边」（左右腿、两只手），
   别用散文 —— 用专门词族 `asymmetrical legwear` 42,979 · `mismatched gloves` 9,144 ·
   `asymmetrical sleeves` 9,286 · `mismatched footwear` 4,408，再加两个颜色标签。
   标签是现成的，比散文可靠。**蝴蝶结没有这个机制**（两个词族里都没有 bow / ribbon）。

---

# 三、样式和颜色不能拼成一个标签

实测：整个索引里 `{样式} {颜色} bow` 与 `{颜色} {样式} bow` **一个都不存在**
（`frilled white bow`、`white frilled bow`、`large white bow`、`white large bow` 全部 ABSENT）。

所以「白色蕾丝蝴蝶结」= `lace-trimmed bow` + `white bow`，两个标签各管一件事。

可用的样式词：

| 类别 | 标签（实测存在的） |
|---|---|
| 花边装饰 | `frilled bow` 15,354 · `lace-trimmed bow` 376 · `ribbon-trimmed bow` 397 · `frilled ribbon` 3,885 |
| 花纹 | `striped bow` 32,176 · `polka dot bow` 11,068 · `plaid bow` 9,089 · `diagonal-striped bow` 5,074 · `print bow` 2,329 · `checkered bow` 1,356 · `argyle bow` 236 · `vertical-striped bow` 204 · `pinstripe bow` 156 |
| 多色 | `two-tone bow` 2,968 · `multicolored bow` 2,186 · `gradient bow` 163 |
| 大小 | `large bow` 7,518 · `huge bow` 1,646 |
| 状态 | `torn bow` 247 · `untied ribbon` 158 · `undone bowtie` 410 · `loose bowtie` 2,779 |

---

# 四、一条训练侧的限制：翻转增强会毁掉左右

CivitAI 上的 Anima LoRA 训练指南（文章 31678）明确写着：

> 如果训练的角色有重要的不对称特征——单片眼罩、只在单侧出现的发型或印记——
> 不要开启它；数据集里有大量可读文字或 logo 时也一样，那些也会被镜像。

机制是：翻转增强把每张图水平镜像一份，**而 caption 不变**。所以只要写了左右，
镜像出来的那一半就是错的，模型学到的是「左右都会出现这个」。

**实际含义**：即使要写散文，也别写「左边是白色、右边是粉色」，
改成不指定侧面的写法（「两侧各有一个蝴蝶结，一个白色一个粉色」）。
先确认你的训练器有没有开这个开关。

---

# 五、本训练集的对账（逐个实测）

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
| `light blue bow` | 1 | `blue bow`（`light blue` 不是标签） |
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
| {颜色} name tag | 9（8 个不同） | 全部不存在 | `name tag` 20,864 · `badge` 11,567，**都没有颜色变体** |

> 一处要留意：`gold bell` 会被程序的「校验」判成**正常** —— 因为它在 Danbooru 表里
> 确实有 222 篇。但 222 篇和 `red hair bow` 的 71 篇是同一量级，等于没训过。
> **「校验通过」不等于「这个词有用」。**

这三组的处理方式和蝴蝶结一样：**颜色没有标签可挂** → 写进散文，不要造标签。

## 本训练集里「多实例」的文件

同一个词族出现 2 次以上、而且属性不一样的文件。括号里是那一族实际写成了什么：

| 文件 | 实际情况 | 处理 |
|---|---|---|
| `azuki.txt` | bow ×3：`yellow bow` + `white hair bow` + `white back bow` | 2 位置 × 2 颜色 = 4 种读法 → `hair bow` + `back bow` + `yellow bow` + `white bow`，**配对不写** |
| `ingame_08.txt` | ribbon ×3：`white ribbon` + `neck ribbon` + `red ribbon` | 位置相同、两种颜色 → 三条都留，配对不写 |
| `022_01_Chocola_20_white.txt` | bow ×4：`hair bow` + `black bow` + `polka dot hair bow` + `red hair bow` | 位置同、颜色两种、样式一种 → `hair bow` + `black bow` + `red bow` + `polka dot bow` |
| `5_vanilla.txt` | bowtie/bow ×4 + belt ×3：`red belt` + `blue belt` + `gold belt buckle` | 腰带是两条不同颜色 → `red belt` + `blue belt` + `belt buckle` + `gold belt`（1,023） |
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
| `striped cat tail` | 3 | `cat tail` + `striped tail`（4,430） |
| `orange fox tail` | 1 | `fox tail` + `orange tail`（945） |
| `thin tail` | 1 | `thin` 不是标签 → `tail`，或写进散文 |
| `white fishnet thighhighs` | 1 | `fishnet thighhighs` + `white thighhighs` |
| `gold belt buckle` | 1 | `belt buckle` + `gold belt`（1,023） |

---

# 六、写之前问自己三个问题

1. **这个整体存在吗？** 把「颜色 + 名词」当成一个词去查（`white bow` ✅、`white hair bow` ❌）。
2. **不存在的话，颜色该挂到哪个名词上？** 找一个同类别、有颜色的词
   （`back bow` 没颜色 → 颜色挂 `bow`；`hair bow` 没颜色 → 颜色挂 `bow`）。
3. **有两件以上吗？** 位置不同 → 各写各的位置标签；位置相同 → `multiple hair bows`；
   要分清哪一件是哪一件 → **写散文，不要造标签**（但先读二之末节：默认不写，位置词往往已经够了）。

## 三条不要做

- 不要自己拼 `{颜色} {位置} {名词}` —— 索引里没有，模型没学过，颜色可能丢、也可能漂到别的物件上。
- 不要用 `left` / `right` 表达位置 —— 没有这种标签；翻转增强开着的话，写进散文也是错的。
- 不要为了「保险」把父词和子词都写上（`hair bow` + `hair ornament`）—— style LoRA 的社区共识是有具体子标签时删掉通用父标签，过度打标会稀释概念。
  唯一的例外是颜色：颜色只能挂在通用词上，所以 `hair bow` + `white bow` 是必需的，不是冗余。
