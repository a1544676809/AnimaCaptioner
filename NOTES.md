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
- **它自己的原生撤销会被"改色"污染。** 实测：往空框里打 `alpha beta` 再按 `Ctrl+Z`，
  前两次按下去界面上什么都没变，第 3 次才退成 `alpha bet`——**每退一个字符要按两三次**。
  原因是 `ApplyPromptColors` 的 `CharacterFormat` 改动也被 RichEdit 记进了同一个撤销栈，
  `Ctrl+Z` 先退掉那些看不见的格式操作。
  这是**既有行为**（早于撤销/重做功能），不是新引入的；`Windows.UI.Text.ITextDocument`
  没有"暂停记录撤销"的 API，所以没在那一版里处理。要修的话方向是给改色套
  `BeginEditCollection`/`EndEditCollection`（把格式改动并进同一个撤销单元），
  但没验证过副作用。
- **它会先吃掉 `Ctrl+Z` 并置 Handled。** 把 `Ctrl+Z` 挂在 `RootGrid` 上当加速键、
  回调里再判断"该不该我管"是**不生效的**——探针日志在提示词框里一行都没打。
  换句话说"焦点在输入框里就归输入框"是框架保证的，自己那层判断只是第二道防线。

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
- **`AutoSuggestBox` 内部的 `TextBox` 会吞掉 `Ctrl+Z`，冒泡阶段收不到。**
  给「添加标签」框加 `KeyDown="AddTagBox_KeyDown"` 处理 `Ctrl+Z` 是无效的——事件在
  内部那个 `TextBox` 就被处理并标记 Handled 了，压根不上浮。必须用
  `PreviewKeyDown`（隧道，从根往下先经过外层）。`Tab` 能走冒泡那条路是因为
  焦点切换不由内部 `TextBox` 处理。
  同一处的判据也必须挑对时机：**只在框里空着时**才把 `Ctrl+Z` 让给标签历史，
  框里有字时归输入框自己（那是用户在改刚打的字）。
- **UIA `SetFocus()` 在容器元素上会抛「目标元素无法接收焦点」。** `AutoSuggestBox`、
  `ListView` 都是这样，能接收键盘焦点的是它们的子元素（内部 `Edit`、`ListItem`）。
  写自动化脚本时要先试元素本身、失败再往下找 `Edit`/`ListItem`，否则一整条
  键盘驱动的用例会全部失败，而失败原因看着像产品缺陷。
- **UIA 看不到滚动视口之外的元素。** 设置对话框的内容高约 1790 DIP、视口只有 420 DIP，
  放在下半部分的控件在 `TreeWalker` 里根本不存在（`FindFirst` 返回 null），
  截图里也看不到。要断言它得先用 `ScrollPattern.SetScrollPercent` 滚过去。
  这一条排查起来很像"控件没生成"。

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
- **改名后必须点名"被改的那一行"，否则三样都会坏。** 实测 `ApplyTagRename`
  走的是裸 `CommitPrompt()`（`keepVisibleIndex = -1`），日志一次说全：
  ```
  RDBG before: offset=1089 keep=-1 sel=[closed mouth]   ← 位置记对了
  RDBG after:  offset=0    keep=-1                      ← 但掉回顶部
  ```
  三处后果：(1) 位置没人放回去 → 回顶部；(2) **选中丢在别的行**——`_tagRows.Clear()`
  换掉的是一批**全新的 `TagItem` 实例**，原选中对象已被丢弃，`ListView` 的选中随之
  变空；(3) 改名可能跨段（`medium breasts` → 别的段），行被重排后移出视口。
  改法是复用上移/下移那条已验证的路径：`keepVisibleIndex` 传**新名字在新序列里的
  下标**（`Canonicalize` 会重排，所以不能沿用改名前的 `i`），并让 `RefreshTagRows`
  按**标签名**把重建前的选中重新套回新行对象上。
