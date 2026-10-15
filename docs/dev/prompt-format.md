# Anima 的提示词格式：标签与自然语言之间是什么

> 起因是一个观察：「自然语言和普通提示词之间好像也是用逗号分隔的，只是自然语言
> 永远在最后面。」**前半句不成立、后半句不是官方要求**——两件事都要说清，
> 因为它们各自对应着代码里的一个决定。
>
> 依据是官方 model card 的 *Prompting* 一节
> （[circlestone-labs/Anima](https://huggingface.co/circlestone-labs/Anima)，
> 国内直连不了时走镜像/代理；本机抓下来的原文可用于复跑，见文末）。

## 官方怎么写的（逐字）

| 官方原文 | 含义 |
|---|---|
| `Recommended positive prefix: "masterpiece, best quality, score_7, safe, "` | 纯标签串**逗号分隔**，以逗号收尾 |
| `"masterpiece, best quality, @big chungus. An anime girl with medium-length blonde hair is..."` | 标签与自然语言之间是**句号**（`. `） |
| `You can mix tags and natural language in arbitrary order.` | **自然语言不必在最后面** |
| `You can put quality / artist tags at the beginning of a natural language prompt.` | 标签也可以在散文前面 |
| `## Full tag example` | 一整串纯标签，全用逗号 |

所以：

- **逗号是标签之间的分隔符**（前半句对的这一半）。
- **标签与自然语言之间不是逗号，是句边界**（`.`/`!`/`?`/`;`）。
  官方两个例子都是 `@big chungus. An anime girl …`。
- **自然语言"永远在最后面"不是官方的规定**，是本程序的取舍（见下）。

## 但程序**实际**怎么判：不看标点，看"像不像句子"

这一点比上面更重要，也是实测才发现的（`corecheck separator`）。同一段内容、
交界处一个用句号一个用逗号，解析结果**完全一样**：

```
safe, 1girl, solo, blue eyes. A black cat plush sits on her shoulder, and a small …
safe, 1girl, solo, blue eyes, A black cat plush sits on her shoulder, and a small …
        ↓ 两者都得到
tags = [safe | 1girl | solo | blue eyes]   散文 = A black cat plush sits …
```

原因在 `PromptText.ProseStart` 的三条判据，**没有一条在数逗号**：

1. 段内有 `. ` 这类句边界，且**句号之前那截是已知标签**（官方 `safe. An anime girl…`
   正是这种：`safe` 是标签、句号是标签区的终结符）；
2. 段以句末标点收尾且去掉标点后是已知标签（`@big chungus.`）；
3. 段**首字母大写、≥3 个词、且不是已知标签** —— 读起来就是句子。

第 3 条才是那个把逗号写法也救回来的东西：`A black cat plush sits on her shoulder`
首字母大写、词数够、词库里没有，于是判成散文。

**反证**（同一个探针）：把小写、不像句子的那段放在逗号后面，它立刻被当成标签：

```
safe, 1girl, solo, blue eyes, black cat plush on her shoulder, plain white backdrop
        ↓
6 个标签，没有散文     ← 逗号不具备边界语义，六段全算标签
```

**这对写 caption 意味着什么**：逗号写法"能用"是**碰巧**——依赖散文恰好像句子。
一旦那段以小写开头、或不到三个词（`a small black cat` 只有四个词、还小写），
整段就会被当成标签塞进训练数据。**要写自然语言就用句号收尾标签区**，
这也正是官方两个例子的写法。

## 本程序的取舍：只支持"标签区 + 尾巴"

官方允许任意顺序，本程序**只保证**"标签在前、自然语言在后"这一种。
理由在 `PromptText` 的类注释里：连续尾巴既是官方推荐写法（质量前缀 + 散文）
的产物，也是本程序"把英文描述追加到末尾"的产物，所以规则可预测；
逐段启发式猜测则会把合法的多词标签（`best quality`、`silver hair`）误判成散文。

代价要说清：散文在**最前面**时（官方允许），第 3 条判据从第 0 个字就命中，
于是后面的标签也一并被吞进散文——`tags=0`，整段成了散文。

## 顺带修掉的一个真缺陷：散文在前时加标签会把词粘起来

上面那个取舍平时不影响使用（本程序生成的 caption 都是尾巴式），但**碰得到**：
用户手工写的散文式 caption、或从官方工作流复制来的提示词。

实测（这次是探针先发现、`prose` 套件补上回归）：

```
原文：An anime girl with blonde twintails stands in a white room. 1girl, solo, smile
加一个标签 masterpiece 之后：
     masterpieceAn anime girl with blonde twintails stands in a white room. …   ← 粘成一个词
```

原因是 `Rebuild` 假定尾巴自带前导分隔符（正常尾巴是 `. ` 或 ` `），
而散文在开头时 `ProseStart` 返回 0、尾巴一个分隔符都没有，于是 `head + tail`
直接拼死。**这是静默的**：标签列表看不出异常，训练时读到一个不存在的词
加上一段被毁掉的散文。

修法：尾巴首字符不是标点也不是空白时，补上标签区的分隔符（默认 `", "`）。
对正常尾巴**逐字节不变**（有断言钉着：`1girl` + `. She is smiling.` 仍得
`1girl. She is smiling.`）。

## 怎么复跑

```powershell
# 官方 model card（国内直连不了，要代理或镜像）
$env:HTTPS_PROXY='http://127.0.0.1:10808'
Invoke-WebRequest 'https://huggingface.co/circlestone-labs/Anima/raw/main/README.md' `
  -OutFile "$env:TEMP\anima-card.md"

# 探针：官方依据（逐字核对）+ 解析行为 + 粘词缺陷
tests\bin\Release\net10.0\corecheck.exe separator "$env:TEMP\anima-card.md" `
  "$env:AC_VOCABDB" "$env:AC_SEED"
```

**词库不能省**：交界判据第 1 条要求"句号前那截是**已知标签**"，
没有词库时 `safe` 不是已知标签，整条规则不成立——第一次跑就因为这个
测出了假行为（两种写法都判成 4 标签 0 散文），差点得出错误结论。

默认套件里对应的回归在 `corecheck prose` 第 9b 节（不需要网络）。
