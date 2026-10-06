# 开发笔记

都是「编译能过，只有真跑起来才暴露」的问题，记下来免得重蹈。
按主题分组，同类问题放在一起。

---

## WinUI 3 里没有的 API

这几个都是 UWP / WinUI 2 的，WinUI 3 移除了。写之前先让编译器判定，别凭印象。

- `ScrollViewer.ScrollingIndicatorMode` — 不存在。
- `SelectableTextBlock` — 不存在。要可选中文本只能用 `TextBlock.IsTextSelectionEnabled`
  或 `RichTextBlock`。
- `TextBox.VisibilityChanged` — 不存在。想让刚变可见的输入框拿到焦点，只能在切换可见性
  之后显式 `Focus()`，而且要先把布局跑完（`UpdateLayout()`），否则焦点会被静默忽略。
- `GridSplitter` — 不存在。分栏用 `Thumb` + 手动拖拽。
- 顺带一条：查 NuGet 包是否存在要直接打 v3 API，别信搜索结果。
  `CommunityToolkit.WinUI.Controls.MarkdownTextBlock` 这个**包名 404**；真正存在的是
  `CommunityToolkit.Labs.WinUI.Controls.MarkdownTextBlock`，但只有 prerelease，
  且主题走一个约 60 个画刷的 `Config` 快照、赋值后必须强制重渲染才生效、
  深色模式不自动跟随。所以这里选了 Markdig + 自己渲染。

有些 API 是命名空间或类型对不上，编译器会直接报出来：

- `FlyoutBase` 在 `Microsoft.UI.Xaml.Controls.Primitives`，不在 `.Controls`。
- `Block` 在 `Microsoft.UI.Xaml.Documents` 和 `Markdig.Syntax` 之间歧义（CS0104）。
  用 `using MdBlock = Markdig.Syntax.Block;` 消歧。
- `Paragraph` 和 `Span` **没有共同基类**。两者的 `Inlines` 都是 `InlineCollection`
  但类型上不通用，所以行内渲染的形参要收 `InlineCollection`，收 `Paragraph` 的话
  给 `Span` 加粗体时会报无法转换。
- Markdig 的 `ListBlock.OrderedStart` 是 `string` 不是 `int`；表格的对齐在
  **列定义**上，`TableCell` 没有 `ColumnAlignment`。

---

## ContentDialog

这类问题的共同点是：**出错了但界面不报**，只是内容悄悄消失。

- **内容区只有 496 DIP 宽**（`ContentDialogMaxWidth` 548 减去内边距）。`RootPanel`
  曾写死 `Width="560"`，右侧 64 DIP 直接被裁掉。「测试连接」按钮正好在 x=479..560，
  只剩十几像素露出半个字，看上去像个莫名其妙的小控件。
  **给对话框内容设固定宽度这件事本身就不对**，宽度交给它自己算。
- **内容超出高度是静默裁掉的。** 帮助页、标签统计、校验结果都把文本直接当 `Content`
  塞进一个裸 `TextBlock`，没有滚动容器。帮助页整整少了两节，界面完全看不出还有下文。
  现在一律套 `ScrollViewer`，高度上限按 `XamlRoot.Size.Height` 实算——写死的话
  小窗口上照样溢出。
- 六个 `ColorPicker` 平铺会把对话框撑到屏幕外，底部的「保存」都够不到。
  改成页面上只留色块、取色器放进 `Flyout`。
- **弹出层关闭时焦点会被第一个输入框抢走。** 色块过去是 `Border`，它不能接收焦点，
  所以 `Flyout` 一关焦点无处可去，落回接口地址输入框，而 `ScrollViewer` 默认
  `BringIntoViewOnFocusChange=true` 会把它滚进视野。表现就是「点开配色再点别处，
  设置跳回顶部」。消融实测：旧实现滚动位置 `551 → 28`，换成可聚焦的 `Button`
  （`Padding=0` 保持色块外观）并在 `Closed` 里显式 `Focus()` 后停在 `551`。
- 别用空格凑列对齐。比例字体下那样排出来的列一定是歪的，改用两列 `Grid`。
  注意 `Grid.SetRow` 需要先有 `RowDefinition`——没有行定义时 Grid 只有隐含的第 0 行，
  `SetRow(1..5)` 全部无效，六个色块会叠在一格上。

---

## RichEditBox

- **改字符格式会触发 `TextChanged`，形成无限回环。** 给提示词上底色
  （`Range.CharacterFormat.BackgroundColor`）本身会触发 `TextChanged`，而处理函数里
  又调 `ApplyPromptColors()`，于是「改色 → 事件 → 再改色」自激。实测日志 `#215 → #254`，
  **4 秒内自激约 40 次**，界面卡到没法操作。修法是着色期间立一个 `_loading` 挡住回灌，
  并比对「上次解析的正文」提前返回。修复后空闲 4 秒 CPU 仅 16ms。