- **`ScrollIntoView` 和重建后的位置恢复不能在同一个流程里各做一次。**
  `ScrollIntoView` 是**同步**的，而 `RestoreTagScroll` 隔两跳才落地，两者时间上错开：
  先滚到目标，几十毫秒后又被恢复逻辑覆盖（旧值），或者反过来把该跟随的行带偏。
  所以 `SelectTagRow` 加了 `alsoFollow`：需要"跟着这一行走"的场景
  （改名、跨段归位）只负责**选中**，滚动统一交给 `keepVisibleIndex`。

---

## 模式切换（写标签模式）

两个模式共用同一份控件与代码，所以坑都出在"按模式分派"这件事上。

- **不要为了分派菜单而建两套 `MenuBar`。** 两套会把 `Ctrl+S` 这类加速键各挂一份，
  按一下触发两次（对"保存"是幂等的所以看不出来，对"复制并入库"就会把使用次数加两次）。
  正确做法是一套菜单 + 按模式切 `Visibility`。顺带一条实测：
  `AddAccel` 挂在 `RootGrid` 上的加速键与菜单项自己的 `KeyboardAccelerator`
  **不会**双双触发——先处理的那个 `Handled = true` 就截住了。
- **`KeyboardAcceleratorTextOverride` 只是文字，它不会接线。** 写了一个没挂
  `KeyboardAccelerator` 的提示（「保存到提示词库」曾标着 `Ctrl+Shift+S`），
  用户按下去毫无反应，而菜单里明明写着。而这个组合键已经被「规整大类顺序」真占了。
  写之前先确认这个键**真的**接上了，否则不如不写。
- **`RadioMenuFlyoutItem` 的勾选不会自己跟着模式走。** 从 `Ctrl+M` 切过去时，
  菜单还是旧的勾。所以 `ApplyModeChrome` 里要显式设两边的 `IsChecked`。
  界面外壳（标题、菜单可见性、主操作文案、面板）必须**收敛到一个方法**里，
  否则"启动时恢复模式"那条路会漏掉其中几项——表现就是标题写着标注、界面是写标签。
- **启动时恢复模式不能顺手"保存当前"。** 那条路在标注侧会走 `SaveCurrent`，
  于是启动后立刻凭空建出一个空 `.txt`。同理，模式切换本身也不该产生文件副作用。
- **"当前操作对象"必须显式化，否则两个模式会互相踩。** 原来标签操作的门禁是
  `_index < 0`（有没有选中图片），而写标签模式没有图片但有文档。把它换成
  `_doc is null`（有没有打开文档）之后，中栏和右栏**一行都不用改**就通用了——
  它们本来就不读当前图片，只吃标签列表 + 词库 + 配置。
  反过来说，真正的耦合点只有三处：从哪读、写回哪、以及那两道守卫。
- **状态栏的数字必须同源。** 左栏列表行和底部状态栏都显示"几个 tag"，曾经一处走
  `PromptText.Parse`、另一处用"含空格的逗号段不算标签"来猜散文边界，于是
  `best quality` / `silver hair` 这类**合法的多词标签**被漏数，同一屏上左边写
  `3 tags`、右边写 `4 tags`。散文边界只有解析器判得准（它看句边界与已知标签），
  按空格猜一定会错。现在两处都走 `PromptText.Summarize`。
- **分栏比例要按模式各存一套。** 两个模式的分栏语义不同（图片/标签/提示词 vs
  提示词库/标签/提示词），共用一组值会让一个模式里拖过的布局把另一个模式改掉。
- **库列表的选中必须在列表重建之后设。** `OpenDraft` 要在行集合里按 id 找到当前那条
  才能标上选中态；顺序反了就会"载入了提示词但左栏一行都没高亮"。
  直接以写标签模式启动时最容易漏——那条路上没有别的代码会构建这个列表。
- **占位标题不能当标题传下去。** 新建草稿的显示名是「未命名草稿」，若入库时把它
  当标题传给 `Update`，一条正文很具体的提示词会永远叫这个名字（而派生标题本来
  已经算好了）。所以 `DraftDoc` 用 `HasCustomTitle` 区分"用户起的名字"和"占位串"。

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

### `/models` 的能力字段，以及一个被静默忽略的开关

**"这个模型吃不吃图"有确定答案，不用猜、也不用发试探请求。** 但那些字段是
**扩展**：标准 OpenAI 的 `/models` 只保证 `id`，所以解析一律"有就取、没有就留空"。
实测 DeepSeek（`api.deepseek.com`，2026-10）回的是：

