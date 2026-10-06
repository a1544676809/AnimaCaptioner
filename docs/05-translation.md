# 翻译是怎么做的

> 这一栏只做「中文自然语言 → 英文自然语言」，散装标签交给中栏。

## 为什么中文必须翻成英文

Anima 的文本编码器是 **Qwen3-0.6B**。T5-XXL 的**分词器**在跑，但它不加载权重——只负责给出 token 位置，DiT 内部一个 6 层的 adapter 把 Qwen 的隐状态交叉注意力到 T5 词表索引的嵌入上。

而实测 Anima 自带的 T5 分词器：**32100 个 piece 里 CJK 字符数为 0**。51 个常用汉字里 **49 个直接塌成 `<unk>`**，整句中文只有 **33–45%** 的位置是有效 token。

作者本人的说法（HF 讨论区 #52）：

> "T5 wasn't trained on Chinese. The base Cosmos2 model with T5 has no ability to understand Chinese. This, combined with the fact that all captions are in English, is why the model only understands English."

**所以中文必须翻成英文，这不是选择。**

## 翻译流程

1. 从词库给模型一份**中文词 → 英文单词**的对照
2. 模型输出英文自然语言
3. 检查是否残留中文，有则重试

第 1 步不能省。人称词（「少女」「少年」「女孩」…）在整个词库里**没有条目**——它们对应的标签是 `1girl` 这类数量词，不是词对词翻译。不给译法，8B 会稳定产出：

```text
A silver-haired少女 stands in the cherry blossoms...
```

「少女」被当成名字的一部分不翻了。而这句话在 T5 里等于没写。

> 实测对照：笼统要求"全部用英文"**无效**、降 temperature **无效**、**点名那几个字并给出译法有效**。加了对照后，同样 4 段中文从 2/4 残留降到 **0/4**。

## 追加还是替换

- **默认**：译文追加到提示词末尾（官方写法：标签 + 句号 + 散文）
- 勾选「替换已有散文」：只替换掉原来那段散文，标签不动

翻译后可以用「工具 → 撤销上次翻译」回退。

## 提示词长度

512 是**下限不是上限**——Anima 的代码是 `if out.shape[1] < 512: pad(...)`，两个分词器都是 `max_length=99999999`，从不截断。

但实际可用长度约 **2400 字符 / 400 词**（约 512 个 T5 token）。官方自己的 caption 变体都是 T5 形状的，超出这个范围属于训练分布之外。

## 模型补译（标签的中文释义）

中栏标签右栏的中文来自词库。**词库不是全集**——实测你自己的 53 个 caption 里，扣掉控制词，还有约 **93 个不同标签（147 个实例）**查不到中文，主要两类：

- **Anima 原生标签**：`cat smile`、`ass grab`、`topless`、`vertical stripes`（Danbooru 上没有，Anima 索引里有）
- **颜色+名词的复合词**：`white back bow`、`white maid headdress`、`red and white striped`

这些可以右键 → **「模型翻译（补中文释义）」**让模型补一个（快捷键 `Ctrl+Shift+T`）。用法要点：

- **只对缺中文的标签生效**。词库已有官方释义时该项会变灰——再让模型译一遍只会多一个可能更差的版本。
- **模型自己决定要不要联网查**。它知道就直接答（约 5 token），不确定才发起 `web_search`（约 487 token）。一律强制先搜反而更慢、更容易被无关结果带偏。
- **结果是推断，不是官方释义**。界面上用橙色 + `≈` 前缀 + 「模型译 · 非官方」标出，**不写回词库、不覆盖官方值**。
- 模型不确定时会**弃权**（答 `UNKNOWN`），不留半条记录。这是刻意的：错译会污染训练集，宁可少一条。
- 结果缓存在 `%LocalAppData%\AnimaCaptioner\model-gloss.json`，重开程序仍有效。

### 联网搜索的两个坑

搜索走 `192.168.1.4` 上已部署的网关（Anthropic 兼容协议 + 自建 SearXNG）。接它的时候踩了两个**静默失败**，都值得记下来：

1. **TLS SNI 必须覆盖成证书域名**。Caddy 按 SNI 选站点，直连 IP 时 SNI 是那个 IP，Caddy 找不到站点，回的是 `TLS alert: InternalError`。注意 `HttpClientHandler.SslOptions.TargetHost` **不管用**（实测无效），必须自己建 `SslStream` 并 `AuthenticateAsClientAsync(TargetHost = ...)`。
2. **`Host` 头也必须写成同一个域名**。用 IP 当 Host 时 Caddy 返回 `200 OK` + `Content-Length: 0`——**一个既不报错也没内容的空响应**，排查起来毫无线索。代码里对空响应专门给了一句提示。

另外：**搜索这一路即使全局关着思考链也会保留思考**。实测关掉 `enable_thinking` 后模型根本不发出 `tool_calls`，只把标签原样吐回来，搜索等于失效；而那个全局开关是给散文翻译设的（那边思考纯属浪费 token）。两件事的正确取值相反，所以工具循环不跟全局走。