- **底色不能用 alpha=0 去「清空」。** 先设 `FromArgb(0,0,0,0)` 再设半透明色，
  RichEdit 会把 alpha=0 当成**不透明黑**，整段正文变成白字黑底、完全读不了。
  底色只能用全不透明的实色（把分类色按比例混进实际背景色），并把文字色显式设回
  主题前景色。
- `TextChanged` 的委托类型是 `RoutedEventHandler`，不是 `TextChangedEventHandler`
  （后者是 `TextBox` 的）。签名写错编译期直接报「没有与委托匹配的重载」。

---

## 滚动条

WinUI 3 的滚动条是浮层、会自动淡出，**静止状态下截帧里一个像素都不画**。
「下面还有内容」这件事在界面上根本看不出来，而这正是要解决的问题。

试过的路都走不通：独立的 `ScrollBar` 控件用 `MouseIndicator` 只在鼠标交互时画滑块
（实测静止 31px / 滚动中 117px，指针一离开就消失），`TouchIndicator` 什么都不画。

最后用 `Thumb` 自己画：长度按 `viewport/(viewport+scrollable)` 算，位置按滚动比例
映射到「轨道高 − 滑块高」这段可移动范围。实测 `trackH=349 thumbH=94 max=948
viewport=349`。

自己画要注意两处：

- **几何不能只在 `ViewChanged` 里同步。** 该事件首帧不一定触发，只靠它的话初始滑块
  是按 `Maximum=0` 算出来的（实测 31px，正确值 117px），看着就像坏了。
  `Loaded` 和 `SizeChanged` 也各同步一次。
- **显示判据不能拿「轨道」的高度。** 折叠的如果是轨道本身，就会「因为量到 0 所以折叠，
  因为折叠所以量到 0」自锁，滚动条永久不再出现（实测日志 `trackH=0 thumbH=NaN`）。
  改成量**容器**（始终参与布局、高度恒为行高），只折叠轨道、保留容器占位，
  顺带避免文字宽度在出现/消失时跳一下。

另有一条同源问题：**`ChangeView` 在内容刚换、还没测量完时会被静默忽略。**
预览窗口和帮助页都踩到了——切换模块后正文停在中间，而代码里明明调了 `ScrollToTop`。
修法是先 `UpdateLayout()` 再 `ChangeView`，并在 `DispatcherQueue` 里补一次。

---

## Markdown 渲染

- **已有父级的 `UIElement` 不能再 `Add` 到第二个父级。** 表格单元格复用了
  `RenderBlockToElement`，它把渲染结果从临时 `StackPanel` 里直接返回，元素仍挂在
  那上面，于是 `Children.Add` 抛 `COMException 0x800F1000: 没有检测到已安装的组件`。
  这个提示和真实原因完全对不上，极难查。修法是返回前 `Children.RemoveAt(0)`
  断开父子关系。
- **加粗不能用 `VisualTreeHelper` 遍历。** 表头加粗时控件还没进可视树、也没跑过布局，
  可视树子级数是 0，遍历会静默什么都不做。改走逻辑树（`Panel.Children` /
  `Border.Child` / `ContentControl.Content`）。
- **摘要里的行内标记要摘掉。** 摘要显示在标题下面，是纯文本、不走 Markdown 渲染，
  所以 `**加粗**` 会把星号原样露出来。`HelpDocs.StripInlineMarks` 处理链接语法、
  反引号、强调号、删除线。
- 渲染器和 `HelpDocs` 的配合见 `Core/HelpDocs.cs`：`SplitHeader` 取第一个非空行当标题，
  所以 **`.md` 开头不能加 HTML 注释**，否则标题会退化成文件名；而渲染器刻意把 HTML 块
  当代码块原样显示（静默丢弃会让问题查不出来），于是那行注释还会直接显示在正文里。

---

## 焦点与键盘

- **菜单项上的快捷键是窗口级的，会吃掉输入框的按键。**「删除选中」挂了 `Del`，
  于是在提示词框里按 `Del` 会连带删掉一个标签；`Ctrl+↑/↓`、`Ctrl+C/V` 同理。
  现在这些动作一律先问 `TextInputFocused()`（`FocusManager.GetFocusedElement`，
  判 `TextBox` / `RichEditBox` / `AutoSuggestBox`），命中就直接让路。
  `Ctrl+C/V` 更进一步，挂在 `ListView.KeyboardAccelerators` 上——加速键只在焦点位于
  该子树内时才触发，从机制上避开冲突。