```json
{"id":"deepseek-flash","name":"DeepSeek-V4.1-Flash","context_window":1048576,
 "max_output_tokens":393216,"input_modalities":["text","image"],
 "effort":{"supported_levels":["low","high","max"],"default_level":"high"}}
```

同一份响应里 `deepseek-v4-pro` 的 `input_modalities` 只有 `["text"]`——两个模型的
差别就这样摆着，不需要用户填、也不需要程序去试。原样字节存在 `tests/Models.cs`
的第 7 节，改动解析就会被它挡下。

一个被测试抓到的细节：**判"`name` 是不是就是 id"必须用 Ordinal。** 一开始用了
忽略大小写的比较，于是 `DeepSeek-V4-Pro` 被判成"和 `deepseek-v4-pro` 相同"、
显示名被丢掉。而忽略大小写的本意（`{"models":[{"name":"llama3"}]}` 那种网关里
`name` 就是 id）用**逐字符相等**就能满足。

**`chat_template_kwargs.enable_thinking=false` 在云服务上被静默忽略。** 代码注释
原来写的是"不下发以免云端服务 400"，实测推翻了这个担心，而真正的问题更隐蔽：
DeepSeek 照收这个字段、回 **200**，然后什么都不做。同端点、同一条提问、同
`max_tokens` 的 A/B：

| 请求体 | 返回的 `reasoning_content` |
|---|---|
| 只有 `chat_template_kwargs` | **69 字符**（思考照常发生） |
| 加上 `reasoning_effort: "none"` | 0 |

也就是说这个开关在云服务上一直是**空转**的：用户以为省了 token，实际每次都在
为思考付费。`corecheck llm` 那个探针就是把这个 A/B 固定下来，换端点时可以复跑。

**`reasoning_effort` 不能瞎填。** 服务端严格校验，认不得的值直接 422，并在错误
里列出全部合法值：`none/minimal/low/medium/high/xhigh/ultra/max`。而 `/models` 报的
`supported_levels` 是**该模型支持的子集**（flash 只报 `low/high/max`）——所以按
它发，别硬编码一份全局清单。

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
- **候选弹层是独立的顶层窗口，宿主走了它不会自己关。** 弹层是 `AutoSuggestBox`
  模板里的 `Popup`，WinUI 3 把它放进自己的窗口
  （`Microsoft.UI.Content.PopupWindowSiteBridge`）。宿主被移出可视树（列表重建
  换掉整行、虚拟化回收容器）或折叠之后，那个窗口**不会被隐藏**，于是留下一个
  看不见、却仍然吃掉鼠标的孤儿窗口——用户看到的是"标签列表某一片区域点不动、
  鼠标移上去也没反应"。实测 2026-10-10 那次会话里留下三个，最久的一个存活
  6 分钟，`WindowFromPoint` 命中的是弹层而不是列表。
  所以凡是"丢弃 / 折叠弹层宿主"的地方（`EndEdit`、`AbandonEdit`、容器回收）
  都要先收弹层；而且**不能只设 `IsSuggestionListOpen = false`**——那是控件级
  属性，弹层展开过程中的中间态会吃掉这一下，还得把模板里那个 `Popup` 的
  `IsOpen` 直接置 false（它收的是窗口本身，不看控件状态机），并补一跳延后
  再收一次。竞态很窄（看着只在弹层**首次展开**那一瞬），但残留窗口一旦出现
  就不会自己消失，只能重启程序。

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

**"某片区域点不动"先查有没有孤儿弹层。** 枚举该进程名下所有
`Microsoft.UI.Content.PopupWindowSiteBridge` 顶层窗口，看有没有 `IsWindowVisible`
却早就该关掉的；再拿 `WindowFromPoint` 打目标点——命中的是弹层就实锤了
（`IsWindowVisible` 和"能不能收到鼠标"是两件事，要分别验）。

