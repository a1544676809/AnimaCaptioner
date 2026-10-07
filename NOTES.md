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

还有一个是**宿主容器类型选错**：帮助页原来把 `ScrollHost` 塞进 `ContentControl`，而
`ContentControl` 的子元素默认**不拉伸**（按内容的 `DesiredSize` 排、左对齐），于是正文
在宽窗口里只铺到最长那一行的宽度。实测最大化时容器 1429 DIP、内容只有 936，右边空一大片，
看着就像"正文没有适应窗口"。换成 `Grid` 即可——`Grid` 的子元素默认填满。
判断手法很直接：把容器和内容的 `ActualWidth` 一起打出来比（帮助页的 `help layout:`
日志就是干这个的，`slot` 对 `host`/`body`）。

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
- **加速键别挂在铺满窗口的容器上。** 帮助页的 `F5` 原来挂在 `RootGrid` 上，结果是鼠标停在
  正文这类"自己没提示"的地方时，页面上会凭空冒出一个小「F5」提示框。改前改后用同一个脚本、
  同一个坐标悬停正文各截一次，差别很清楚；挂到「重新载入」按钮上就不冒了，而快捷键照样
  整个窗口有效。

  机制没验到底：把 `F5` **同时**挂到 `RootGrid` 和按钮上做消融，反而不冒了，所以"就近归属"
  比"从悬停处往上找到持有者"更贴切，但这条没坐实——所以代码注释里只写了实测到的现象。

  顺带一条对取证有用的观察：这类**加速键提示**在指针移上去时立刻就画，而 `ToolTipService`
  的普通提示要等悬停计时，合成鼠标输入（`SetCursorPos`）能稳定抓到前者、抓不到后者。
- **`Esc` 干脆不用加速键。** 它有同样的提示问题，而且语义上不属于任何控件。改用
  `RootGrid.AddHandler(UIElement.KeyDownEvent, …, handledEventsToo: true)`，一样能关窗。
