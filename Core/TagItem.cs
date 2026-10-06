using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AnimaCaptioner.Core;

/// <summary>
/// Tag 列表里的一行：标签本身 + 中文释义 + 校验提示。
/// 校验状态用颜色和文字同时表达，不单靠颜色（色盲可辨）。
///
/// 列表按 Anima 官方大类分段显示：每一段的第一行额外带一条"大类条带"
/// （标题 + 段内标签数），同段其余行不显示。这样分段边界完全由行模板控制，
/// 不依赖 ListView 的分组头——后者在 WinUI 3 上实测不渲染（见 README）。
///
/// 所有画刷都在 <see cref="ApplySection"/> 里一次算好：绑定在模板里逐行求值，
/// 每次滚动都重建画刷会很浪费。
/// </summary>
public sealed class TagItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Tag { get; set; } = "";
    public string Cn { get; set; } = "";
    public string Note { get; set; } = "";

    /// <summary>
    /// 这条中文释义是不是模型译的（不是词库里的官方值）。
    ///
    /// 必须能一眼分清：词库的中文来自 Danbooru 对照表，模型译的是推断、可能错。
    /// 把它和官方释义混在一起显示，等于让用户无法判断该不该信。
    /// </summary>
    public bool CnIsModel { get; set; }

    /// <summary>模型译文的来源说明，例如"模型译 · 已联网核对"。</summary>
    public string CnNote { get; set; } = "";

    /// <summary>模型译文用醒目的颜色标出来（区别于词库释义的灰）。</summary>
    public Brush CnBrush => CnIsModel
        ? new SolidColorBrush(Microsoft.UI.Colors.DarkOrange)
        : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public Visibility CnNoteVisibility =>
        CnIsModel && CnNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>模型译文前面的警示标记，让"非官方"这件事在不看说明文字时也成立。</summary>
    public string CnBadge => CnIsModel ? "≈" : "";

    /// <summary>校验用：ok / alias / retired / not-a-tag / underscore / control。</summary>
    public string Status { get; set; } = "ok";

    /// <summary>所属大类（TagSections 里的 key）。</summary>
    public string Section { get; set; } = TagSections.General;

    public int SectionIndex { get; set; } = TagSections.IndexOf(TagSections.General);

    /// <summary>
    /// 这一行在 _pendingTags 里的下标。
    /// 列表是按大类重排后显示的，所以显示下标 ≠ 待保存下标；
    /// 上下移动要靠这个字段换算。每次重建列表时刷新。
    /// </summary>
    public int PendingIndex { get; set; }

    // ---- 分段条带（只有每段第一行显示）----

    public bool IsSectionStart { get; set; }
    public string SectionTitle { get; set; } = "";
    public string SectionSubtitle { get; set; } = "";

    public Visibility HeaderVisibility =>
        IsSectionStart ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>大类条带底色。</summary>
    public Brush HeaderBrush { get; private set; } =
        new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>该大类的颜色，用于行左侧竖条与条带左侧边界。</summary>
    public Brush AccentBrush { get; private set; } =
        new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>该大类的极淡底色，让同一段在视觉上连成一个框。</summary>
    public Brush RowTint { get; private set; } =
        new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>按大类着色。必须在行加入列表之前调用：模板绑定不会在颜色变化时重求值。</summary>
    public void ApplySection(VocabDb? vocab, AppSettings? cfg)
    {
        Section = TagSections.Classify(Tag, vocab);
        SectionIndex = TagSections.IndexOf(Section);
        AccentBrush = TagColors.BrushOf(Section, cfg);
        RowTint = TagColors.RowTintOf(Section, cfg);
        HeaderBrush = TagColors.HeaderOf(Section, cfg);
    }

    /// <summary>
    /// 强制指定配色档位，不做归类。
    /// 自然语言那一段用它——它不是标签，套 Classify 会被兜底成 general，
    /// 于是散文和普通标签长得一模一样，分不出来。
    /// </summary>
    /// <summary>
    /// 强制指定配色档位，不做归类。
    /// 自然语言那一段用它——它不是标签，套 Classify 会被兜底成 general，
    /// 于是散文和普通标签长得一模一样，分不出来。
    /// 顺带把 SectionIndex 设成排在所有大类之后的哨兵值，让它稳定落在列表末尾，
    /// 也不会去抢 general 的段头条带。
    /// </summary>
    public void ForceSection(string key, AppSettings? cfg)
    {
        Section = key;
        SectionIndex = key == TagSections.Prose
            ? TagSections.ProseIndex
            : TagSections.IndexOf(key);

        AccentBrush = TagColors.BrushOf(key, cfg);
        RowTint = TagColors.RowTintOf(key, cfg);
        HeaderBrush = TagColors.HeaderOf(key, cfg);
    }

    public Visibility NoteVisibility =>
        string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;

    // ---- 行内编辑（双击标签进入）----

    private bool _editing;

    /// <summary>
    /// 这一行是否正在被就地编辑。
    ///
    /// 用双向通知而不是只读属性：模板里的 TextBox 要靠它切换显隐，
    /// 而 x:Bind 默认 OneTime——不通知的话切到编辑态界面不会变。
    /// </summary>
    public bool IsEditing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            _editing = value;
            Raise(nameof(IsEditing));
            Raise(nameof(DisplayVisibility));
            Raise(nameof(EditVisibility));
        }
    }

    /// <summary>正常状态下的标签文字。</summary>
    public Visibility DisplayVisibility =>
        _editing ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>编辑态下的输入框。</summary>
    public Visibility EditVisibility =>
        _editing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>散文那一段不是标签，不能就地编辑（它整段是自由文本，不是词库里的条目）。</summary>
    public bool CanEdit => PendingIndex >= 0;

    public Brush NoteBrush => Status switch
    {
        "not-a-tag" => new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        "alias" or "underscore" => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
        "retired" or "no-usage" => new SolidColorBrush(Microsoft.UI.Colors.Gray),
        "control" => new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Gray)
    };
}
