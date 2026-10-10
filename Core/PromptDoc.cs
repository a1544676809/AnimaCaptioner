namespace AnimaCaptioner.Core;

/// <summary>
/// 两种模式。它们共用中栏（标签装配）与右栏（提示词原文 + 翻译器），
/// 只有左栏、菜单分派和"主操作"不同。
///
/// 为什么不用两个 exe：中栏/右栏所需的全部能力（词库校验、大类分段、
/// Tab 补全、标签搜索、中英对照、翻译、配色、提示词解析着色）都在 Core 里，
/// 且**本来就不读当前图片**。拆成两个 exe 只会把这几千行复制一份或另抽一个库。
/// </summary>
public enum PromptMode
{
    /// <summary>标注模式：编辑训练集里某张图的 caption，主操作是写回同名 .txt。</summary>
    Label,

    /// <summary>写标签模式：从零组装一段提示词供生图用，主操作是复制到剪贴板。</summary>
    Draft,
}

/// <summary>
/// 当前正在编辑的提示词**文档身份**。
///
/// 刻意只表示"现在编辑的是哪一个"，**不含内容**：内容始终以提示词框
/// （CaptionBox）为准，<c>_pendingTags</c> / <c>_proseTail</c> 是它派生出来的缓存。
/// 这是既有的单一真源，文档对象如果也存一份标签列表，两者迟早会漂移——
/// 而漂移的表现是"保存下去的不是界面上看到的"。
///
/// 有了它，标签操作的门禁从"有没有选中图片"（<c>_index &lt; 0</c>）变成
/// "有没有打开文档"（<c>_doc is null</c>）。这正是写标签模式需要的语义：
/// 那边根本没有图片，但有文档。
/// </summary>
public abstract class PromptDoc
{
    public abstract PromptMode Mode { get; }

    /// <summary>左栏列表里显示的主标题。</summary>
    public abstract string DisplayName { get; }

    /// <summary>用于日志与去重的稳定标识。</summary>
    public abstract string Identity { get; }

    /// <summary>主操作的名字。菜单项与按钮文案按它分派。</summary>
    public abstract string PrimaryActionText { get; }
}

/// <summary>
/// 标注模式下的文档：训练集里的一张图，内容落在同名的 .txt 上。
/// </summary>
public sealed class LabelDoc : PromptDoc
{
    public required DatasetItem Item { get; init; }

    /// <summary>在整个数据集里的序号，供上一张/下一张与状态栏使用。</summary>
    public required int Index { get; init; }

    public override PromptMode Mode => PromptMode.Label;
    public override string DisplayName => System.IO.Path.GetFileName(Item.ImagePath);
    public override string Identity => Item.ImagePath;
    public override string PrimaryActionText => "保存";
}

/// <summary>
/// 写标签模式下的文档：一段内存里的提示词草稿，没有文件路径。
///
/// <see cref="Id"/> 为空表示这是一份**还没进历史库的新草稿**——用户第一次
/// 复制或切换时它才被写进 PromptLibraryStore。
/// </summary>
public sealed class DraftDoc : PromptDoc
{
    /// <summary>历史库里的条目 id；空串表示尚未入库。</summary>
    public string Id { get; set; } = "";

    /// <summary>新建草稿时的占位名。它不是用户起的名字。</summary>
    public const string DefaultTitle = "未命名草稿";

    /// <summary>显示名。新建草稿时是占位名，入库后由库里的标题决定。</summary>
    public string Title { get; set; } = DefaultTitle;

    /// <summary>
    /// 用户是否**显式**命名过这条草稿。
    ///
    /// 必须区分"占位名"和"用户起的名字"：入库时若把占位名当标题传下去，
    /// Update 会用它覆盖掉库里派生出来的、有信息量的标题，于是那条提示词
    /// 永远叫「未命名草稿」（实测踩到）。
    /// </summary>
    public bool HasCustomTitle { get; set; }

    /// <summary>
    /// 尚未入库的新草稿共用的身份。
    ///
    /// 它是个**共用键**，所以按身份分桶的撤销历史必须特别对待
    /// （见 <see cref="PromptHistoryStore.Migrate"/>）：首次入库拿到 id 时要把历史
    /// 搬走，否则那份草稿从库里重新打开时历史凭空消失，而下一份全新草稿会继承
    /// 它的撤销栈——按 Ctrl+Z 会把上一份草稿的标签退进来。
    /// </summary>
    public const string NewIdentity = "draft:(new)";

    /// <summary>由库条目 id 得到文档身份；空 id 即"尚未入库"。</summary>
    public static string IdentityOf(string? id) =>
        string.IsNullOrEmpty(id) ? NewIdentity : "draft:" + id;

    public override PromptMode Mode => PromptMode.Draft;
    public override string DisplayName => Title.Length > 0 ? Title : DefaultTitle;
    public override string Identity => IdentityOf(Id);
    public override string PrimaryActionText => "复制提示词";

    /// <summary>新建一份空草稿。</summary>
    public static DraftDoc New() => new();
}
