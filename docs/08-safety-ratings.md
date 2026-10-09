# 安全标签的边界

> `safe` / `sensitive` / `nsfw` / `explicit` 四个词的判据、灰色地带怎么定、以及本训练集的现状。

## 先说清楚一件事：官方没有给定义

Anima 模型卡里 `## Safety tags` 一节只有一行：

```text
safe, sensitive, nsfw, explicit
```

**四个词，没有一句解释。** 所以边界不是"照官方文档执行"，而是必须先选一个权威依据。
下面这套判据来自 **Danbooru 官方评级规范**（`howto:rate`），理由见最后一节。

## 四个词对应哪四档

Danbooru 自己的评级字段是 `rating:` 加四个值，与 Anima 的四个词**数量相同、递进顺序相同**：

| Anima 用词 | Danbooru 评级 | 一句话 |
|---|---|---|
| `safe` | `rating:general` | 完全 SFW，没有任何性化 |
| `sensitive` | `rating:sensitive` | 有性暗示但穿着完整 |
| `nsfw` | `rating:questionable` | 裸露，但没有露骨性器 / 性行为 |
| `explicit` | `rating:explicit` | 露骨性器、性行为、体液 |

> `sensitive` 和 `explicit` 两个词在两边**字面相同**；Anima 把 `general` 换成了 `safe`、把
> `questionable` 换成了 `nsfw`。这个对应关系是**推断**（依据：档数相同、顺序相同、两个词同名、
> 且 Anima 的训练数据来自 Danbooru，而 Danbooru 存的就是这四档），不是官方声明。

## 判定流程

**从上往下逐条检查，命中即停**（不要跳着看，第 1 条优先）：

### 1 → `explicit`

- **露出的性器**：`pussy` / `penis` / `anus` — **打码也算**
- **性行为**：`sex` / `fellatio` / `masturbation` / `handjob` / 性玩具插入 — **打码也算**
- **体液**：`cum` / `pussy juice` / 用过的避孕套
- 猎奇、排泄、极端血腥

> 要点：**censored 和 uncensored 同级**。出现 `mosaic censoring` / `censored` 不降低档位。
> 判断依据是"画的内容是什么"，不是"有没有打码"。

### 2 → `nsfw`

- **非性器裸露**：`nipples` / `areolae` / `bare ass` / `completely nude`
- **近裸**：衣服只剩一点、衣服**半脱或被撕开**
- **隔着衣服清楚看见**乳头或下体轮廓：`pronounced cameltoe`、`bulge`、`erection under clothes`、`wedgie`
- **轻度性接触**：`groping`、`grabbing another's breast`、`ear biting`、重度接吻
- 暗示但未画出的性行为、模拟性行为
- **拘束 / BDSM / 打屁股**（没有可见性行为或体液时）
- 性玩具**明显摆着但未使用**
- 单纯的立姿正面全裸（性器隐约可见但未被刻意展示）

> "刻意展示"指 `spread legs`、`presenting`、镜头对准胯下、下体完全清晰可见 —— 这些归 `explicit`。

### 3 → `sensitive`

- **暴露/紧身服装**：`swimsuit` / `bikini` / `lingerie` / `underwear` / `leotard` /
  `skin tight` / `playboy bunnysuit`
- **镂空**：`clothing cutout` / `cleavage cutout` ← 官方**点名**归这一档
- **镜头聚焦性化部位**：胸 / 臀 / 乳沟 / 下乳 / 侧乳 / 腋 / 腹 / 肚脐 / 唇 / 足
- **走光类**：`pantyshot` / `upskirt` / 露出腹股沟线条
- **薄、透、湿的衣物**透出内衣或身体
- **轻微的** cameltoe、或被衣物遮住的乳头轮廓（未明显到 `nsfw` 那种）
- 暗示性的场景（背景里不显眼的性玩具、暗示性手势）
- 轻度血腥

### 4 → `safe`