- **新开的窗口抢不到前台：只调 `SetForegroundWindow` 没用，得核对实际前台句柄。**
  双击图片打开预览时，主窗口的 XAML 焦点还留在图片列表上，系统按「有焦点的窗口才是活动
  窗口」把前台扳回主窗口——实测抢到前台后 **2–5ms** 就被换走（日志里 `preview front:
  focused` 之后紧跟 `preview activated: Deactivated` / `main activated: CodeActivated`；
  注意后者是 `CodeActivated` 而非 `PointerActivated`，所以不是"点击"打回来的）。
  试过三种都不行：只调 `SetForegroundWindow`；把窗口尺寸设置挪到激活之后（**消融证明
  Resize/Move 无辜**——跳过它照样丢焦点）；把键盘焦点 `Focus()` 进预览的 `ScrollViewer`。
  而补抢第二次时 `Activated` **不再触发**——因为补抢是在该事件处理器里调 `Activate()`，
  重入时事件被吞掉，所以"靠事件重试"这条路本身不可靠。
  现在不看事件：开一个 500ms 的竞争窗口，每 40ms 核对一次 `GetForegroundWindow()`，
  不是自己就再抢；连续三次是自己就提前收工（实际约 120ms），之后用户切窗口不会再被抢回。
  一条有用的判据：**这个坑与双击快慢相关**（快 → 主窗口留焦点，慢 → 预览拿到焦点）——
  那正是"某次激活排在后面"的特征，不是随机故障。
  取证工具留在 `analyse\_zfix\`：`probe3.ps1` 连续开两次并比对 z 序/前台，`probe4.ps1`
  验键盘（←/→ 翻页、Esc 关闭）没被焦点改动弄坏。
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
- **"改名"和"规范化"是两件事，不能共用一个函数。** `TagEdit.Apply` 原来直接转调
  `NormalizeOne`，而后者对「输入解析出来就是它自己」一律回"已经是规范形式"——
  于是把 `long hair` 改成 `twintails` 会被拒，而 `twintails` 正是 Tab 补全给出的
  形式，**新增的补全在就地编辑里完全落不了地**（日志 `edit rejected:
  'long hair' -> 'twintails' (anima)`）。现在 `Apply` 只管四件该拒的事：空、逗号、
  没改、撞名；词库不认的新名字也照用——用户明确敲了它，而训练集里本来就有
  74 个词库不认的真实标签。
- **列表重建会把滚动位置打回顶端。** `_tagRows` 一 `Clear()`，ListView 的 extent
  就归零，`ScrollViewer` 立刻回到 0——上移/下移一个标签也会连带把视线甩到列表开头。
  实测日志（`natural` = 不做任何补救时它自己停在哪儿）每一条都是
  `natural=0`，而 `want` 是几百。所以重建前记下位置、重建后放回去，只有换图片
  时才显式设回 0（**不要靠"Clear 会自然弹回顶端"这个副作用**——那是 bug 行为，
  拿它当特性用，等于把"回到顶部"押在将来可能被改掉的东西上）。
- **`ChangeView` 是异步的，两件事都得按这个来。** 一是紧接着读 `VerticalOffset`
  拿到的还是旧值（我第一版日志因此一直显示 `want == now`，看不出到底放回去没有）；
  二是"把位置放回去 + 需要时翻页"这两步**不能排在同一个 DispatcherQueue 回调里**：
  后写的 `ChangeView(want)` 会把先算好的翻页覆盖掉，同时量几何又量到"已经翻好了"的
  旧布局，结果翻页量算成 0、行最终停在视口外 38 DIP。正确做法是位置放回后**再排一跳**
  去量、并把翻页量一次设到目标位置。
- **`ListView` 会把 `Ctrl+方向键` 吃掉（当"移动焦点"用）。** 挂在菜单项上的
  `Ctrl+↑/↓` 加速键在列表有焦点时**收不到**——实测焦点在列表里按 `Ctrl+↓`，
  列表滚了 3%、标签一个没动；焦点在按钮上时同一下按键正常生效。所以这份加速键
  得挂在 `TagList` 自己的 `KeyboardAccelerators` 上，和 `Ctrl+C/V` 同一个道理。
- **虚拟化会让"量这一行在哪"偶发失败。** `ContainerFromItem` 在重建后两跳仍可能
  返回 null（实测约四成），那时算不出翻页量、标签可能滑出视口。退回平台自己的
  `ScrollIntoView` 兜底（它内部会先实体化再滚动）；能量到就用自己算的精确翻页量，
  落点是"正好贴边"，可复现也可断言。

---

## 可编辑下拉框

设置里的「模型名」是 `ComboBox` + `IsEditable="True"`（该属性在 WinUI 3 里存在，
用编译器核实过）。两处只有真跑起来才会暴露：

- **清空 `Items` 会把 `Text` 一并清掉。** 刷新模型列表时先 `Items.Clear()` 再填，
  结果用户刚填好的模型名凭空消失——而列表本身看起来完全正常。修法是先存后恢复，
  实测 `model name preserved = True`。
- **下拉项的文字就是选中后要用的值。** 若把「量化级别」「上下文长度」拼进条目文本，
  选中后那一长串会被当成模型名发出去，服务端只回 404。所以条目里只放模型 id，
  扩展信息放下面那行提示。

顺带一条判断：**「服务端没报模型」不等于「连接失败」。** 有些网关不实现 `/models`
（回 `{"object":"list","data":[]}`），这时该照常让用户手填模型名，所以
`ListModelsAsync` 把 `Ok` 和 `Models.Count` 分成两件事返回。

### `AutoSuggestBox`（Tab 补全 / 候选列表）

补全用的是 `AutoSuggestBox`，它的模板内部有一个真的 `TextBox`。四条实测教训：

- **`AutoSuggestBox` 没有 `SelectAll`**（那是 `TextBox` 的，编译期 CS1061）。
  进编辑时要全选，得先走可视树拿到模板里的 `TextBox`，聚焦和全选都落在它身上。
- **给候选列表设 `ItemsSource` 会再触发一次 `TextChanged`。** 那一下看着像
  "用户改了字"，若据此清掉 Tab 循环，**连按两次 Tab 会卡在同一格上**
  （日志里两条 `fresh=True` 同格）。所以"循环要不要作废"只能按**内容**判
  （框里的字是否等于当前候选），不能按事件判。
- **`AutoSuggestBox` 会吃掉 `Esc`**（它要用它关候选列表），普通 `KeyDown` 和挂在
  `ListView` 上的 `Escape` 加速键都收不到——表现是"按 Esc 没反应，编辑框赖着不走"，
  换成 `AutoSuggestBox` 之前（`TextBox`）是好的。而且**必须用隧道的
  `PreviewKeyDown`**，不能用 `handledEventsToo` 的冒泡 `KeyDown`：等冒泡到我们这里，
  它已经把列表关掉了，`IsSuggestionListOpen` 永远是 false，"第一次 Esc 只关列表"
  这段逻辑根本进不去。隧道阶段读到的才是"这一下之前"的状态。
- **表格/列表里的长条目会被裁切**，但 `AutoSuggestBox` 的候选弹层在 `Popup` 里，
  不会被行容器的裁剪切掉——所以行内编辑框换成它之后，候选列表能正常展开。

还有一个反直觉的：**候选排序不能直接用 `VocabDb.Search` 的结果。**
它的评分里"废弃/无引用 +10"会把一个**前缀**候选压到仅靠**包含**命中的候选后面，
于是 Tab 会跳到 `@nsfw bb`、`@silver bell` 这种只是碰巧含有输入串的东西上
（实测就是这条把 `nsfw`、`silver bell` 变成了画师名）。另外候选还要逐条过一遍
解析：直接把搜索结果当候选，Tab 会补出 `china dress`，而回车存下去的是 `qipao`
（词库把退役名重定向到正典名）——**框里写的和实际存的不一样**。

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

- **思考内容绝不进译文——那条「退回 reasoning」的兜底是错的。** `ParseResponse` 曾有
  一条「content 为空就把 reasoning 当结果返回」，注释还写着"思考模式可能把答案塞进
  `reasoning_content`"。**实测推翻了这个判断**：服务端配 `--reasoning-format deepseek`
  时答案稳稳落在 `content`，`reasoning_content` 只有思考；当时看到 content 为空，
  真实原因是 `max_tokens` 太小、模型还在思考就被截断（`finish_reason=length`）。
  后果很重：开思考 + 预算不足时，700 多字中文思考过程会被当成译文写进 caption
  （"好的，用户让我翻译……"），而界面上看不出任何异常。现在只有思考没有答案就如实报错。
- **`max_tokens` 是「思考 + 答案」的总预算，不是答案自己的。** 实测同一句真实长描述：
  预算 400 时思考就吃光了（`length`、content 空），1600 才正常。所以开思考时给思考
  单独留一段余量（`ReasoningHeadroom`），用户设的额度仍完整地留给答案。
- **关掉思考链后模型不会发出 `tool_calls`。** 实测同一提示词：`enable_thinking=false`
  时 `searched=False rounds=1`，模型把标签原样吐回来；开启时才 `searched=True rounds=2`。
  所以 `ChatWithToolsAsync` 不跟全局走。散文翻译则两种都行——开与关实测质量相同，
  只是慢 10–15 倍（12 个样本对照，见 `docs/05`）。
- **尖括号字面量会被编辑工具链吃掉，而 `IndexOf("")` 恒返回 0。** 源码里直接内联写
  Qwen3 那个 redacted_thinking 标记时，尖括号那截被吃掉了，代码变成 `t.IndexOf("")`
  ——于是 `StripThinking` 把**整段文本**都截成了空串。它是被 `tests/Reasoning.cs` 的
  假阳性守卫当场抓到的（9 条语料 + 53 个真实 caption 全被改动）。现在标记字面量是
  拼出来的，源码里不存在完整的尖括号标记。
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

**UI Automation 找窗口不能只 `FindFirst` 按 PID 找。** 输入法会把辅助窗口注入宿主进程，
实测撞到一个 `SoPY_Status`（搜狗）窗口排在真正的应用窗口前面，于是后续 `Descendants`
枚举出 0 个元素、报「控件找不到」——而控件其实好好地在界面上。按
`ClassName == 'WinUIDesktopWin32WindowClass'` 挑才稳。顺带：`$TRUE` 是 PowerShell 的
只读自动变量，赋值会报「无法覆盖变量 true」。

**PowerShell 版本决定中文能不能写在脚本里。** 本机 `pwsh` 是 **PowerShell 7.6**，
无 BOM 的 UTF-8 `.ps1` 里的中文正常；但**远程 Windows Server 上用
`powershell -File` 起的是 5.1**，它按 ANSI 读，脚本里的中文会乱码——那种场景
（以及一切交给 5.1 跑的脚本）必须保持纯 ASCII，中文走独立 UTF-8 文件或界面输入。
两种环境混着用时，一律按严格的那条来。

**合成鼠标按键不会移动指针。** `mouse_event(LEFTDOWN)` 只在**光标当前所在处**按下，
它不会去你传的坐标——所以"点添加框"实际点在别处，而脚本里一点异常都看不到。
必须先 `SetCursorPos`，再按下/抬起。

**点完要回读焦点，不能默认点中了。** 窗口刚被提到前台时，第一次点击可能不生效
（应用还没处理完激活），后面的 `Ctrl+A` / `DEL` / `Ctrl+V` 就全打到别处，
`Text` 读回来是空——看起来像"粘贴坏了"，其实只是没点中。用
`AutomationElement.FocusedElement` 确认焦点确实落在目标输入框里，不中就重点。

**窗口不在前台时，合成按键会静静地发给别人。** `SetForegroundWindow` 单独用会被
系统拒绝（实测窗口被 DSH 界面压在下面），此时所有 `SendKeys` / `keybd_event` 都去了
别的窗口，而脚本一路"成功"。做法是 `SetWindowPos(HWND_TOPMOST)` 提上来再抢焦点，
并且**回读 `GetForegroundWindow` 断言**。跑长脚本时人可能会碰一下别的窗口——
那之后的按键全发错窗口，于是把"有人碰了机器"误判成"程序有 bug"，
所以每次输入前都重新确认一次前台并计数。

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