- **构造期不能开设置对话框。** `--settings` 一开始直接调 `ShowSettingsAsync()`，
  而 `_cfg` 要等 `Initialize()`（挂在 `RootLoaded` 上）才赋值，于是构造函数里第一句
  取 `cfg.ApiBaseUrl` 就 NullReferenceException。改成延迟到 `Initialize()` 之后。

---

## 列表与标签编辑

- **编辑中切换图片会让改名落到错的图片上。** `RefreshTagRows` 会整体重建列表，
  而那时 `_pendingTags` 已经是**另一张图**的内容了，再拿编辑框里的文字去改名就是
  张冠李戴。所以重建前一律 `AbandonEdit`（放弃而不是提交）；正常的 Enter / 失焦提交
  会先把 `_editingRow` 置空，走到这里已是 no-op。
- **编辑框的内容要在退出时从存下来的引用读**，不能再去可视树里找。行被虚拟化回收后
  `ContainerFromItem` 返回 null，那时用户敲进去的内容会被读成 null、退化成「没有改动」，
  **静默丢掉用户的输入**。
- **编辑态下要拦住行拖拽**（`DragItemsStarting` 里 `e.Cancel = true`）：在行内输入框里
  按住鼠标划选文字会命中 `ListView` 的行拖拽，选字变成拖行。
- **散文行是接在标签行之后加的。** 早先的写法先把它塞进 `_tagRows` 再拼标签行，
  于是它被顶到列表最上面，和注释里写的「挂在末尾」正好相反。
- **`GroupStyle.HeaderTemplate` 在 WinUI 3 上不渲染。** 用 `CollectionViewSource` +
  `IsSourceGrouped` 确实分好了组（日志能看到 `groups=4`），但分组头一个都没画出来。
  改成分段条带画在**每段第一行的行模板**里，边界由数据自己表达。

---

## 网络

接 `192.168.1.4` 上的搜索网关时踩的两个静默失败，成因都在 Caddy 按 SNI 选站点：

- **`SslOptions.TargetHost` 不改变 TLS SNI。** 直连 `192.168.1.4:49977` 时 SNI 是那个
  IP，Caddy 找不到站点，回 `TLS alert: InternalError`。`SocketsHttpHandler.SslOptions`
  和 `HttpClientHandler` 的同名属性**都无效**（实测内层就是那个 alert）。只有自己建
  `SslStream` 并 `AuthenticateAsClientAsync(TargetHost = ...)` 才管用，
  所以搜索客户端走 `ConnectCallback`。
- **`Host` 头与 SNI 不一致时 Caddy 返回 `200` + 空体。** 用 IP 当 Host 得到的是
  `Content-Length: 0` 的静默空响应，不报错、没内容，表现为「模型说没搜到」，
  实际是根本没查。代码里对空响应单独给了一句指向 Host / SNI 的提示。
- `HttpClient.Timeout` 在发出第一个请求之后就不能再设，会抛
  `InvalidOperationException`。「夹中文就重试一次」那条路径第一次真跑起来就炸在这里。
  现在记住当前值、只在真变化时才写回（同一个值重复赋值一样会抛），并吞掉异常继续用原超时。

---

## 模型调用

- **关掉思考链后模型不会发出 `tool_calls`。** 实测同一提示词：`enable_thinking=false`
  时 `searched=False rounds=1`，模型把标签原样吐回来；开启时才 `searched=True rounds=2`。
  而全局那个开关是给散文翻译设的（那边思考纯属浪费 token），两件事的正确取值相反，
  所以 `ChatWithToolsAsync` 不跟全局走。
- **思考链会先吃光 `max_tokens`。** 给 300 时第二轮直接 `finish_reason: "length"`，
  既无正文也无 `tool_calls`，和「模型不想回答」表现一样但成因完全不同，所以单独报错。
  工具循环给 1600。
- **把 `ApplyTo` 的内容误写进构造函数，会用控件默认值覆盖配置。** 搜索那段初值代码
  一度放错位置，构造时先读未勾选的复选框、把 `cfg.SearchEnabled` 写成了 `false`，
  再把它读回控件。界面与配置文件双双变错，而编译和运行都不报错。构造函数**只读不写**。
  发现它的方式是 UIA 取复选框真实状态（`state=Off`）与配置文件（`true`）对不上。

---

## 调试方法

**界面问题用应用内几何转储，不用 VS 的实时可视化树。** 后者要交互式 attach，
脚本驱动不了；转储同一套 XAML 诊断 API（`TransformToVisual` / `GettingFocusEvent` /
`ViewChanged`）能反复跑，还能留成回归。`AC_DUMP_TREE=1` 转储完整可视树，
`AC_TRACE=1` 追踪滚动与焦点；几何转储和横向溢出告警常驻，不占多少日志。

