using System.Globalization;
using System.Text;

namespace AnimaCaptioner.Core;

/// <summary>
/// 给标签里的特殊符号转义——**只为生图**，标注侧绝不能开（见 <see cref="AppSettings.EscapeTagParens"/>）。
///
/// <para><b>为什么只有圆括号需要转义</b>（实测，不是照搬 A1111 习惯）：</para>
///
/// Anima 原生走 ComfyUI 的文本编码路径，而 ComfyUI 的提示词解析器**只认圆括号**：
/// <c>comfy/sd1_clip.py</c> 里 <c>escape_important</c> 只处理 <c>\(</c> <c>\)</c>，
/// <c>parse_parentheses</c> / <c>token_weights</c> 也只跟踪 <c>(</c> <c>)</c> 的配对。
/// <c>[</c> <c>]</c> <c>{</c> <c>}</c> 是**字面量**，原样进编码器——这一点和 A1111 的
/// 习惯不同，所以不能一起转。
///
/// <para>用 Anima 的真实分词器（<c>comfy/text_encoders/anima.py</c> 的 AnimaTokenizer）
/// 逐条实测，编码器实际收到的是：</para>
///
/// <code>
/// hatsune miku (vocaloid)     ->  "hatsune miku vocaloid"     括号被剥掉
/// hatsune miku \(vocaloid\)   ->  "hatsune miku (vocaloid)"   转义后原样
/// 210 (reverse:1999)          ->  "210 reverse" + 权重 1999.0  :1999 被当权重吃掉
/// 00 qan[t]                   ->  "00 qan[t]"                 方括号字面量
/// 009 re:cyborg               ->  "009 re:cyborg"             括号外冒号是字面量
/// @big chungus                ->  "@big chungus"              @ 是画师前缀
/// </code>
///
/// <para><b>影响面</b>：Anima 自带索引 108,257 个标签里 <b>25,815 个含圆括号</b>，
/// 其中 <b>25,813 个（23.84%）</b>会被解析器改写（多出来的 2 个是括号不配对的
/// <c>;(</c> / <c>&gt;:(</c>，原样通过），另有 104 个是灾难性的
/// （<c>(reverse:1999)</c> 那类，冒号后面的年份被当成权重、那个词直接消失）。</para>
///
/// <para><b>有效性</b>：对全部 108,257 个标签实测，<see cref="Escape"/> 之后
/// <b>100.0000% 能精确还原</b>，且不再残留任何误加的权重。</para>
///
/// <para><b>转义不了的三件事</b>（已在文档里说明，别以为开关一开就万事大吉）：</para>
/// <list type="bullet">
///   <item>逗号：Anima 索引里有 27 个标签自带逗号，而 ComfyUI 不认 <c>\,</c>。
///         而且逗号是**本工具自己的标签分隔符**，不是 Anima 的——那是另一个问题。</item>
///   <item>Qwen 特殊 token：<c>&lt;|endoftext|&gt;</c> / <c>&lt;|im_end|&gt;</c> /
///         <c>&lt;|im_start|&gt;</c> 会被当成单个特殊 token，解码成空串。词库里没有，
///         但手输会中招（<c>&lt;|foo|&gt;</c>、<c>&lt;|&gt; &lt;|&gt;</c> 是安全的）。</item>
///   <item>括号不配对的标签（<c>;(</c>、<c>&gt;:)</c> 等 6 个）实测原样通过，不用管。</item>
/// </list>
/// </summary>
public static class TagEscape
{
    /// <summary>
    /// 把标签里的 <c>(</c> <c>)</c> 各自前面补一个反斜杠。**其余字符一律不动。**
    ///
    /// 不需要额外处理反斜杠：ComfyUI 的 <c>escape_important</c> 只吞掉紧跟圆括号的
    /// 那一个反斜杠，所以标签里原本就有的字面反斜杠会自动留下一个。实测 9 个含反斜杠
    /// 的标签（<c>\(^o^)/</c>、<c>\m/</c>）全部精确还原。
    /// </summary>
    public static string Escape(string tag) =>
        tag.Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>
    /// <see cref="Escape"/> 的逆运算：把每个紧跟在 <c>(</c> <c>)</c> 前的反斜杠去掉一个。
    ///
    /// 刻意用单趟扫描而不是两次 <c>Replace</c>：后者在 <c>\\(</c> 这种
    /// "字面反斜杠 + 转义括号"的串上顺序敏感，容易多吞一个反斜杠
    /// （<c>\(^o^)/</c> 正好是这个形状，实测踩得到）。
    /// </summary>
    public static string Unescape(string tag)
    {
        if (tag.IndexOf('\\') < 0) return tag;
        var sb = new StringBuilder(tag.Length);
        for (var i = 0; i < tag.Length; i++)
        {
            if (tag[i] == '\\' && i + 1 < tag.Length && (tag[i + 1] == '(' || tag[i + 1] == ')'))
            {
                sb.Append(tag[i + 1]);
                i++;   // 跳过被转义的那个括号
            }
            else sb.Append(tag[i]);
        }
        return sb.ToString();
    }

