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

## 着色

「提示词原文」输入框里的文字会**按大类着色**，和中栏的段颜色对应。打字时实时更新。

着色开关和七个颜色都在**设置 → 标签大类配色**里。

> 一个实测教训：清空底色不能把颜色的 alpha 设成 0——RichEdit 会当成不透明黑，正文直接变成黑底白字。所以底色用的是按主题背景算出的**不透明**淡色。
