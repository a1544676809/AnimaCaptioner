using System.Text;
using System.Text.Json;

namespace AnimaCaptioner.Core;

/// <summary>
/// 用户手改的标签归类（`标签 → 大类`）的落盘。
///
/// 为什么单独一个文件而不是塞进 settings.json：它是**会随使用增长的数据**
/// （每写一个新触发词就可能多一条），不是配置；而且清掉它不该影响任何设置。
/// 与 model-gloss.json / prompts.json 同一个道理。
///
/// 真正的"生效"靠 <see cref="TagSections.SetOverrides"/>——那边是纯逻辑，
/// 这边只管读写。所以回归测试能直接验语义，不必碰磁盘。
/// </summary>
public static class TagSectionStore
{
    public static string FilePath => Path.Combine(AppSettings.Dir, "tag-sections.json");

    /// <summary>
    /// 读进内存并**立刻生效**（<see cref="TagSections.SetOverrides"/>）。
    /// 文件坏了不该让程序起不来：丢掉重建即可，代价只是重新手改一遍。
    /// </summary>
    public static int Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                TagSections.SetOverrides(null);
                return 0;
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            TagSections.SetOverrides(data);
            Log.Write($"tag sections loaded: {TagSections.OverrideCount} overrides from {FilePath}");
            return TagSections.OverrideCount;
        }
        catch (Exception ex)
        {
            Log.Write("tag sections load failed (will start empty): " + ex.Message);
            TagSections.SetOverrides(null);
            return 0;
        }
    }

    /// <summary>把当前生效的手改表写盘。返回是否成功（失败只记日志，不抛）。</summary>
    public static bool Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var json = JsonSerializer.Serialize(TagSections.OverridesSnapshot(),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });

            // 先写临时文件再替换：写到一半崩了不会留下半截映射表
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            Log.Write($"tag sections saved: {TagSections.OverrideCount} overrides");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("tag sections save failed: " + ex.Message);
            return false;
        }
    }
}