以上都没有。穿着完整的角色、风景、静物、无伤大雅的亲密（拥抱、牵手、非情欲的亲吻）、
卡通式打闹。

> ⚠️ 官方在这档末尾加了一条硬规则：**涉及儿童样貌角色时，只要有一丝不当或暗示，就不算
> `safe`**（原文：*anything involving child-like characters that is the least bit inappropriate
> or suggestive should not be rated G*）。写 `safe` 前先过一遍这条。

## 灰色地带：这几处最容易打错

| 情况 | 归哪档 | 分界线 |
|---|---|---|
| 穿内衣 | `sensitive` | 只是**穿着** → sensitive |
| 内衣被拉开 / 半脱 | `nsfw` | 有**脱的动作或状态** → nsfw |
| 乳沟 | `sensitive` | 露沟本身不升级 |
| 镂空露胸 | `sensitive` | 官方点名在 sensitive，**不是** nsfw |
| 轻微骆驼趾 | `sensitive` | "轻微" |
| 明显骆驼趾 | `nsfw` | 隔着衣服能看清轮廓 |
| 被布料遮住的乳头 | `sensitive` 或 `nsfw` | 官方两档都提到，看"明显程度"——最典型的模糊点 |
| 裸乳 / 裸臀 | `nsfw` | 非性器裸露就是这档，**不是** explicit |
| 性器 / 性行为 | `explicit` | 打不打码都一样 |
| 接吻 | `sensitive` 或 `safe` | 非情欲的亲吻 → safe；带情欲 / 重度 → nsfw |
| 泳装 | `sensitive` | 官方点名 |
| 性玩具摆着没用 | `nsfw` | 正在用 → explicit |
| 拘束 / BDSM | `nsfw` | 出现可见性行为 → explicit |

## 拿不准时怎么办

官方给了一条兜底规则，**只适用于 `safe` 和 `sensitive` 之间**：

> *Anything you're not sure is safe enough for `rating:general`. When in doubt, use `rating:sensitive`.*

即：**不确定够不够 `safe`，就写 `sensitive`**（不要往上跳档）。

在 `sensitive` / `nsfw` / `explicit` 之间**没有**官方兜底规则，得按上面逐条判。

## 关于「打几个」

- **只打一个。** 四个词互斥，不要同时写两个。
- 它属于官方第一段的最后一类（`[quality/meta/year/safety tags]`）。本工具把这四类
  拆成了四段，所以安全词落在**安全**段——在它前面是质量 / 元信息 / 年代三个段。
- 官方推荐前缀是 `masterpiece, best quality, score_7, safe, `，安全词就在这一段的
  末尾、紧跟 `1girl` 之前。本工具的子序与它一致。
- 加不加质量词由你决定，两种写法工具都接受，段序都不变。
- 模型卡的推荐前缀自带 `safe`；但**训练集不受此限**——训练集要如实反映图的内容。
- 安全词写在**正向**提示词里。模型卡的负向串（`worst quality, low quality, ...`）是压制用的，
  不放安全词。

## 接吻这一档为什么从严

「灰色地带」表里写「非情欲的接吻 → `safe`」，但工具的判据把 `kiss` 归 `sensitive`。
依据是官方那条兜底规则 —— *When in doubt, use `rating:sensitive`* —— 插画里的接吻
常带情欲语境，拿不准就该往上放一档。真正情欲的（`heavy kissing` / `french kiss`）
在 `nsfw`。这是**有意的从严**。

## 为什么以 Danbooru 为准

- Anima 的训练数据来自 Danbooru，Danbooru 存的就是这四档评级；
- Danbooru 的评级规范是**唯一有逐条原文边界**的公开标准（上面每一条判据都能在 `howto:rate` 里找到对应句子）；
- 官方模型卡只列了词，没给判据，所以只能外部借标准。

本文件的判据译自 Danbooru 官方 `howto:rate`，原文可在 `wiki_pages/howto:rate` 查询。