一个反直觉的坑：**把窗口提到前台会顺手收掉一部分残留弹层**（实测三个里收掉两个），
所以必须在"刚复现、还没切窗口"的时候查，否则会误判成"已经好了"。同理，判断
"修复有没有生效"不能只靠肉眼，要用 `EnumWindows` 数窗口。

自动化驱动 GUI 还有两条：**目标窗口会被别的窗口盖住**，点击和按键就全落到盖住它的
那个窗口上——先 `SetWindowPos(HWND_TOPMOST)` 再操作，否则会得到"点了没反应"的假象；
以及**用 `-Force` 强杀进程，草稿不会落盘**，要测"启动时就已存在的状态"得发
`WM_CLOSE` 正常关闭。

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
另一条能用的路子不必把窗口设成 TOPMOST：先
`AttachThreadInput(自己线程, 前台窗口线程, true)`，再 `BringWindowToTop` +
`SetForegroundWindow`，然后立刻 `Detach`——实测 `GetForegroundWindow` 回读即为真，
随后 `Ctrl+S` 一次命中；而只调 `SetForegroundWindow` 时剪贴板压根没变、脚本却一路"成功"。

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

## 撤销历史按文档分桶：共用键与"外部改过"两个坑

背景与设计见 `docs/dev/architecture.md`（"撤销历史为什么按文档身份分桶"）。这里只记
实测踩到的三件事。

- **`draft:(new)` 是个共用键。** 所有尚未入库的新草稿身份相同，所以首次入库拿到 id 时
  必须把历史 `Migrate` 过去。不搬的后果不是"少一点历史"，而是**下一份全新草稿继承上一份
  的撤销栈**——按 `Ctrl+Z` 会把别人的标签退进来。`OpenDraft(null)` 还要清掉共用键上的
  残留（被放弃、从未入库的那一份），但判据里必须排除"当前文档就是新草稿"，否则会把
  **正在编辑**的那份清掉。
- **`OpenDraft` 回读库正文用的是裸 `Parse`，这是个真缺陷（已修）。** 库里存的是线上格式
  （带 `\(`），不还原的话标签列表会显示 `210 \(reverse:1999\)`。它还正好会把按身份分桶的
  一致性校验带偏：离开时记的是字面量、重开时解析出带反斜杠的版本 → 被误判成"外部改过"
  → 历史被丢掉。改成走 `ParseCaption`。
- **一致性校验只比"离开那一刻"。** 不必在编辑过程中反复记：盘上 / 库里的内容正是离开
  那一刻写下去的，它就是唯一正确的基线。位置也必须卡在 `SaveCurrent` **之后**、
  `LoadCurrent` 覆盖 `_pendingTags` **之前**——各调用方（Next / Prev / JumpFirst）都是
  先存再换。散文尾巴按 `Trim` 比较：草稿入库时正文会 Trim，重新解析回来差一个尾随空白
  不该算"外部改过"。
- **`Attach` 丢弃历史时保留桶。** 桶还属于这份文档，下次编辑就接着用；而且丢掉时记了
  `history dropped (content changed outside)`——不留日志的话，用户只会看到"撤销怎么没了"，
  无从判断是外部改了文件还是程序把它弄丢了。

## 自动化测这个程序踩到的坑（GUI 冒烟）

- **加标签框的 `Enter` 不由程序处理。** 它走 `AutoSuggestBox` 自己的 `QuerySubmitted`
  （`AddTagBox_KeyDown` 只接 `Tab`，注释里写明了理由）。而候选弹层是**异步创建**的独立
  窗口（见前面孤儿弹层那一节），所以聚焦后第一次 `Enter` 可能被吞掉——脚本必须重试，
  并以日志里的**精确标签名**确认（`add tag: <tag> ` 带尾随空格，才能把 `smile` 和
  `smilesilver hair` 区分开；实测第一次失败时两次输入被拼成了一个词）。
- **`SetFocus()` 聚焦 `AutoSuggestBox` 容器不等于聚焦内部 `TextBox`。** 要找到它
  `ControlType=Edit` 的后代再 `SetFocus()`。
- **`SendKeys` 会在加标签框里丢掉空格**：实测 `silver hair` 变成了 `silverhair`
  （`via=` 为空、词库不认识它）。自动化里改用单词标签。
