using System;
using AnimaCaptioner.Core;

namespace AnimaCaptioner;

/// <summary>
/// 「模式 → 打标格式」三个单选：纯标签 / 纯自然语言 / 混合（Anima）。
///
/// 关键：**它不是只改一个显示开关**。`_cfg.ActiveCaptionMode` 会被
/// <c>ParseCaption</c>（读）和 <c>CommitPrompt</c>（写）同时使用，所以切换之后
/// 当前正文立刻按新语义重新解析、标签列表跟着变，后续保存也按新格式落盘。
///
/// 切换本身**不改写正文文字**（只重新解析），所以不会因为切模式丢内容：
/// - 切到「纯标签」：逗号分隔的全部内容都算标签，不再有"散文尾巴"；
/// - 切到「纯自然语言」：整段都算描述，标签列表为空；
/// - 切回「混合」：重新按词库/句子特征切出标签与散文。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>当前打标格式（来自设置，改了就存盘）。</summary>
    public CaptionMode CurrentCaptionMode
    {
        get => _cfg?.ActiveCaptionMode ?? CaptionMode.Hybrid;
        set
        {
            if (_cfg is not null && _cfg.ActiveCaptionMode != value)
            {
                _cfg.ActiveCaptionMode = value;
                _cfg.Save();
            }
            UpdateCaptionModeMenuState(value);
        }
    }

    /// <summary>启动时把菜单勾选对齐到配置里的当前模式。</summary>
    private void InitCaptionModeMenu()
    {
        try { UpdateCaptionModeMenuState(CurrentCaptionMode); }
        catch (Exception ex) { Log.Write("init caption mode menu failed: " + ex.Message); }
    }

    private void CaptionModeHybridClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SetActiveCaptionMode(CaptionMode.Hybrid);

    private void CaptionModeTagsOnlyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SetActiveCaptionMode(CaptionMode.TagsOnly);

    private void CaptionModeProseOnlyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SetActiveCaptionMode(CaptionMode.ProseOnly);

    /// <summary>切模式：存盘 → 对齐菜单 → **重新解析当前正文** → 重建标签列表。</summary>
    public void SetActiveCaptionMode(CaptionMode mode)
    {
        if (_cfg is null) return;

        var changed = _cfg.ActiveCaptionMode != mode;
        CurrentCaptionMode = mode;   // 存盘 + 菜单勾选

        if (changed)
        {
            // 不重写正文，只重新按新语义解析（正文文字原样保留）。
            var text = GetCaptionText();
            var parsed = ParseCaption(text);
            _pendingTags = parsed.Tags;
            _proseTail = parsed.ProseTail;
            RefreshTagRows();
            ApplyPromptColors();
            Log.Write($"caption mode -> {mode}: tags={_pendingTags.Count} prose={_proseTail.Length}");
        }

        TranslateInfoText.Text = mode switch
        {
            CaptionMode.TagsOnly => "打标格式：纯标签 —— 逗号分隔的全部内容都算标签，不再分出自然语言尾巴。",
            CaptionMode.ProseOnly => "打标格式：纯自然语言 —— 整段都算描述，标签列表为空。",
            _ => "打标格式：标签 + 自然语言（Anima）—— 前置分类标签 + 尾段描述。",
        };
    }

    private void UpdateCaptionModeMenuState(CaptionMode mode)
    {
        if (CaptionModeHybridItem is not null)
            CaptionModeHybridItem.IsChecked = mode == CaptionMode.Hybrid;
        if (CaptionModeTagsOnlyItem is not null)
            CaptionModeTagsOnlyItem.IsChecked = mode == CaptionMode.TagsOnly;
        if (CaptionModeProseOnlyItem is not null)
            CaptionModeProseOnlyItem.IsChecked = mode == CaptionMode.ProseOnly;
    }
}
