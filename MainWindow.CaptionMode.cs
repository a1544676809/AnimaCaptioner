using System;
using System.Collections.Generic;
using System.Linq;
using AnimaCaptioner.Core;

namespace AnimaCaptioner;

/// <summary>
/// 「模式」菜单的行为：打标格式三选一 + 动态打标预设（Anima / MOSS 的角色 / 画风 LoRA）。
///
/// 关键：预设**默认不启用**（<see cref="_activePreset"/> 为 null）。这样老用户不碰菜单
/// 就完全是原来的行为；选了某个预设才会注入触发词、剥离特征、追加预设系统提示。
/// </summary>
public sealed partial class MainWindow
{
    private List<CaptionPreset> _availablePresets = new();
    private CaptionPreset? _activePreset;

    /// <summary>当前预设（可能为 null = 不使用预设）。</summary>
    public CaptionPreset? ActivePreset
    {
        get => _activePreset;
        set
        {
            _activePreset = value;
            if (_cfg is not null)
            {
                // 预设带模式；选了预设就让编辑器/保存也按它的模式走
                if (value is not null)
                {
                    _cfg.ActiveCaptionMode = value.CaptionMode;
                    _cfg.ActiveHybridProfile = value.HybridProfile;
                }
                _cfg.ActivePresetId = value?.Id ?? "";
                _cfg.Save();
            }
            UpdateCaptionModeMenuState(_cfg?.ActiveCaptionMode ?? CaptionMode.Hybrid);
            UpdatePresetMenuSelection(value?.Id ?? "");
            if (value is not null)
                Log.Write($"preset -> {value.Id} (mode={value.CaptionMode}, intent={value.TrainingIntent})");
            else
                Log.Write("preset -> (none)");
        }
    }

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

    /// <summary>启动时载入预设并把选中状态对齐到配置。只读 id、不写盘。</summary>
    public void InitializeDynamicPresets()
    {
        try { _availablePresets = CaptionPresetEngine.LoadAllPresets(); }
        catch (Exception ex) { Log.Write("load presets failed: " + ex.Message); _availablePresets = new(); }

        _activePreset = string.IsNullOrWhiteSpace(_cfg?.ActivePresetId)
            ? null
            : _availablePresets.FirstOrDefault(p =>
                string.Equals(p.Id, _cfg!.ActivePresetId, StringComparison.OrdinalIgnoreCase));

        UpdateCaptionModeMenuState(CurrentCaptionMode);
        UpdatePresetMenuSelection(_activePreset?.Id ?? "");
        Log.Write($"presets loaded: {_availablePresets.Count}; active={( _activePreset?.Id ?? "(none)")}");
    }

    public void SelectPresetById(string presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) { ActivePreset = null; return; }
        if (_availablePresets.Count == 0)
            _availablePresets = CaptionPresetEngine.LoadAllPresets();
        var target = _availablePresets.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (target is not null) ActivePreset = target;
    }

    private void CaptionModeHybridClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SetActiveCaptionMode(CaptionMode.Hybrid);
    private void CaptionModeTagsOnlyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SetActiveCaptionMode(CaptionMode.TagsOnly);
    private void CaptionModeProseOnlyClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SetActiveCaptionMode(CaptionMode.ProseOnly);

    private void PresetNoneClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SelectPresetById("");
    private void PresetAnimaCharLoraClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SelectPresetById("anima-character-lora");
    private void PresetAnimaStyleLoraClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SelectPresetById("anima-style-lora");
    private void PresetMossCharLoraClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SelectPresetById("moss-character-lora");
    private void PresetMossStyleLoraClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => SelectPresetById("moss-style-lora");

    private void OpenPresetsFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            var dir = CaptionPresetEngine.UserPresetsDirectory;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Write("open presets folder failed: " + ex.Message); }
    }

    /// <summary>手动切打标格式：会**清掉当前预设**（预设自带格式，留着会打架）。</summary>
    public void SetActiveCaptionMode(CaptionMode mode)
    {
        if (_activePreset is not null)
        {
            _activePreset = null;
            if (_cfg is not null) { _cfg.ActivePresetId = ""; _cfg.Save(); }
            UpdatePresetMenuSelection("");
            Log.Write("preset cleared by manual mode switch");
        }

        CurrentCaptionMode = mode;
        UpdateCaptionModeMenuState(mode);

        var text = GetCaptionText();
        var parsed = ParseCaption(text);
        _pendingTags = parsed.Tags;
        _proseTail = parsed.ProseTail;
        RefreshTagRows();
        ApplyPromptColors();

        TranslateInfoText.Text = mode switch
        {
            CaptionMode.TagsOnly => "打标格式：纯标签 —— 逗号分隔的全部内容都算标签。",
            CaptionMode.ProseOnly => "打标格式：纯自然语言 —— 整段都算描述，标签列表为空。",
            _ => "打标格式：标签 + 自然语言（Anima）。",
        };
    }

    private void UpdateCaptionModeMenuState(CaptionMode mode)
    {
        if (CaptionModeHybridItem is not null) CaptionModeHybridItem.IsChecked = mode == CaptionMode.Hybrid;
        if (CaptionModeTagsOnlyItem is not null) CaptionModeTagsOnlyItem.IsChecked = mode == CaptionMode.TagsOnly;
        if (CaptionModeProseOnlyItem is not null) CaptionModeProseOnlyItem.IsChecked = mode == CaptionMode.ProseOnly;
    }

    private void UpdatePresetMenuSelection(string presetId)
    {
        if (PresetNoneItem is not null)
            PresetNoneItem.IsChecked = string.IsNullOrWhiteSpace(presetId);
        if (PresetAnimaCharItem is not null)
            PresetAnimaCharItem.IsChecked = string.Equals(presetId, "anima-character-lora", StringComparison.OrdinalIgnoreCase);
        if (PresetAnimaStyleItem is not null)
            PresetAnimaStyleItem.IsChecked = string.Equals(presetId, "anima-style-lora", StringComparison.OrdinalIgnoreCase);
        if (PresetMossCharItem is not null)
            PresetMossCharItem.IsChecked = string.Equals(presetId, "moss-character-lora", StringComparison.OrdinalIgnoreCase);
        if (PresetMossStyleItem is not null)
            PresetMossStyleItem.IsChecked = string.Equals(presetId, "moss-style-lora", StringComparison.OrdinalIgnoreCase);
    }
}
