# 提示词：标签 + 自然语言

> 提示词不是一串标签。官方允许标签和自然语言混排，本工具因此把它当成两段来管。

## 官方怎么说的

模型卡里明确允许两者混排：

> "You can mix tags and natural language in arbitrary order."
>
> "If using pure natural langauge, more descriptive is better."

官方工作流模板的正向提示词**就是一整段英文散文、没有任何标签**；官方 `example.png` 那次生成用的是「质量前缀 + 四句散文」，前缀以**句号**收尾：

```text
masterpiece, best quality, score_7, safe. An anime girl wearing a black
tank-top and denim shorts is standing outdoors. She's holding a rectangular
sign out in front of her that reads "ANIMA". She's looking at the viewer with
a smile. The background features some trees and blue sky with clouds.
```

注意那段前缀以**句号**结束，然后接的是完整句子——这就是官方的写法。

## 本工具怎么切分

**句号之前算标签区，句号之后整段算自然语言。**

- 标签区：按大类着色，参与校验，中栏显示
- 自然语言：单独一色，**不参与**标签校验和排序，中栏最下面单独一行标着「不参与标签排序」

保存时两段原样写回，中间的分隔符保持字节不变。

> 为什么按"尾巴"切，而不是逐句判断？
> 因为实测 679 个真实标签里，以句末标点结尾的有 **0 个**、首字母大写的也有 **0 个**——这两个信号在英文散文里却到处都是。用它俩区分既简单又不会误伤标签。逐句判断会把 `She's holding a sign, looking at the viewer` 这类含逗号的句子切碎，碎片看起来很像标签。

## 一个容易踩的坑：别用逗号过渡到散文

**省略句号、直接用逗号接散文，有时也能被认出来——但那是碰巧，不能依赖。**

判据里没有一条在数逗号，起作用的是"这一段像不像句子"（首字母大写 + ≥3 个词 +
词库里没有）。所以：

```text
safe, 1girl, solo, blue eyes, A black cat plush sits on her shoulder    ← 首字母大写、像句子 → 认出来了
safe, 1girl, solo, blue eyes, black cat plush on her shoulder           ← 小写 → 整段被当成标签
```

第二行会变成 **6 个标签**（`black cat plush on her shoulder` 成了其中一个），
于是这段自然语言被当成标签喂给训练。**句号是明确的边界，逗号不是**——
要写自然语言，就把标签区用句号收尾，跟官方那两个例子一样。

## 散文写在最前面时

官方允许任意顺序，但本工具只**保证**"标签在前、散文在后"这一种排布：
散文在最前面时，整段（包括后面的标签）都会被判成散文。

这是取舍，不是疏漏：本工具自己产出的都是"标签 + 尾巴"形态，
而规则可预测比多认一种排布更重要（逐段猜会把 `best quality`、`silver hair`
这类合法多词标签误判成散文）。所以从别处复制来的纯散文提示词，
在中栏会显示成"没有标签、整段自然语言"——那正是它实际的样子。

## 着色

「提示词原文」输入框里的文字会**按大类着色**，和中栏的段颜色对应。打字时实时更新。

着色开关和十个颜色都在**设置 → 标签大类配色**里。

## 送进模型前：括号要转义

标注时标签原样写进 `.txt`；但**拿去生图**时，标签里的圆括号要先转义成 `\(` `\)`，
否则 ComfyUI 会把 `(...)` 当权重语法、连括号带内容一起改掉。
这件事由写标签模式的「转义 ( )」开关自动做，见 [写标签模式](11-write-tag-mode.md)。