- **`ListViewItem` 的 UIA Name 是数据对象的 `ToString()`**（这里是
  `AnimaCaptioner.Core.PromptLibRow`），拿不到标题——要按行内 `TextBlock` 的文本找。
- **`prompts.json` 是一个 JSON 数组**（`List<Entry>`），不是 `{Items:[...]}`。
- **读正文前先清剪贴板。** 否则 `Ctrl+C` 没生效时 `Get-Clipboard` 会返回上一次的旧值，
  断言可能**假通过**（实测踩到：一条撤销断言"通过"了，其实读的是同一段旧内容）。
- **日志可以当断言依据**：`Log.Write` 是 `File.AppendAllText`，没有缓冲，所以"日志里没有
  这一行"就等于"这件事没发生"。但**产物过期**会造成同样的现象——我给 `Migrate` 加了一行
  日志却忘了重新构建，冒烟就以"迁移没发生"失败了，白查了半天。加日志后先构建再跑。
- **用菜单项触发撤销比键盘可靠。** 菜单项不依赖焦点（`InvokePattern.Invoke()`），而且它的
  文案与 `IsEnabled` 直接暴露了 `CanUndo` / `UndoLabel`——把菜单展开读一遍就能确认
  "这一刻到底有没有可撤销的东西"，比按 `Ctrl+Z` 再看日志稳得多。
- **`ConvertFrom-Json` 出来的对象上"没有"的字段不能直接赋值。** 旧 `settings.json` 里
  没有 `AgentEnabled` / `AgentAttachImage`（它们是这次新加的），脚本里
  `$json.AgentEnabled = $true` 会抛 `SetValueInvocationException`，而
  PowerShell 的**非终止错误**默认不会打断脚本——于是配置根本没改成，后面十几条断言
  全都在"面板没开"的前提下跑，看起来像功能全坏了。要用
  `Add-Member -NotePropertyName ... -Force`。这条也提醒：冒烟脚本自己要检查
  "前提条件真的成立了吗"（`AgentInput` 在不在 UIA 树里），而不是直接开始点。

## AI 助手（读图 / 提案）踩到的坑

- **`Trim` 只剥一次是错的。** 模型会写 `` - `1girl` `` 或 `* solo*`，而 `Trim(chars)`
  遇到不在集合里的字符就停：先剥掉连字符后被**空格**挡住，前导反引号留了下来，于是
  标签变成 `` `1girl ``。要多轮剥（空格和标点交替），`tests/Agent.cs` 第 4 节钉着这条。
- **`BitmapEncoder` 没有 `SetQuality`。** JPEG 质量走
  `encoder.BitmapProperties.SetPropertiesAsync({"ImageQuality": ...})`，而且必须在
  `SetSoftwareBitmap` **之前**设。`ImageQuality` 的值类型是 `PropertyType.Single`。
- **EXIF 方向 5–8 时解码器报的宽高是转置的。** 不管它长边就落在错的那一维上
  （竖图会按宽度去缩）。`ImageSizing.Oriented` 负责换，`Fit` 再缩。
- **图片 token 的实测口径**（deepseek-v4.1-flash）：约等于像素数 / 1600，且每张图有
  一个约 200 token 的**底**。所以小图并不便宜，长边 1024 是"够看清"和"不烧 token"
  的折中；1024×1024 约 850 token。
- **`"tools": []` 不能发。** 收尾那一次"不给工具"必须**不带这个字段**：有些服务会把
  空数组当成"要求调用工具但没有工具可用"而报错。
- **收尾请求复用同一个 messages 列表**（只把工具清空），不要另起一条纯文本请求——
  那会把第一轮的图丢掉，而读图正是最容易把轮数用完的场景。
- **滚动要补一跳。** 卡片是刚 `Add` 进去的，第一跳时布局还没算完，`ScrollableHeight`
  读到的还是旧值，结果停在上一条、最新那张提案卡片正好在视野外。隔 150 ms 再滚一次。
- **写标签模式没有图。** `AgentImageBox` 会自动灰掉，判据是
  `_mode == Label && _index` 有效——`CurrentImagePath()` 一处收敛，别在别处再判断一次。


