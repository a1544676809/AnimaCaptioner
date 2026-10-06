# AnimaCaptioner

Anima 训练集标注工具。左栏图片列表，中栏按官方大类分段的 tag，右栏上面是写入
`.txt` 的**提示词原文**（标签 + 英文自然语言，按大类着色）、下面是**中文描述 →
英文自然语言**的翻译器。全部离线，词库和翻译都在本机。

> **许可分层**：软件本体 **GPL-3.0**，自带文档 **CC BY-NC-SA 4.0**，
> 标签数据**不随本仓库分发**、遵循各自上游协议。详见 [许可](#许可)。

```powershell
# 构建
cd E:\source\repos\AnimaCaptioner
dotnet build -c Release

# 运行
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\AnimaCaptioner.exe

# 直接打开设置对话框（自动化验证用；菜单不好点，取色器又必须真看一眼）
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\AnimaCaptioner.exe --settings

# 直接打开帮助窗口（左侧目录 + 右侧 Markdown 正文）
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\AnimaCaptioner.exe --help

# 与 Python 实现对拍（退出码 0 = 一致）
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\AnimaCaptioner.exe --parity golden.json
```

非自包含，依赖已安装的 .NET 10 Desktop Runtime 与 Windows App Runtime 1.8+。

---

## 帮助文档：随程序分发的 Markdown

帮助窗口是独立的顶层窗口，经典的两栏：**左边模块目录、右边正文**。正文不是硬编码的
字符串，而是 exe 同级 `docs\` 里的 `.md` 文件：

```text
<程序目录>\docs\01-shortcuts.md
              \02-interface.md  ...
```

**改文档不必重新编译，也不必重启程序**——编辑 `.md`，点工具栏「重新载入」（`F5`）即可。

渲染走 **Markdig**（纯 .NET，`lib/net10.0`）把语法树转成 WinUI 原生控件，
**刻意不用 WebView2**：那要拉起整个浏览器渲染栈，滚动和选中都明显迟滞，而帮助页
只需要静态排版。支持标题、粗斜体、删除线、行内代码、代码块、列表（可嵌套）、
任务列表 ☑、引用、表格、图片、站内 `.md` 链接跳转与外链。

细节见 `docs\07-editing-docs.md`（程序里就能看）。

---

## 提示词是「标签 + 自然语言」，不是一串标签

这一节是右栏设计的全部依据，来自 Anima 官方模型卡与 ComfyUI 的官方实现。

**架构**（`comfy/text_encoders/anima.py` + `comfy/ldm/anima/model.py`，ComfyUI 自带源码）：

- 真正加载的文本编码器**只有 Qwen3-0.6B**。T5-XXL 的**分词器**在跑，但不加载权重，
  只负责给出 token 位置；DiT 内部一个 6 层 `LLMAdapter` 把 Qwen 的隐状态交叉注意力到
  T5 词表索引的嵌入上。权重文件里 `net.llm_adapter.embed.weight` 是 `[32128, 1024]`——
  32128 正是 T5-XXL 的词表大小，1024 是 Qwen3-0.6B 的 hidden size。
- 512 是**下限不是上限**：`if out.shape[1] < 512: pad(...)`，两个分词器都是
  `max_length=99999999`。

**中文必须翻成英文**，这不是偏好而是硬约束。实测 Anima 自带的 T5 分词器（32100 个
piece）里 **CJK 数量为 0**：51 个常用汉字里 49 个直接塌成 `<unk>`，整句中文只有
33–45% 的位置是有效 token。作者原话也确认了：*"T5 wasn't trained on Chinese...
the model only understands English."* 所以**不要**试图把中文直接写进提示词。

**官方允许三种写法**（模型卡 Prompting）：

> You can mix tags and natural language in arbitrary order.
> If using pure natural langauge, more descriptive is better. Aim for at least 2 sentences.

- 官方工作流模板（`image_anima_base_v1.json`）的正向提示词是**一整段英文散文、零标签**；
- 官方 `example.png` 里那次生成的提示词是**质量前缀 + 四句散文**，前缀以句号收尾：
  `masterpiece, best quality, score_7, safe. An anime girl wearing a …`

所以本工具把提示词模型化为 **`标签区 + 自然语言尾巴`**（`Core/PromptText.cs`）。
尾巴从第一个"句边界"开始（`. ` / 段末句末标点且前半是已知标签 / 首字母大写的 ≥3 词
且不是标签），一直吃到结尾。

**为什么按"尾巴"切而不是逐段判断**：实测 679 个真实标签里，含句末标点的 **0 个**、
首字母大写的 **0 个**，而这两个信号在英文散文里到处都是。连续的尾巴也正是官方写法和
本程序"追加散文"的产物，所以规则可预测。回归测试第 1 项把这个事实钉住了：
**53 个现有 caption 必须 100% 判成标签**，一旦规则改错会立刻报出来。

`ProseTail` **保留前导分隔符**（`. ` 或空格），于是 `标签.join(", ") + ProseTail`
逐字节还原原文——官方那种"前缀以句号收尾"的写法不会被改写。

### 着色

`提示词原文` 用 `RichEditBox`（`TextBox` 没有区间字符格式 API），标签段按大类上色，
散文尾巴单独一色。散文必须**有自己的颜色**而不是留白：官方那段散文按逗号切是
13/13 全不是标签，留白的话用户无从判断"这段到底被识别成标签了没有"，染成 general
又会把整段散文涂成标签色。颜色在「设置 → 标签配色」里改，共 7 项（6 个大类 + 自然语言）。

### 翻译器只做「中文 → 英文自然语言」

刻意**不产出散装标签**：标签那条路由中栏负责（词库补全 + 大类分段 + 校验 + 段内排序），
两个入口都产标签会互相打架。而且实测让 8B 直接写英文标签是错的——它会写成散文
（`black and white maid outfit`），用 GBNF 语法约束单词都合法了却会选错词义
（`少女` → 某个光之美少女角色）。

翻译时先按词库给一份**中文词 → 英文单词**的对照塞进系统提示。这不是装饰：人称词
（`少女`/`少年`/`女孩`/`男人`/`人物`…）在合并表和人工映射里**全都查不到**——它们对应的
标签是 `1girl` 这类数量词，不是词对词的翻译。不给译法模型就会把中文原样留下，实测稳定
产出 `A silver-haired少女 stands…`；补上对照后同样 4 段中文从 **2/4 留中文降到 0/4**。

万一还是留了中文，会依次重试（点名那几个字 → 就地改写整句），仍不行才弹窗如实告知。
**这一步不能省**：中文在 T5 里会塌成 `<unk>`，等于那句话没写，而在编辑器里看不出来。

---

## 标签顺序：官方规定

来源是 Anima 官方模型卡（`circlestone-labs/Anima`，Prompting → Tag order）：

```
[quality/meta/year/safety tags] [1girl/1boy/1other etc] [character] [series] [artist] [general tags]
```

> Within each tag section, the tags can be in arbitrary order.

**这两句合起来决定了本工具的整个分段设计**：

- 大类之间顺序固定 —— 所以中栏按 6 个大类分段显示，每段一个框；
- 大类内部顺序任意 —— 所以段内顺序**完全按用户排的来**，上移/下移/拖拽
  都只在本段内生效，绝不会因为引用数之类的启发式被自动重排。

实现见 `Core/TagSections.cs`。归类顺序是先认官方逐条列出的控制词（`masterpiece`、
`score_7`、`year 2025`、`safe`、`1girl`、`solo` …），再查词库，且**Anima 自带索引
优先于合并表**。这一步不能省：`1girl`、`solo` 在 Anima 索引里是 `general`，在中文
包里被标成「人物数量」；`sensitive` 在 Danbooru 是 general 但官方列在 safety 段。

### 唯一一处与官方示例不一致的地方

官方模型卡的完整示例把 `solo` 放在 general 段中间（`smile, brown hair, hat, solo, ...`），
本工具把它归进「人物数量」段和 `1girl` 放一起。依据：

1. 官方给第二节的写法是 `[1girl/1boy/1other etc]`，社区指南一律称其为 subject count，
   而 solo 正是数量语义；
2. **用户自己那 53 个 caption 全部把 solo 紧跟在 1girl 后面**（35 个含 solo 的
   caption 里它都在 index 2）。归到 general 会让这 35 个全部被重排；
3. 官方明确说段内顺序任意，所以示例里 solo 的位置不构成"它属于 general"的论据。

这不是悄悄做的决定——`corecheck sections` 的第 4 项会把这个差异显式报出来。

---

## 标签的右键菜单与就地编辑

**双击**标签行就地改名，**右键**出功能菜单（复制 / 粘贴 / 编辑 / 翻译 / 移动 / 删除 / 校验）。
两条路都收在 `Core/TagEdit.cs` 里，它不引用 WinUI，所以能被 net8 回归程序直接测。

「翻译 / 规范化」做四件事：中文 → 英文标签、去下划线、旧别名 → 正典名、补画师 `@`。
实测对现有 53 个 caption 会改掉 9 处，全是 Danbooru 改名表里的真实旧别名
（`clothes removed`→`unworn clothes`、`print yukata`→`print kimono`、
`leg garter`→`frilled thigh strap` …），已逐条核对"源不在 Anima 索引、目标在"。

### 两条不变量

1. **认不出来的一律原样保留。** 词库不是全集——实测 679 个真实标签里 74 个词库不认
   （`2 fingers`、`bangs`、`mixed bathing`），用户在写新角色名或自造短语时更是如此。
   编辑这类标签只会提示"不在任何索引中"，**绝不代改**。静默改掉就是在用户没察觉的
   情况下动了训练集。
2. **改名不许产生重复。** `print yukata` 的规范形式是 `print kimono`，而列表里可能
   本来就有它——这时拒绝并说明撞上了哪个，而不是让两个一样的标签并排存在。

顺带摁住一条优先级：**Anima 索引压过 Danbooru 改名表**。`wink` 在 Anima 索引里是原生
标签（62.8 万帖），Danbooru 把它并进了 `one eye closed`，跟着 Danbooru 改会把一个
Anima 认的标签换成另一个，属于无谓改动。

### 输入框里的按键不归标签管

`Del`、`Ctrl+C/V`、`Ctrl+↑/↓` 这些动作**先问焦点在哪**：落在提示词框（`RichEditBox`）、
中文描述框或行内编辑框里就直接让路。没有这道闸，在提示词里按 `Del` 会连带删掉一个
标签。`Ctrl+C/V` 更进一步挂在 `ListView.KeyboardAccelerators` 上——加速键只在焦点
位于该子树内时才触发，从机制上避开冲突。

## 模型补译：给词库没有的标签加中文

中栏右栏的中文来自词库，但**词库不是全集**。实测现有 53 个 caption、2,335 个标签实例里，
扣掉控制词（`@sayori style`／`explicit`／`nsfw`，87 个实例）后仍有约 **147 个实例、93 个不同标签**
查不到中文，两类为主：

- **Anima 原生标签**：`cat smile`、`ass grab`、`topless`、`vertical stripes`、`overflow`
  —— Danbooru 上没有，Anima 自带索引里有（这正是"Anima 会员优先"那条规则覆盖的集合）
- **颜色+名词的复合词**：`white back bow`、`white maid headdress`、`red and white striped`

右键 →「模型翻译（补中文释义）」或 `Ctrl+Shift+T`，让模型补一个。做法收在
`Core/TagTranslator.cs`（工具循环）与 `Core/ModelGlossStore.cs`（缓存 + 清洗）。

### 三条设计约束

1. **只处理缺中文的标签。** 词库已有官方释义时菜单项直接变灰。再让模型译一遍
   只会得到一个可能更差的版本，而且会诱导用户去信它。
2. **由模型自己决定搜不搜。** 实测同一个模型：`cat smile` 直接答（`searched=False`，
   5 token），`kimekomi` 才发起 `web_search`（`searched=True`，487 token，2 轮）。
   一律强制先搜会把它已经会的也搜一遍，既慢又容易被无关结果带偏。
3. **结果绝不写回词库。** 词库值来自 Danbooru 对照表，模型译的是推断。界面上用
   橙色 + `≈` 前缀 + 「模型译 · 非官方」标注，缓存进独立的
   `%LocalAppData%\AnimaCaptioner\model-gloss.json`。模型答 `UNKNOWN` 时弃权，
   不留半条记录——错译会污染训练集，宁可少一条。

实测：`cat smile` → `猫咪微笑`（联网核对过 5 条结果）；`ass grab` → 模型弃权。
单次约 12–40 秒（含搜索），取决于模型要不要查。

## 联网搜索：接 192.168.1.4 上的既有网关

`192.168.1.4` 上已经部署好一套搜索（本机 DSH 在用）：SearXNG 容器 →
`C:\dsh-search-api\server.js`（Anthropic 兼容垫片，`127.0.0.1:8099`）→
Caddy（`nas.ddger.top:49977`）。本程序直接复用，**不需要另起服务**，走局域网即可。

设置页可选 5 种后端：局域网网关（默认）、直连 SearXNG、Tavily、Brave、Serper。
换后端只改一个 `ComboBox`，其余代码不变。

接这套网关时踩了两个**静默失败**，都很难查，记在这里：

1. **TLS SNI 必须覆盖成证书域名。** Caddy 按 SNI 选站点，直连 `192.168.1.4:49977`
   时 SNI 是那个 IP，Caddy 找不到站点，回 `TLS alert: InternalError`。
   ⚠️ `HttpClientHandler.SslOptions.TargetHost`（以及 `SocketsHttpHandler` 的同名属性）
   **不改变 SNI**——实测无效，内层就是上面那个 alert。必须自己建 `SslStream` 并
   `AuthenticateAsClientAsync(TargetHost = ...)`，所以走 `ConnectCallback`。
2. **`Host` 头也得是同一个域名。** 用 IP 当 Host，Caddy 回 `200 OK` +
   `Content-Length: 0` —— 一个**既不报错也没内容的空响应**。代码里对空响应单独给了提示，
   否则只会看到"模型说没搜到"。

**搜索这一路即使全局关着思考链也保留思考。** 实测关掉 `enable_thinking` 后模型
**根本不发出 `tool_calls`**，只把标签原样吐回来，搜索等于失效；而那个全局开关是给
散文翻译设的（那边思考纯属浪费 token）。两件事的正确取值相反，所以
`ChatWithToolsAsync` 不跟全局走，`ChatAsync` 行为不变。

另外，思考链会先吃掉一大截预算：`max_tokens=300` 时第二轮直接 `finish_reason: "length"`
且既无正文也无 `tool_calls`。工具循环给 1600，并且这种截断单独报错——它和"模型不想回答"
表现一样但成因完全不同。

## 文件

| 文件 | 作用 |
|---|---|
| `Core/TagSections.cs` | 大类定义、归类规则、`IsKnownTag`（标签 vs 散文的判据）、稳定排序、颜色串规范化。**纯逻辑，不引用 WinUI**，所以能被 net8 回归程序编译 |
| `Core/PromptText.cs` | 提示词解析：`标签区 + 自然语言尾巴`、着色区间、追加散文、重建整行。同样纯逻辑、可测 |
| `Core/TagColors.cs` | 颜色解析与画刷构造（要用 WinUI 类型，故与上面分开） |
| `Core/TagOrder.cs` | 段内上移/下移、按位删除、拖拽落位。同样纯逻辑、可测 |
| `Core/TagEdit.cs` | 单个标签的解析与规范化（右键菜单、就地改名、粘贴都走它）。同样纯逻辑、可测 |
| `Core/TagItem.cs` | 列表一行：标签、释义、校验提示、所属大类与颜色、编辑态 |
| `Core/VocabDb.cs` | 词库引擎，`animatag.py` 的 C# 移植；`ProseGlossary` 供散文翻译用。三条不能走样的规则见文件头注释 |
| `Core/CaptionFile.cs` | caption 读写，严格保字节；`SaveBody` 写"已拼好的整行"，`Save` 写标签列表 |
| `Core/AppSettings.cs` | 配置持久化，API Key 走 DPAPI |
| `SettingsDialog.xaml(.cs)` | 设置：模型接口、数据路径、7 项配色 |
| `MainWindow.xaml(.cs)` | 三栏主界面 |
| `MarkdownRenderer.cs` | Markdig → WinUI 元素（帮助窗口用） |
| `ScrollHost.cs` | 常驻滚动条的滚动宿主，帮助窗口与对话框共用 |

回归测试在 `E:\Users\14947\Documents\analyse\corecheck\`，它直接编译上面几个
**真实源文件**（不是副本），五个模式：

```powershell
cd E:\Users\14947\Documents\analyse\corecheck
dotnet build -c Release
.\bin\Release\net8.0\corecheck.exe sections    # 归类 / 排序 / 颜色 / 结构编辑
.\bin\Release\net8.0\corecheck.exe prose       # 标签 vs 散文、着色区间、保存不丢散文
.\bin\Release\net8.0\corecheck.exe edit        # 标签解析 / 防重复 / 粘贴 / 规范化收敛
.\bin\Release\net8.0\corecheck.exe roundtrip   # 53 个 caption 逐字节往返
.\bin\Release\net8.0\corecheck.exe parity      # 与 Python 实现 126 项对拍
```

### 为什么回归测试值得留着

`sections` 里最关键的一条是**对现有 53 个 caption 做恒等变换**。这些 caption 是
人工排的、且已独立验证符合官方顺序，所以一旦归类规则改错，测试会直接指出哪个
标签会被挪到哪去——比肉眼看 2,335 个标签实例可靠得多。实测 53/53 恒等。

`prose` 里最关键的是两条：**53 个 caption 必须 100% 判成标签**（否则真标签会被当成
散文藏起来），以及**保存必须保住散文尾巴**。后者是本轮抓到的真缺陷——保存路径过去调
`item.Caption.Save(tags)` 只写标签，带散文的提示词一存就把整段英文**静默删掉**；
测试里还留了一个反例，用旧写法验证它确实会丢，把这条缺陷钉死。

`edit` 守两条不变量：**认不出的标签必须原样保留**（词库不是全集，静默改掉等于偷改
训练集），以及**改名不许产生重复**。另外把"整行规范化只动 Anima 说不合规的那些、
且目标必须在 Anima 索引里、且必须幂等"钉住。

> 写这个测试时我自己的断言错过一次：起初断言"规范化对现有 caption 恒等"，
> 实测 53 个里有 10 个会被改。逐条查 Danbooru 改名表后确认**改是对的**——
> 那 10 个（`print yukata`→`print kimono`、`leg garter`→`frilled thigh strap` 等）
> 源形式都不在 Anima 索引里、目标形式都在，是真实的旧别名。所以把断言改成上面
> 那三条可验证的规则，而不是"什么都不许变"。

`roundtrip` 保证工具不会静默改坏训练集。写盘路径只沿用原文件的字节风格，
内容没变化就不碰文件。


## 踩过的坑

这几条都是"编译能过、只有真跑起来才暴露"的，记下来免得重蹈：

1. **`GroupStyle.HeaderTemplate` 在 WinUI 3 上不渲染。** 用 `CollectionViewSource`
   + `IsSourceGrouped` 确实分好了组（日志能看到 `groups=4`），但分组头一个都没画
   出来。改成分段条带画在**每段第一行的行模板**里，边界由数据自己表达。
2. **`Grid.SetRow` 需要先有 `RowDefinition`。** 没有行定义时 Grid 只有隐含的第 0
   行，`SetRow(1..5)` 全部无效，六个色块会叠在一格上（截图上就是六行文字糊成
   一团）。现在按大类数量动态建行，并把每个色块的实际 Y 坐标写进日志备查。
3. **六个 `ColorPicker` 平铺会把对话框撑到屏幕外**，底部的「保存」按钮都够不到。
   改成页面上只留色块、取色器放进 `Flyout`，滚动区高度也限制住。
4. **构造期不能开设置对话框。** `--settings` 一开始直接调 `ShowSettingsAsync()`，
   而 `_cfg` 要等 `Initialize()`（挂在 `RootLoaded` 上）才赋值，于是构造函数里第一
   句取 `cfg.ApiBaseUrl` 就 NullReferenceException。改成延迟到 `Initialize()` 之后。
5. **`FlyoutBase` 在 `Microsoft.UI.Xaml.Controls.Primitives`**，不在 `.Controls`。
6. **PS 5.1 会把无 BOM 的 UTF-8 脚本按 ANSI 读**，脚本里写死中文会乱码。测试脚本
   一律保持纯 ASCII，中文走独立 UTF-8 文件或界面输入。
7. **`ContentDialog` 的内容区只有 496 DIP 宽**（`ContentDialogMaxWidth` 548 减去
   内边距）。`RootPanel` 曾经写死 `Width="560"`，右侧 64 DIP 直接被裁掉，
   「测试连接」按钮正好在 x=479..560，于是只剩十几像素露出半个字，看上去像个
   莫名其妙的小控件。**给对话框内容设固定宽度这件事本身就不对**，宽度交给它自己算。
8. **弹出层关闭时焦点会被对话框的第一个输入框抢走。** 取色器的色块过去是
   `Border`——它不能接收焦点，所以 `Flyout` 一关，焦点无处可去就落回接口地址
   输入框，而 `ScrollViewer` 默认 `BringIntoViewOnFocusChange=true` 会把它滚进
   视野，表现为"点开配色再点别处，设置就跳回顶部"。消融实测：旧实现滚动位置
   `551 -> 28`，换成可聚焦的 `Button`（`Padding=0` 保持色块外观）并在 `Closed`
   里显式 `Focus()` 后停在 `551`。现在 `Closed` 会比对视口位移，超过 8 DIP 就告警。

9. **`RichEditBox` 改字符格式会触发 `TextChanged`，形成无限回环。** 给提示词上底色
   （`Range.CharacterFormat.BackgroundColor = …`）本身会触发 `TextChanged`，而那个
   处理函数里又调 `ApplyPromptColors()`，于是"改色 → 事件 → 再改色"自激。实测日志
   `#215 → #254`：**4 秒内自激约 40 次**，每 ~85ms 一次，界面卡到没法操作。
   修法是着色期间把 `_loading` 立起来挡掉回灌，并比对"上次解析的正文"提前返回。
   修复后空闲 4 秒 CPU 仅 16ms。
10. **`RichEditBox` 的底色不能用 alpha=0 去"清空"。** 先设 `FromArgb(0,0,0,0)` 再设
   半透明色，结果 RichEdit 把 alpha=0 当成**不透明黑**，整段正文变成白字黑底、
   完全读不了。底色只能用**全不透明的实色**（把分类色按比例混进实际背景色），
   并把文字色显式设回主题前景色。
11. **`RichEditBox.TextChanged` 的委托类型是 `RoutedEventHandler`**，不是
   `TextChangedEventHandler`（那是 `TextBox` 的）。签名写成后者编译期直接报
   "没有与委托匹配的重载"。
12. **`HttpClient.Timeout` 在发出第一个请求之后就不能再设**，会抛
   `InvalidOperationException: This instance has already started one or more requests`。
   "夹中文就重试一次"那条路径第一次真跑起来就炸在这里。现在记住当前值、只在真变化时
   才写回（同一个值重复赋值一样会抛），并吞掉这个异常继续用原超时。
13. **散文行是接在标签行之后加的。** 早先的写法先把它塞进 `_tagRows` 再拼标签行，
   于是它被顶到列表**最上面**，和注释里写的"挂在末尾"正好相反。
14. **`ContentDialog` 的内容超出高度是静默裁掉的。** 帮助页、标签统计、校验结果
    都把文本直接当 `Content` 塞进一个裸 `TextBlock`，没有任何滚动容器——帮助页
    整整少了两节（「翻译是怎么做的」「写入文件的安全性」），而界面上完全看不出
    还有下文（用户的原话就是"缺少个滚动条"）。现在一律套 `ScrollViewer`，高度上限
    按 `XamlRoot.Size.Height` 实算（写死会在小窗口上照样溢出），别处也不再
    用空格凑列对齐——比例字体下那样排出来的列一定是歪的，改用两列 `Grid`。
15. **WinUI 3 的 `ScrollViewer` 没有 `ScrollingIndicatorMode`。** 那是 UWP 的 API，
    编译期直接报"未包含该定义"。它的滚动条是浮层、会自动淡出，**静止状态下截帧里
    一个像素都不画**——"下面还有内容"这件事在界面上根本看不出来，而这正是要解决的
    问题本身。独立 `ScrollBar` 控件也试过：`MouseIndicator` 只在鼠标交互时才画滑块
    （实测静止 31px / 滚动中 117px，指针一离开就消失），`TouchIndicator` 什么都不画。
    两条路都得不到"常驻可见"，最后用 `Thumb` 自己画：长度按
    `viewport/(viewport+scrollable)` 算，位置按滚动比例映射到"轨道高 − 滑块高"
    这段可移动范围。实测 `trackH=349 thumbH=94 max=948 viewport=349`。
16. **滑块几何不能只在 `ViewChanged` 里同步。** 该事件首帧不一定触发，只靠它的话
    初始滑块是按 `Maximum=0` 算出来的（实测 31px，正确值 117px），看着就像坏了。
    现在 `Loaded` 和 `SizeChanged` 也各同步一次。
17. **自绘滚动条不能拿"轨道"的高度当显示判据。** 折叠的如果是轨道本身，就会
    "因为量到 0 所以折叠，因为折叠所以量到 0"自锁——滚动条永久不再出现
    （实测日志 `trackH=0 thumbH=NaN`）。改成量**容器**（始终参与布局、高度恒为
    行高），只折叠轨道、保留容器占位，顺带避免文字宽度在出现/消失时跳一下。
18. **`ChangeView` 在内容刚换、还没测量完时会被静默忽略。** 这正是预览窗口踩过的
    坑，帮助页又踩了一次：切换模块后正文停在**中间**（截图上顶部表格被截断、
    "预览窗口"标题出现在上方），而代码里明明调了 `ScrollToTop`。修法是先
    `UpdateLayout()` 再 `ChangeView`，并在 `DispatcherQueue` 里补一次。
19. **已有父级的 `UIElement` 不能再 `Add` 到第二个父级。** 表格单元格复用了
    `RenderBlockToElement`——它把渲染结果从临时 `StackPanel` 里**直接返回**，
    元素仍挂在那上面，于是 `Children.Add` 抛
    `COMException 0x800F1000: 没有检测到已安装的组件`。这个提示和真实原因完全
    对不上，极难查。修法是返回前 `Children.RemoveAt(0)` 断开父子关系。
20. **`MakeBold` 不能用 `VisualTreeHelper` 遍历。** 表头加粗时控件还没进可视树、
    也没跑过布局，可视树子级数是 0，遍历会**静默什么都不做**。改走逻辑树
    （`Panel.Children` / `Border.Child` / `ContentControl.Content`）。
21. **`Block` 在 `Microsoft.UI.Xaml.Documents` 与 `Markdig.Syntax` 之间歧义**
    （CS0104）。用 `using MdBlock = Markdig.Syntax.Block;` 消歧。
22. **`Paragraph` 与 `Span` 没有共同基类**，`Paragraph.Inlines` 和 `Span.Inlines`
    都是 `InlineCollection` 但类型上不通用。行内渲染的形参要收 `InlineCollection`，
    不能收 `Paragraph`（否则给 `Span` 加粗体时编译报无法转换）。
23. **`SelectableTextBlock` 在 WinUI 3 里不存在。** 想要可选中文本只能用
    `TextBlock.IsTextSelectionEnabled` 或 `RichTextBlock`。另外
    `CommunityToolkit.WinUI.Controls.MarkdownTextBlock` 这个**包名也不存在**
    （NuGet 404）；真正存在的是 `CommunityToolkit.Labs.WinUI.Controls.MarkdownTextBlock`，
    但只有 prerelease 版本，且主题走一个约 60 个画刷的 `Config` 快照、赋值后
    必须强制重渲染才生效、深色模式不自动跟随。所以这里选了 Markdig + 自己渲染。
    查包是否存在要直接打 NuGet v3 API，别信搜索结果。
24. **Markdig 的 `ListBlock.OrderedStart` 是 `string` 不是 `int`**，
    而 `TableColumnDefinition.Alignment` 在**列定义**上、`TableCell` 上没有
    `ColumnAlignment`。这些都用真实编译器验证过，别凭印象写。
25. **Markdown 摘要里的行内标记要摘掉。** 摘要显示在标题下面，是**纯文本**、
    不走 Markdown 渲染，所以 `**加粗**` 会把星号原样露出来（截图里就是这样）。
    `HelpDocs.StripInlineMarks` 处理链接语法、反引号、强调号、删除线。
26. **`TextBox` 在 WinUI 3 里没有 `VisibilityChanged`**（又是 UWP 的 API）。想让
    刚变可见的输入框抓到焦点，只能在切换可见性之后显式 `Focus`——而且要先把
    布局跑完（`UpdateLayout`），否则焦点会被静默忽略，和 `ChangeView` 那个坑同源。
27. **菜单项上的快捷键是窗口级的，会吃掉输入框的按键。** 「删除选中」挂了 `Del`，
    于是在提示词框里按 `Del` 会连带删掉一个标签；`Ctrl+↑/↓`、`Ctrl+C/V` 同理。
    现在这些动作一律先问 `TextInputFocused()`（`FocusManager.GetFocusedElement`，
    判 `TextBox`/`RichEditBox`/`AutoSuggestBox`），命中就直接让路。
    `Ctrl+C/V` 更进一步，改成挂在 `ListView.KeyboardAccelerators` 上——加速键只在
    焦点位于该子树内时才触发，从机制上避开冲突。
28. **编辑中切换图片会让改名落到错的图片上。** `RefreshTagRows` 会整体重建列表，
    而那时 `_pendingTags` 已经是**另一张图**的内容了，再拿编辑框里的文字去改名
    就是张冠李戴。所以重建前一律 `AbandonEdit`（放弃而不是提交）；正常的
    Enter/失焦提交会先把 `_editingRow` 置空，走到这里已是 no-op。
29. **编辑框的内容要在退出时从存下来的引用读，不能再去可视树里找。** 行被虚拟化
    回收后 `ContainerFromItem` 就返回 null，那时用户敲进去的内容会被读成 null、
    退化成"没有改动"——**静默丢掉用户的输入**。
30. **编辑态下要拦住行拖拽**（`DragItemsStarting` 里 `e.Cancel = true`）：
    在行内输入框里按住鼠标划选文字会命中 `ListView` 的行拖拽，选字变成拖行。
31. **`SslOptions.TargetHost` 不改变 TLS SNI。** 直连 `192.168.1.4:49977` 时
    Caddy 按 SNI 选站点，看到 IP 就回 `TLS alert: InternalError`。
    `SocketsHttpHandler.SslOptions.TargetHost` 和 `HttpClientHandler` 的同名属性
    **都无效**（实测内层就是那个 alert）；只有自己建 `SslStream` 并
    `AuthenticateAsClientAsync(TargetHost = ...)` 才管用，所以搜索客户端走
    `ConnectCallback`。
32. **`Host` 头与 SNI 不一致时 Caddy 返回 `200` + 空体。** 用 IP 当 Host 得到的是
    `Content-Length: 0` 的**静默空响应**——不报错、没内容，表现为"模型说没搜到"，
    实际是根本没查。代码里对空响应单独给了一句指向 Host/SNI 的提示。
33. **关掉思考链后模型不会发出 `tool_calls`。** 实测同一提示词：`enable_thinking=false`
    时 `searched=False rounds=1`，模型把标签原样吐回来；开启时才 `searched=True rounds=2`。
    而全局那个开关是给散文翻译设的（那边思考纯属浪费 token），两件事的正确取值相反，
    所以 `ChatWithToolsAsync` 不跟全局走。
34. **思考链会先吃光 `max_tokens`。** 给 300 时第二轮直接 `finish_reason: "length"`，
    既无正文也无 `tool_calls`——和"模型不想回答"表现一样但成因完全不同，所以单独报错。
    工具循环给 1600。
35. **把 `ApplyTo` 的内容误写进构造函数，会用控件默认值覆盖配置。** 搜索那段初值
    代码一度放错位置，构造时先读未勾选的复选框、把 `cfg.SearchEnabled` 写成了
    `false`，再把它读回控件——界面与配置文件双双变错，而编译和运行都不报错。
    构造函数**只读不写**。发现它的方式是 UIA 取复选框真实状态（`state=Off`）
    与配置文件（`true`）对不上。

排查界面问题用的是**应用内几何转储**而不是 VS 的实时可视化树：后者要交互式 attach，
脚本驱动不了，而转储同一套 XAML 诊断 API（`TransformToVisual` / `GettingFocusEvent` /
`ViewChanged`）能反复跑、还能留成回归。`AC_DUMP_TREE=1` 转储完整可视树，
`AC_TRACE=1` 追踪滚动与焦点；几何转储和横向溢出告警常驻，不占多少日志。

界面验证走 **UI Automation**（`AutomationElement`）而不是模拟点击：`ValuePattern` 能
直接设输入框的值、`InvokePattern` 能直接按按钮、`SelectionItemPattern` 能直接选列表项、
`BoundingRectangle` 给的是物理屏幕坐标。模拟点击容易被三件事坑：坐标该不该乘 DPI 缩放、
点之前要先移动光标再按下、以及 `SetFocus` 拿不到真实键盘焦点（`SendKeys` 因此不生效）。
另外 UIA 报的 `Hyperlink` 矩形可能覆盖**整行**（实测 5 个字的链接报 531px 宽），
点它的中心会落在文字之外——那种情况直接用 `InvokePattern`。

## 已知限制

- 中栏的最小宽度受内容约束：按钮行实测要 226 DIP，所以 `MinWidth` 必须 ≥226 且
  <默认中栏自然宽度（约 251 DIP），否则默认的 2:2:3 自己就被挤变形。改按钮文案
  前先量一下。
- 段内拖拽是所见即所得；**跨段拖拽会被拨回本段**（那才符合官方顺序），
  要换段只能改标签本身。
- 「规整大类顺序」刻意只放在菜单里（Ctrl+Shift+S），没放进按钮行——
  加进去这一行就超过 240 DIP 的下限了。
- 每次刷新列表都会重建全部行对象（`ObservableCollection` 整体重填），所以滚动
  位置不保留。标签数在几十到几百量级时无感；上千标签的 caption 会需要改成
  增量更新。

---

## 许可

**本仓库包含三类内容，许可各不相同。** 三者互不覆盖。

| 内容 | 位置 | 许可 |
|---|---|---|
| **软件本体**（C# 源码、XAML、构建配置） | `*.cs` `*.xaml` `*.csproj` `app.manifest` `Assets/` | **GNU GPL v3.0**（见 [LICENSE](LICENSE)） |
| **自带文档**（帮助窗口读的 Markdown） | `docs/*.md` | **CC BY-NC-SA 4.0** |
| **标签数据** | **不在本仓库** — 见下 | 遵循各自上游协议 |

### 1. 软件本体：GPL-3.0

完整文本见 [LICENSE](LICENSE)。要点：可自由使用、修改、分发；**分发修改版必须
同样以 GPL-3.0 开源并提供源码**；无担保。

### 2. 自带文档：CC BY-NC-SA 4.0

`docs/` 下的 10 个 `.md`（帮助窗口左侧目录的内容）采用
[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)：

- **BY** — 转载需署名
- **NC** — **不得用于商业用途**
- **SA** — 改编后必须以相同协议分发

> 这批文档是随程序分发的数据文件，**不是代码**，所以不跟随 GPL-3.0。
> 这也意味着：**若你要把本软件用于商业用途，`docs/` 需单独取得授权或整体移除**
> ——移除后程序仍可运行（只影响帮助窗口）。
>
> 注意 `docs/08-safety-ratings.md` 里的评级判据**译自 Danbooru 官方 wiki
> `howto:rate`**，原文版权归 Danbooru 所有，此处按引用与改写使用。

### 3. 标签数据：不随本仓库分发

**仓库里没有任何词库文件**（无 `.sqlite` / `.csv` / 标签表）。程序在运行期从
本机路径读取，默认写在 [`Core/AppSettings.cs`](Core/AppSettings.cs)：

```
VocabDbPath = <你的词库路径>\vocab.sqlite
SeedPath    = <你的词库路径>\seed.json
```

词库由上游多个来源合并而成，**各自协议不同，下游再分发时请逐个确认**：

| 上游来源 | 内容 | 许可 |
|---|---|---|
| [ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table](https://github.com/ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table) | Danbooru 全量标签中英对照 | MIT |
| [Qiongyi44/ComfyUI-Bilingual-Prompt-Inspector](https://github.com/Qiongyi44/ComfyUI-Bilingual-Prompt-Inspector) | 社区整理的 Anima 词库包 | MIT |
| [ShiroEirin/comfyui-good-anima](https://github.com/ShiroEirin/comfyui-good-anima) | 画师下架清单 `banned_tags.csv` | **GPL-3.0** |
| Danbooru 本身 | 标签改名表、废弃标签表（抓取自公开 API） | 归 Danbooru 所有 |

⚠️ 其中画师下架清单来自 **GPL-3.0** 仓库，**下游再分发词库时需注意其 copyleft 条款**。

### 4. 第三方依赖

均为**运行时依赖**，不修改、不重新分发其源码：

| 依赖 | 许可 |
|---|---|
| [Microsoft.WindowsAppSDK](https://www.nuget.org/packages/Microsoft.WindowsAppSDK) | Microsoft 专有（Windows App SDK 许可） |
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) | MIT |
| [Markdig](https://www.nuget.org/packages/Markdig) | BSD-2-Clause |
| Microsoft.Windows.SDK.BuildTools | Microsoft 专有 |

### 5. 模型与输出

- **Anima 模型权重**（[circlestone-labs/Anima](https://huggingface.co/circlestone-labs/Anima)，
  CircleStone Labs × Comfy Org）采用**非商业许可**，与本软件许可无关。
- 本工具只改写你本机的 `.txt` 标注文件，**不包含也不分发任何模型权重或图像**。
- 用本工具处理训练集所产出模型的许可，取决于**模型本身的许可**，与本软件无关。

### 一句话总结

> 代码 GPL-3.0；自带文档 CC BY-NC-SA 4.0（**非商业**）；标签数据不在此仓库、
> 遵循上游各自协议；模型许可另算。

