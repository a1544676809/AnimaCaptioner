# AnimaCaptioner

给 Anima LoRA 训练集打标签、以及生图时写提示词的 Windows 桌面工具。

三栏布局：左边图片列表，中间是按官方大类分段的标签，右边上半是写进 `.txt` 的提示词原文
（标签 + 英文散文，按大类着色），下半是把中文描述译成英文散文的翻译器。

还有一个**写标签模式**（`Ctrl+M` 切换）：同一个程序里的第二块界面，从零组装一段提示词、
`Ctrl+S` 复制到剪贴板直接拿去生图，附带历史与预设库、token 预算、安全档位提示，
以及把标签里的圆括号转义成 `\(` `\)` 的开关——否则 ComfyUI 会把
`210 (reverse:1999)` 的括号当权重语法吃掉、年份整个消失。
它和标注模式共用中栏、右栏和全部词库能力，只换了左栏、菜单和「主操作」。
详见 [写标签模式](docs/11-write-tag-mode.md)。

两个模式都有**撤销 / 重做**（`Ctrl+Z` / `Ctrl+Y`，或菜单「编辑」）：标签的增删改移、
粘贴、规范化、重排、翻译、清空都能一步步退回，步数在设置里可调
（默认 100 步，填 0 表示关闭）。撤销只改内存、**不写文件**——要落盘仍然按保存。
**每一份文档有自己独立的撤销历史**：训练集里每张图、提示词库里每条草稿各记一份，
所以翻到别的图再翻回来照样能退，也绝不会把上一张图的标签带进当前这份文档。文档被
别的程序改过时，那一份的历史会作废并从空历史开始。

设置里还能把右栏下半部分换成 **AI 助手**（默认关）：一个能**看当前这张图**、
能查词库的对话面板。它把改动摆成一张提案卡片——要加的标签、要写的英文散文——
**点了「应用」才写进提示词**，而一次应用就是一个撤销点。没有对应标签的概念会被
写成散文而不是硬造标签；词库查不到的写法（例如画师触发词 `@syrstyle`）仍然保留为标签。
原来的翻译器没有删，关掉助手面板就退回去。
详见 [AI 助手](docs/12-ai-assistant.md)。

全部在本机运行，词库、校验、翻译都不出局域网。

## 环境

- Windows 10 1809+ 或 Windows 11
- .NET 10 Desktop Runtime
- Windows App Runtime 1.8+

不需要 Visual Studio，`dotnet build` 就能构建。

## 构建与运行

```powershell
git clone https://github.com/a1544676809/AnimaCaptioner.git
cd AnimaCaptioner
dotnet build -c Release

# 运行
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\AnimaCaptioner.exe
```

根目录有 `AnimaCaptioner.slnx`，所以上面这条会把**应用和 `tests/` 回归工程一起构建**。
只想要应用时：`dotnet build AnimaCaptioner.csproj -c Release`。

解决方案只有一个平台（`x64`，与应用声明的 `<Platforms>` 一致），并且刻意关掉了
`AppendPlatformToOutputPath`——否则显式 `Platform=x64` 会让输出多出一层 `bin\x64\`，
"VS 里生成"和"命令行 build"就会落在两个目录、同一个 exe 有两份。

五个调试用的启动参数：

```
--settings     直接打开设置对话框
--help         直接打开帮助窗口
--tagsearch    直接打开标签搜索窗口
--draft        直接以写标签模式启动
--parity <f>   与 Python 参考实现对拍
```

菜单在 UI 自动化下不太好点，所以留了这几个。

## 它替你处理掉的事情

Anima 的提示词格式有几处容易踩的细节，工具里都做了：

- **标签顺序按官方模板分段**，段内顺序完全按你排的来——上移、下移、拖拽都只在本段内生效，
  不会因为引用数之类的启发式被自动重排。移动时列表位置不动（视线不会被打回顶端），
  标签被移出视口时列表跟着翻一页，让它停在最上/最下一行。
- **标签和散文分开处理**。官方允许两者混排，官方示例本身就是「质量前缀（句号收尾）+ 散文」，
  所以提示词被拆成「标签区 + 自然语言尾巴」，散文单独着色、不参与标签校验和排序。
- **写入严格保字节**。只沿用原文件的行尾和编码风格，内容没变化就不碰文件。
- **中文会被拦下**。Anima 的 T5 分词器词表里一个汉字都没有，中文进去等于没写，
  而编辑器里看不出来，所以翻译器会检查并重试。
- **超长会警告**。训练器在 512 token 处强制截断且不报错，丢掉的正好是排在末尾的散文。
  超预算时状态栏会直接提示，并给出预估 token 数。
- **认不出的标签原样保留**。词库不是全集，编辑时只提示不代改——静默改掉等于在你没察觉的
  情况下动了训练集。

用法、快捷键、这些规则的依据，都在程序内的帮助窗口里（`F1`），文档是随程序分发的
Markdown，改完点「重新载入」即可生效，不必重新编译。

## 目录

```
AnimaCaptioner.slnx  解决方案：Visual Studio 直接打开它；根目录 dotnet build 也认它
tests/            回归测试工程（直接编译 Core/ 真实源码，见下）
Core/            纯逻辑层，不引用 WinUI，所以能被 net10 回归程序直接编译
  VocabDb.cs       词库引擎（animatag.py 的 C# 移植）
  VocabBuilder.cs  词库构建：五源合并 + Anima 索引，纯 .NET（原先的 Python 脚本已并入）
  VocabSources.cs  上游素材清单（文件名 / 下载地址 / 许可），构建器·设置页·README 共用
  TagSections.cs   大类定义、归类规则、稳定排序
  PromptText.cs    提示词解析、着色区间、token 预算
  TagEscape.cs     生图用的括号转义（只做 ( ) ，判据是词库认不认得）
  PromptDoc.cs     两个模式的「当前文档」抽象（LabelDoc / DraftDoc）
  PromptLibraryStore.cs  写标签模式的历史与预设库（独立 JSON）
  PromptHistory.cs 撤销 / 重做栈（快照式，只改内存不落盘）
  PromptHistoryStore.cs  按文档身份分桶的历史（离开时记内容、打开时验一致 + LRU）
  CaptionFile.cs   caption 读写，严格保字节
  ...
