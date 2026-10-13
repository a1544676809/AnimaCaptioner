# 图像区域工具（方案，**尚未实现**）

> 这份是**设计稿**，不是已实现的东西。写下来是因为它牵动 Core 的消息构造，
> 想清楚了再动手比边写边改便宜。

## 要解决什么

整图被缩到长边 1024 再发给模型（实测 4000×6500 → 630×1024），细节全糊。
裁一块区域再缩到 1024，模型能看清的信息量是完全不同的量级。所以给助手一个
"把某个区域放大给我看"的工具。

## 工具

名字 `view_region`，一个工具两种模式（`mode` 参数）：

| 模式 | 参数 | 行为 |
|---|---|---|
| `crop` | `x, y, w, h` | 裁一块 |
| `tiles` | `cols, rows` | 按网格均分裁成多块 |

- **坐标一律用归一化值（0–1），不用像素。** 模型看不到原始分辨率，让它填像素
  等于让它猜；归一化之后 `x=0.4,w=0.2` 这种它推得出来。
- `tiles` 的块数**要封顶**：每张图约 150 KB、还各自算 token，实测 4×4=16 张足以
  把上下文顶爆。上限 9（3×3）并在工具说明里写明"块越多越贵"。
- 不传 `image`（或传 `current`）默认用当前那张。

## 消息构造要改的地方（这是真正的成本）

现在两条硬约束：

```csharp
// 1. 图只挂在"最后一条 user 消息"上
messages.Add(turn.FromUser ? UserMessage(turn.Text, isLast ? image : null) : ...);
// 2. 工具结果只能是文本
messages.Add(ToolMessage(call.Id, text));
```

所以"把裁好的图给模型看"现在走不通。**采用方案 A**：裁完之后往 `messages` 里
插一条**带图的 user 消息**，工具本身只回一句文字确认。要加的是：

1. `LlmClient.UserMessage(string text, IReadOnlyList<ChatImage> images)` 的**多图重载**
   （现有那个只支持一张，parts 形状一样，只是多个 image_url 项）。
2. `TagAgent.IHost` 加一个方法，返回裁剪结果：
   ```csharp
   (bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images) ViewRegion(string arguments);
   ```
3. `TagAgent` 在 `view_region` 分支里：调它 → 把 `Images` 插成一条 user 消息
   （文案写清"这是你要求放大的区域，坐标为 …"）→ 工具结果回那句确认。

## 裁剪放哪一层

**必须在 UI 侧**（`AgentHost`/MainWindow），不能在 Core：Core 不引用任何图像库
（现在一个都没有，`ImagePayload` 在 UI 侧用 WIC）。流程：

1. 取当前图片的原始字节（已有 `ImagePayload.LoadAsync`）；
2. 按归一化坐标算像素矩形（**注意 EXIF 方向**：`ImageSizing.Oriented` 那套必须
   先应用，否则竖拍图裁出来是转 90° 的）；
3. 裁 + 缩到长边 1024（复用 `ImageSizing.Fit`）；
4. 编成 JPEG（`ImagePayload` 那条路已经在做，复用它的编码参数）。

**用完即焚**：图只活在这一轮的 `messages` 里，不落盘、不缓存。下一轮模型要看
就再调一次——这正是用户要的语义（"需要再裁"）。

## 先验证再写

动手前先拿真实 key 实测一件事：**在带工具的多轮对话里插入一条额外的图文 user
消息，模型到底看不看得见那张图。** 有的网关会把这种消息当成"用户新说的话"、
有的干脆忽略图片部分。**不验证就写，很容易做出一个"看着像实现了、模型其实没
看见图"的东西**——症状是它照样回答，只是答得和没给图一样，最难分辨。

## 另一个工具：图像元信息

`image_info`：返回尺寸、EXIF 方向、文件大小、格式。**纯读、纯文本，没有上面那些
成本**，可以随便先做。前提代码都现成（`ImageSizing.Oriented/Fit`）。