    /// <summary>这个标签里有没有需要转义的符号。绝大多数标签都没有，据此跳过开销。</summary>
    public static bool NeedsEscaping(string tag) =>
        tag.IndexOf('(') >= 0 || tag.IndexOf(')') >= 0;

    /// <summary>
    /// 是不是一个**纯权重表达式**，形如 <c>(chibi:2)</c>：整个标签就是一个括号组，
    /// 且组内最后一个冒号后面能解析成数字（判据与 ComfyUI 的 <c>token_weights</c> 一致）。
    ///
    /// 只用于状态栏提示和测试，**不参与"要不要转义"的判定**——因为单看形状判不出来：
    /// <c>210 (reverse:1999)</c> 里 <c>:1999</c> 也是个合法数字，可它是词库里的正经标签。
    /// </summary>
    public static bool IsWeightExpression(string tag)
    {
        if (tag.Length < 4 || tag[0] != '(' || tag[^1] != ')') return false;
        var inner = tag[1..^1];
        var colon = inner.LastIndexOf(':');
        if (colon <= 0) return false;
        return double.TryParse(inner[(colon + 1)..].Trim(),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// 这个标签要不要转义。
    ///
    /// <para>判据是**词库认不认**，而不是"长得像不像权重"。必须这样，理由有两条，
    /// 方向相反：</para>
    /// <list type="bullet">
    ///   <item><c>210 (reverse:1999)</c> 词库认得——它是最需要修的那 104 个之一，
    ///         单看形状却会被误判成权重而放过。</item>
    ///   <item><c>(chibi:2)</c>、<c>(chibi) girl</c> 词库不认——那是用户手写的权重/
    ///         注意力语法，转义掉就把他的意图毁了。</item>
    /// </list>
    ///
    /// <para>代价是：手写进来、词库里没有、又带括号的文本不会被转义（例如
    /// <c>hatsune miku (vocaloid)</c> —— Danbooru 里她本名不带这个后缀，所以词库没有）。
    /// 那种情况 ComfyUI 会剥掉括号，但内容还在，通常无害；而**不会**毁掉权重语法。
    /// 保守这一侧是刻意的。</para>
    /// </summary>
    /// <param name="isKnownTag">词库查询回调（<see cref="TagSections.IsKnownTag"/>）。</param>
    public static bool ShouldEscape(string tag, Func<string, bool> isKnownTag) =>
        NeedsEscaping(tag) && isKnownTag(tag);

    /// <summary>按 <see cref="ShouldEscape"/> 转义一串标签，返回新列表（不改原列表）。</summary>
    public static List<string> EscapeAll(IReadOnlyList<string> tags, Func<string, bool> isKnownTag)
    {
        var outTags = new List<string>(tags.Count);
        foreach (var t in tags)
            outTags.Add(ShouldEscape(t, isKnownTag) ? Escape(t) : t);
        return outTags;
    }

    /// <summary>这一串标签里有多少个真的会被转义（供状态栏提示）。</summary>
    public static int CountEscaped(IReadOnlyList<string> tags, Func<string, bool> isKnownTag)
    {
        var n = 0;
        foreach (var t in tags)
            if (ShouldEscape(t, isKnownTag)) n++;
        return n;
    }
}