MainWindow.xaml(.cs)      主界面：标注模式 + 写标签模式（共用中栏与右栏）
SettingsDialog.xaml(.cs)  设置
HelpWindow.xaml(.cs)     帮助窗口
MarkdownRenderer.cs      Markdig → WinUI 元素
docs/                   帮助文档（11 个模块，随程序分发）
  dev/                  开发文档：架构取舍、文档格式、本训练集审计（不随程序分发）
Assets/app.ico          应用图标（9 档尺寸 16–256px）
vocab/seed.json         词库种子映射母本（273 条中文概念 → 标签），构建时复制到库旁边
tools/icon/             图标的 SVG 源文件与生成脚本
```

## 回归测试

`tests/` 下的断言直接编译 `Core/` 的真实源文件（不是副本），所以归类规则、提示词解析、
写盘逻辑一改就会被测到。

```powershell
cd tests
dotnet build -c Release
.\bin\Release\net10.0\corecheck.exe parity      # clone 下来就能跑，不需要任何外部数据
.\bin\Release\net10.0\corecheck.exe models      # /models 响应解析，同样不需要外部数据
.\bin\Release\net10.0\corecheck.exe reasoning   # 思考内容不许进译文（给了数据集会多扫一遍真实 caption）
.\bin\Release\net10.0\corecheck.exe prompts     # 提示词库：历史/预设/淘汰/去重/标题派生（纯临时文件）
.\bin\Release\net10.0\corecheck.exe history     # 撤销 / 重做：栈语义、按文档分桶、外部改动作废、LRU 淘汰（纯逻辑）
.\bin\Release\net10.0\corecheck.exe agent       # AI 助手：发图尺寸 / 提案解析 / 工具循环（假 host，不联网）
.\bin\Release\net10.0\corecheck.exe vocab       # 词库构建：从素材重建并与既有库逐表对拍（需 AC_VOCABSRC）
```

有一条**要联网、要已配置接口**的诊断（不属于上面这组"clone 下来就能跑"的）：

```powershell
.\bin\Release\net10.0\corecheck.exe llm   # 拉一次 /models 看服务端报的能力，并 A/B 验证"关闭思考"是否真生效
```

它会用你 `settings.json` 里的地址与密钥打真实请求（因此单独列出来）。
有用的场合：换了接口或模型之后，想确认服务端到底报了什么能力、以及
`enable_thinking` 那个字段是不是又被静默忽略了。**档位设成「关闭思考」时它才有 A/B 可言**——
其它档位本来就要思考，探针会说明并跳过。

`parity` 与 Python 参考实现做 117 项对拍，只需要随仓库分发的 `tests/golden.json`。
（条数会随训练集内容变化——`golden.json` 是参考实现对**当前数据集**的输出快照，
改了 caption 就要用生成脚本重新出一份。）
它有一半的检查（prior / arrange / build / audit）比的是"参考实现对**同一个数据集**的
输出"，所以那几项读的是设置里当前的数据集目录。**数据集对不上时它会跳过那几节并以
退出码 2 结束**，而不是刷出一堆看起来像代码坏了的差异——2 的含义是"没能验证"，
不是"通过"（与"缺数据集/词库"同一个约定）。
其余几项要用到你的训练集和词库，它们在仓库外，按参数或环境变量传入：

```powershell
$env:AC_DATASET = 'D:\train\my-lora'          # 训练集目录
$env:AC_VOCABDB = "$env:LOCALAPPDATA\AnimaCaptioner\vocab\vocab.sqlite"
$env:AC_SEED    = "$env:LOCALAPPDATA\AnimaCaptioner\vocab\seed.json"
$env:AC_VOCABSRC = 'D:\vocab-sources'         # 词库素材目录（只有 vocab 那一项用）