**界面验证走 UI Automation 而不是模拟点击。** `ValuePattern` 能直接设输入框的值、
`InvokePattern` 能直接按按钮、`SelectionItemPattern` 能直接选列表项、
`BoundingRectangle` 给的是物理屏幕坐标。模拟点击容易被三件事坑：坐标该不该乘 DPI 缩放、
点之前要先移动光标再按下、以及 `SetFocus` 拿不到真实键盘焦点（`SendKeys` 因此不生效）。
另外 UIA 报的 `Hyperlink` 矩形可能覆盖**整行**（实测 5 个字的链接报 531px 宽），
点它的中心会落在文字之外，那种情况直接用 `InvokePattern`。

**PowerShell 5.1 会把无 BOM 的 UTF-8 脚本按 ANSI 读**，脚本里写死中文会乱码。
测试脚本一律保持纯 ASCII，中文走独立 UTF-8 文件或界面输入。

---

## 图标

设计源是 `tools/icon/app.svg`（蓝色圆角底 + 白色画框 + 暖色画芯 + 四条彩色标签条），
由 `tools/icon/make-ico.py` 生成 `Assets/app.ico`。重跑：

```
python tools/icon/make-ico.py
```

**为什么要分尺寸出图。** 原设计在 256 画布上，标签条之间的空隙是 `10/256`。
缩到 16px 时那空隙只剩 **0.62 像素**——间隙消失，四条粘成一块，整个图标读起来是
"彩色糊块"。这不是设计缺陷，而是矢量图标缩放的固有限制；Windows 自己的图标也是
分尺寸出图的（Notepad 的 16px 就不是 256px 的等比缩小）。所以：

| 尺寸 | 用哪张 | 条间空隙 |
|---|---|---|
| 16px | 简化版：3 条加粗 | 1.06px |
| 20 / 24px | 简化版：4 条宽隙、去白点 | 1.33 / 1.59px |
| 32px 及以上 | `app.svg` 原设计 | 1.25px 起，细节完整 |

判断依据是**空隙像素数**（<1.0px 必然粘连），不是眼睛估的。

**几个实测出来的结论：**

- **从高分辨率降采样，比按目标尺寸原生渲染更锐。** 我原以为"每个尺寸原生 4× 渲染再
  缩"更好，实测相反：1024 → LANCZOS 在 16/20/24/32px 的拉普拉斯方差都更高
  （如 32px：6348 vs 5818）。原因是降采样时每个输出像素平均了更多样本，抗锯齿更好。
- **`app.svg` 不能放在 `Assets\` 下。** WinUI SDK 对 `Assets\**` 有默认通配，放那里会被
  拷进输出目录（实测确认，我一开始只是推断、推错了）。它是构建输入不是运行时资源，
  所以放 `tools/icon/`。
- **脚本里的非 GBK 字符会让整个流程崩掉。** 默认 Windows 控制台是 GBK，我那句
  `print("... ✓")` 直接抛 `UnicodeEncodeError`——而 ICO 已经写完了，于是"成功"被报成失败。
  现在脚本开头 `sys.stdout.reconfigure(errors="replace")`。

两处接线是分开的，容易只做一半：

- `csproj` 的 `<ApplicationIcon>` → 资源管理器、任务栏固定项看到的 **exe 图标**。
  少了这行 exe 会顶着 .NET 默认图标，而 `Assets/app.ico` 只被当普通文件拷来拷去。
- `AppWindow.SetIcon()`（三个窗口各调一次）→ **窗口自身**的图标。

**验证 exe 内嵌图标，用字节查找比取图比对更严格。** Pillow 写 ICO 时每帧是独立 PNG，
而 Windows 把各帧**原样**存进 PE 的 `RT_ICON`。所以逐帧取出 PNG 字节去 exe 里
`find()` 即可——没有任何重采样或色彩转换的余地（走 GDI 取图反而要处理
`GetDIBits` 和 64 位句柄溢出两个坑）。窗口图标则置顶后截图看标题栏；
**别只看句柄非零**，句柄有值不代表画的是你要的那个图标。

**截图验证有个 DPI 陷阱。** 这台机器是 150% 缩放，PowerShell 进程默认不是 DPI 感知的：
`GetWindowRect` 返回**虚拟化后**的坐标，而 `CopyFromScreen` 拿到的是**物理**像素，
于是裁出来的区域落在窗口右上约 2/3 处，拍到的是别的窗口。先调
`SetProcessDPIAware()` 再取坐标即可（实测窗口从"1000×627"变成真实的 1500×940）。


