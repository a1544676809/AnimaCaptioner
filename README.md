# AnimaCaptioner

给 Anima LoRA 训练集打标签的 Windows 桌面工具。

三栏布局：左边图片列表，中间是按官方大类分段的标签，右边上半是写进 `.txt` 的提示词原文
（标签 + 英文散文，按大类着色），下半是把中文描述译成英文散文的翻译器。

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

三个调试用的启动参数：

```
--settings     直接打开设置对话框
--help         直接打开帮助窗口
--parity <f>   与 Python 参考实现对拍
```

菜单在 UI 自动化下不太好点，所以留了前两个。

## 它替你处理掉的事情

Anima 的提示词格式有几处容易踩的细节，工具里都做了：

- **标签顺序按官方模板分段**，段内顺序完全按你排的来——上移、下移、拖拽都只在本段内生效，
  不会因为引用数之类的启发式被自动重排。
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
Core/            纯逻辑层，不引用 WinUI，所以能被 net8 回归程序直接编译
  VocabDb.cs       词库引擎（animatag.py 的 C# 移植）
  TagSections.cs   大类定义、归类规则、稳定排序
  PromptText.cs    提示词解析、着色区间、token 预算
  CaptionFile.cs   caption 读写，严格保字节
  ...
MainWindow.xaml(.cs)      三栏主界面
SettingsDialog.xaml(.cs)  设置
HelpWindow.xaml(.cs)     帮助窗口
MarkdownRenderer.cs      Markdig → WinUI 元素
docs/                   帮助文档（10 个模块，随程序分发）
```

## 回归测试

`tests/` 下的断言直接编译 `Core/` 的真实源文件（不是副本），所以归类规则、提示词解析、
写盘逻辑一改就会被测到。

```powershell
cd tests
dotnet build -c Release
.\bin\Release\net8.0\corecheck.exe parity      # clone 下来就能跑，不需要任何外部数据
```

`parity` 与 Python 参考实现做 126 项对拍，只需要随仓库分发的 `tests/golden.json`。
其余几项要用到你的训练集和词库，它们在仓库外，按参数或环境变量传入：

```powershell
$env:AC_DATASET = 'D:\train\my-lora'          # 训练集目录
$env:AC_VOCABDB = "$env:LOCALAPPDATA\AnimaCaptioner\vocab\vocab.sqlite"
$env:AC_SEED    = "$env:LOCALAPPDATA\AnimaCaptioner\vocab\seed.json"

.\bin\Release\net8.0\corecheck.exe sections    # 归类 / 排序 / 颜色 / 结构编辑
.\bin\Release\net8.0\corecheck.exe prose       # 标签 vs 散文、保存不丢散文、token 预算
.\bin\Release\net8.0\corecheck.exe edit        # 标签解析 / 防重复 / 规范化收敛
.\bin\Release\net8.0\corecheck.exe safety      # 安全标签档位核对
.\bin\Release\net8.0\corecheck.exe roundtrip   # caption 逐字节往返
.\bin\Release\net8.0\corecheck.exe defaults    # 默认路径解析
```

缺输入时会以退出码 2 明确报错，不会假装通过。

`sections` 和 `roundtrip` 会拿真实训练集做恒等变换——一旦归类规则改错，会直接指出
哪个标签会被挪到哪去，比肉眼看几千个标签可靠。

开发过程中踩过的坑记在 [NOTES.md](NOTES.md)。

## 许可

本仓库包含三类内容，许可各不相同，三者互不覆盖。

| 内容 | 位置 | 许可 |
|---|---|---|
| **软件本体**（C# 源码、XAML、构建配置） | `*.cs` `*.xaml` `*.csproj` `app.manifest` `Assets/` | **GNU GPL v3.0**（见 [LICENSE](LICENSE)） |
| **自带文档**（帮助窗口读的 Markdown） | `docs/*.md` | **CC BY-NC-SA 4.0** |
| **标签数据** | 不在本仓库 | 遵循各自上游协议 |

### 1. 软件本体：GPL-3.0

可自由使用、修改、分发；分发修改版必须同样以 GPL-3.0 开源并提供源码；无担保。

### 2. 自带文档：CC BY-NC-SA 4.0

`docs/` 下的 10 个 `.md`（帮助窗口左侧目录的内容）采用
[CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)：署名、**非商业**、
相同方式共享。

这批文档是随程序分发的数据文件，不是代码，所以不跟随 GPL-3.0。这意味着**若要把本软件
用于商业用途，`docs/` 需单独取得授权或整体移除**——移除后程序仍可运行，只影响帮助窗口。

`docs/08-safety-ratings.md` 里的评级判据译自 Danbooru 官方 wiki `howto:rate`，
原文版权归 Danbooru 所有，此处按引用与改写使用。

### 3. 标签数据：不随本仓库分发

仓库里没有任何词库文件（无 `.sqlite` / `.csv` / 标签表）。程序在运行期从本机路径读取，
默认值见 [`Core/AppSettings.cs`](Core/AppSettings.cs)。

词库由多个上游来源合并而成，各自协议不同，下游再分发时请逐个确认：

| 上游来源 | 内容 | 许可 |
|---|---|---|
| [ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table](https://github.com/ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table) | Danbooru 全量标签中英对照 | MIT |
| [Qiongyi44/ComfyUI-Bilingual-Prompt-Inspector](https://github.com/Qiongyi44/ComfyUI-Bilingual-Prompt-Inspector) | 社区整理的 Anima 词库包 | MIT |
| [ShiroEirin/comfyui-good-anima](https://github.com/ShiroEirin/comfyui-good-anima) | 画师下架清单 `banned_tags.csv` | **GPL-3.0** |
| Danbooru 本身 | 标签改名表、废弃标签表（抓取自公开 API） | 归 Danbooru 所有 |

⚠️ 画师下架清单来自 GPL-3.0 仓库，下游再分发词库时需注意其 copyleft 条款。

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