.\bin\Release\net10.0\corecheck.exe sections    # 归类 / 排序 / 颜色 / 结构编辑
.\bin\Release\net10.0\corecheck.exe prose       # 标签 vs 散文、保存不丢散文、token 预算
.\bin\Release\net10.0\corecheck.exe edit        # 标签解析 / 改名 / 防重复 / 规范化收敛
.\bin\Release\net10.0\corecheck.exe complete    # 输入框候选与 Tab 补全（含 679 个真实标签的抗性）
.\bin\Release\net10.0\corecheck.exe escape      # 标签转义：与 ComfyUI 解析器对拍（给了词库会全量验 25,815 个标签）
.\bin\Release\net10.0\corecheck.exe safety      # 安全标签档位核对
.\bin\Release\net10.0\corecheck.exe roundtrip   # caption 逐字节往返
.\bin\Release\net10.0\corecheck.exe defaults    # 默认路径解析
```

缺输入时会以退出码 2 明确报错，不会假装通过。

`vocab` 另需 `AC_VOCABSRC` 指向素材目录（上游文件不放仓库里）；给了 `AC_VOCABDB`
就与那份库对拍，没给则只验证"能从素材建出一份完整的库"。

`sections` 和 `roundtrip` 会拿真实训练集做恒等变换——一旦归类规则改错，会直接指出
哪个标签会被挪到哪去，比肉眼看几千个标签可靠。

开发过程中踩过的坑记在 [NOTES.md](NOTES.md)。

## 许可

本仓库包含三类内容，许可各不相同，三者互不覆盖。

| 内容 | 位置 | 许可 |
|---|---|---|
| **软件本体**（C# 源码、XAML、构建配置） | `*.cs` `*.xaml` `*.csproj` `app.manifest` `Assets/` | **GNU GPL v3.0**（见 [LICENSE](LICENSE)） |
| **词库种子映射**（273 条「中文概念 → 标签」） | `vocab/seed.json` | **GNU GPL v3.0**（与软件本体一致） |
| **自带文档**（帮助窗口读的 Markdown） | `docs/*.md`（顶层） | **CC BY-NC-SA 4.0** |
| **开发文档**（不随程序分发） | `docs/dev/`、`NOTES.md` | **GNU GPL v3.0**（与软件本体一致） |
| **成品词库**（`vocab.sqlite`，由上表上游来源构建） | 不在本仓库 | 遵循各自上游协议 |

### 1. 软件本体：GPL-3.0

可自由使用、修改、分发；分发修改版必须同样以 GPL-3.0 开源并提供源码；无担保。

### 2. 自带文档：CC BY-NC-SA 4.0

`docs/` **顶层**的 11 个 `.md`（帮助窗口左侧目录的内容）采用
[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)：署名、**非商业**、
相同方式共享。

这批文档是随程序分发的数据文件，不是代码，所以不跟随 GPL-3.0。这意味着**若要把本软件
用于商业用途，`docs/` 需单独取得授权或整体移除**——移除后程序仍可运行，只影响帮助窗口。

`docs/08-safety-ratings.md` 里的评级判据译自 Danbooru 官方 wiki `howto:rate`，
原文版权归 Danbooru 所有，此处按引用与改写使用。

### 3. 标签数据：不随本仓库分发

仓库里没有任何成品词库文件（无 `.sqlite` 导出）。程序在运行期从本机路径读取，
默认值见 [`Core/AppSettings.cs`](Core/AppSettings.cs)。**构建词库不需要外部脚本**——
程序自带构建器，设置里单开了一页「词库构建」（见下）。

#### 词库从哪来

Anima 官方**没有发布标签表**：模型卡只规定规则（下划线换成空格、画师加 `@`、
`score_*` 是唯一保留下划线的例外），不给名单。所以词库是从下面这些来源合并出来的，
各自协议不同，下游再分发构建产物时请逐个确认：

| 上游来源 | 提供什么 | 条数 | 许可 |
|---|---|---|---|
| [ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table](https://github.com/ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table) | Danbooru 全量标签中英对照（`tag.sqlite`，逐日更新） | 331,376 | MIT |
| [ShiroEirin/comfyui-good-anima](https://github.com/ShiroEirin/comfyui-good-anima) → `anima-1.0.csv` | **Anima 自己的标签索引**——Anima 不用 Danbooru 的标签集，判"这个词算不算合法标签"以此为准 | 108,257 | **GPL-3.0** |
| [ShiroEirin/comfyui-good-anima](https://github.com/ShiroEirin/comfyui-good-anima) → `banned_tags.csv` | 画师下架清单（只标记，不删除） | 548 | **GPL-3.0** |
| [Qiongyi44/ComfyUI-Bilingual-Prompt-Inspector](https://github.com/Qiongyi44/ComfyUI-Bilingual-Prompt-Inspector) | 社区精选包（`anima`/`base`/`characters`/`clothing`/`poses`/`camera`），带中文分类 | 951 | MIT |
| Danbooru 公开 API | `tag_aliases.json` 改名表（旧写法 → 现用标签） | 采用 33,145 | 事实数据 |
| Danbooru 公开 API | `tags.json` 废弃标签表 | 4,055 | 事实数据 |

⚠️ **Anima 索引与画师下架清单都来自 GPL-3.0 仓库**（同一个仓库的两个文件）。
本软件本体也是 GPL-3.0，所以兼容；但 Anima 索引是词库里最有价值的那部分，
若**单独**再分发构建出来的 `vocab.sqlite`，请注意它同样落在 copyleft 之下。

#### 怎么构建

构建逻辑在 [`Core/VocabBuilder.cs`](Core/VocabBuilder.cs)（纯 .NET，不依赖 Python），
素材清单在 [`Core/VocabSources.cs`](Core/VocabSources.cs)（文件名、下载地址、许可写在一处，
构建器、设置页、本文档共用同一份，不会各说一套）。两种用法：

**一、在程序里构建（推荐）。** 设置 → 「词库构建」：页面会列出全部素材及许可、
标明哪些还没下到，点「下载素材」自动抓取，再点「开始构建」。构建约 2–3 分钟，
先写临时文件、成功了才替换，中途失败或取消都不会动到原有词库。建完会顺手把
「数据路径」里的词库/种子指向新文件——**点「保存」才生效**（和其它设置项一样，
取消就是取消），保存后主窗口会立刻重载词库。

> 素材从 `raw.githubusercontent.com` 与 `danbooru.donmai.us` 抓取，国内网络**必须**
> 在页面上填一个代理（如 `http://127.0.0.1:10808`）。实测直连时 11 个文件全部失败，
> 填上代理后 11/11 成功，所以这一项不是可选项。

**二、命令行 / 回归测试。** 从素材目录重建并与既有库**逐表逐列对拍**：

```powershell
$env:AC_VOCABSRC = 'D:\vocab-sources'                          # 放上游文件的目录
$env:AC_VOCABDB  = "$env:LOCALAPPDATA\AnimaCaptioner\vocab\vocab.sqlite"   # 基准库（可选）

.\bin\Release\net10.0\corecheck.exe vocab        # 重建 + 对拍
```

对拍是这一项真正的价值所在：它比 `best` 的**每一行每一列**（含 `in_anima` /
`anima_aliases`）、`alias`、`anima_index` 三张表，再用应用自己的加载器比对
解析/搜索/归类的结果。构建器漏掉任何语义（大小写、去重顺序、标记规则）都会以
具体行数差异暴露，而不是等到用户搜索时才发现少了一批标签。

#### 一个容易错的语义

`in_anima` 用 `best.tag = anima_index.name` 做**大小写敏感**的精确匹配。字符名如
`Aether (Genshin Impact)` 在 `best` 里是大写、在 Anima 索引里是小写；改用忽略大小写
去配会多标 28 条、少 15 条别名——数字看着"差不多"，所以这种错误很难被察觉。
构建器与对拍测试都把这一点钉住了。

种子映射 [`vocab/seed.json`](vocab/seed.json)（273 条「中文概念 → 标签」，补全量表里
没有精确中文键的常用说法）随程序分发：它是构建时的母本，会被复制到库旁边。
它不构成"随附词库"——没有它词库照样加载，只是少这 273 条中文映射。

### 4. 第三方依赖

均为运行时依赖，不修改、不重新分发其源码：

| 依赖 | 许可 |
|---|---|
| [Microsoft.WindowsAppSDK](https://www.nuget.org/packages/Microsoft.WindowsAppSDK) | Microsoft 专有 |
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) | MIT |
| [Markdig](https://www.nuget.org/packages/Markdig) | BSD-2-Clause |
| Microsoft.Windows.SDK.BuildTools | Microsoft 专有 |

### 5. 模型与输出

[Anima](https://huggingface.co/circlestone-labs/Anima) 模型权重（CircleStone Labs × Comfy Org）
采用非商业许可，与本软件许可无关。本工具只改写你本机的 `.txt` 标注文件，
不包含也不分发任何模型权重或图像。
