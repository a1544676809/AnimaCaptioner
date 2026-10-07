using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimaCaptioner.Core;

/// <summary>
/// 全部可持久化的配置。存在 %LocalAppData%\AnimaCaptioner\settings.json。
/// 不用程序目录：程序目录在 bin\ 下，重新构建会被清掉，用户配置不该随之丢失。
/// API Key 单独走 DPAPI 加密后存 <see cref="ApiKeyProtected"/>，明文永不落盘。
/// </summary>
public sealed class AppSettings
{
    private const string Entropy = "AnimaCaptioner.v1.ApiKey";

    /// <summary>搜索 Key 用独立的 entropy：两把钥匙各自加密，换掉一把不影响另一把。</summary>
    private const string SearchEntropy = "AnimaCaptioner.v1.SearchApiKey";

    /// <summary>词库文件的默认位置（本仓库不含词库，需自行构建后放到这里）。</summary>
    public static string DefaultVocabPath(string fileName) =>
        Path.Combine(Dir, "vocab", fileName);

    /// <summary>
    /// 训练集的默认位置：用户「图片」目录下的 AnimaCaptioner\dataset。
    /// 刻意不用相对路径——程序常从 bin\ 里启动，相对路径会指向构建输出。
    /// </summary>
    private static string DefaultDatasetDir()
    {
        var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pics)) pics = Dir;
        return Path.Combine(pics, "AnimaCaptioner", "dataset");
    }

    /// <summary>
    /// 训练集目录（图片 + 同名 .txt）。默认取用户「图片」目录下的 AnimaCaptioner\dataset，
    /// 首次运行不存在时会由界面提示选择。真实值存在 settings.json 里，改这里不影响已有配置。
    /// </summary>
    public string DatasetDir { get; set; } = DefaultDatasetDir();

    /// <summary>
    /// 词库数据库。**本仓库不附带词库**，需自行构建后在此指定路径
    /// （设置对话框里可改）。默认放在 %LOCALAPPDATA%\AnimaCaptioner\vocab\ 下。
    /// </summary>
    public string VocabDbPath { get; set; } = DefaultVocabPath("vocab.sqlite");

    /// <summary>人工整理的中文 → 标签映射（词库构建产物之一）。</summary>
    public string SeedPath { get; set; } = DefaultVocabPath("seed.json");

    /// <summary>OpenAI 兼容接口的 base，例如 http://127.0.0.1:8080/v1 。</summary>
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:8080/v1";

    /// <summary>模型名。本地 llama-server 用 8b-iq4_xs。</summary>
    public string Model { get; set; } = "8b-iq4_xs";

    /// <summary>DPAPI 密文，加密后 Base64。不要直接改这个字段。</summary>
    public string ApiKeyProtected { get; set; } = "";

    // ---- 联网搜索（作为模型可调用的工具）----

    /// <summary>
    /// 允许模型调用搜索。关掉时翻译只靠模型自身知识。
    ///
    /// 实测（llama-server b11393 + Qwen3-8B）：关闭思考链后模型**不会**发出
    /// tool_calls，只把标签原样吐回来；开启思考才能走通工具循环。所以这条开关
    /// 生效的前提是 <see cref="DisableThinking"/> 为 false，UI 里要说明。
    /// </summary>
    public bool SearchEnabled { get; set; }

    /// <summary>
    /// 搜索后端：shim / searxng / tavily / brave / serper。
    ///
    /// 默认 shim —— 192.168.1.4 上已部署好搜索网关（Anthropic 兼容 + 自建 SearXNG），
    /// 内网完成检索，免费且不依赖外部服务的额度。
    /// </summary>
    public string SearchProvider { get; set; } = "shim";

    /// <summary>
    /// 搜索接口地址。shim 用局域网地址；searxng 填实例根地址；商业 API 留空用默认。
    /// </summary>
    public string SearchBaseUrl { get; set; } = "https://192.168.1.4:49977";

    /// <summary>
    /// TLS SNI / Host 名。Caddy 按这个名字选站点，所以**必须**填证书对应的域名：
    /// 直连局域网 IP 时若 SNI 是那个 IP，Caddy 会回 TLS InternalError；
    /// 若 Host 用 IP，则会得到 200 + 空体的静默响应。两个坑都实测过。
    /// </summary>
    public string SearchSniHost { get; set; } = "nas.ddger.top";

    /// <summary>搜索 API Key 的 DPAPI 密文。SearXNG 自建实例不需要。</summary>
    public string SearchApiKeyProtected { get; set; } = "";

    /// <summary>回灌给模型的结果条数。8B 上下文有限，不宜过多。</summary>
    public int SearchMaxResults { get; set; } = 5;

    /// <summary>
    /// 关掉 Qwen3 的思考链。主要影响短任务（标签释义）；散文翻译两种都行——
    /// 实测开与关质量相同（各 3 次采样、两类输入共 12 个样本全干净），
    /// 只是开思考慢 10–15 倍。开思考时程序会自动给思考留 token 余量
    /// （见 LlmClient.ReasoningHeadroom），且思考内容永不进译文。
    /// </summary>
    public bool DisableThinking { get; set; } = true;

    public int MaxTokens { get; set; } = 400;
    public double Temperature { get; set; } = 0.3;
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>上次打开的文件，下次启动自动定位。</summary>
    public string LastFile { get; set; } = "";

    /// <summary>最近打开过的训练集目录，最新的在前。菜单「最近打开」用它。</summary>
    public List<string> RecentFolders { get; set; } = new();

    /// <summary>预览窗口上次的尺寸（物理像素）。0 表示还没存过，用默认值。</summary>
    public int PreviewWidth { get; set; }
    public int PreviewHeight { get; set; }

    /// <summary>帮助窗口上次的尺寸（物理像素）。0 表示还没存过，用默认值。</summary>
    public int HelpWidth { get; set; }
    public int HelpHeight { get; set; }

    /// <summary>帮助窗口上次看的模块 id，下次打开回到这一页。</summary>
    public string HelpLastModuleId { get; set; } = "";

    /// <summary>左栏与中栏的宽度比例，供分隔条记忆。</summary>
    public double LeftStar { get; set; } = 2;
    public double MidStar { get; set; } = 2;
    public double RightStar { get; set; } = 3;

    /// <summary>
    /// tag 列表里各大类的框线颜色，"#RRGGBB"。
    /// 键是 TagSections 里的大类 key（meta/count/character/series/artist/general）。
    /// 缺失的键回落到 TagSections 的默认色，所以这里不需要预填全部六个。
    /// </summary>
    public Dictionary<string, string> SectionColors { get; set; } = new();

    /// <summary>取某个大类的颜色十六进制值；没有配置过时返回空串（调用方回落默认）。</summary>
    public string GetSectionColor(string key) =>
        SectionColors.TryGetValue(key, out var v) ? v : "";

    /// <summary>写入某个大类的颜色。</summary>
    public void SetSectionColor(string key, string hex) => SectionColors[key] = hex;

    /// <summary>把某个大类恢复成默认色。</summary>
    public void ResetSectionColor(string key) => SectionColors.Remove(key);

    /// <summary>六大类是否都还是默认色（设置页据此决定"恢复默认"要不要可点）。</summary>
    [JsonIgnore]
    public bool SectionColorsAreDefault =>
        SectionColors.Count == 0 ||
        SectionColors.All(kv =>
            string.Equals(kv.Value, TagSections.Get(kv.Key).DefaultHex,
                          StringComparison.OrdinalIgnoreCase));

    /// <summary>把一个目录记到最近列表头部（去重，最多 8 个）。</summary>
    public void PushRecent(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        RecentFolders.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, dir);
        if (RecentFolders.Count > 8) RecentFolders.RemoveRange(8, RecentFolders.Count - 8);
    }

    // ---- 运行时字段（不参与序列化）----

    /// <summary>明文 API Key。只在内存里，保存时加密写入 <see cref="ApiKeyProtected"/>。</summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => _apiKey ??= DpapiSecret.TryUnprotect(ApiKeyProtected, Entropy) ?? "";
        set
        {
            _apiKey = value ?? "";
            ApiKeyProtected = string.IsNullOrEmpty(_apiKey)
                ? ""
                : DpapiSecret.Protect(_apiKey, Entropy);
        }
    }

    [JsonIgnore] private string? _apiKey;

    /// <summary>明文搜索 API Key。与 <see cref="ApiKey"/> 同一套 DPAPI 处理。</summary>
    [JsonIgnore]
    public string SearchApiKey
    {
        get => _searchKey ??= DpapiSecret.TryUnprotect(SearchApiKeyProtected, SearchEntropy) ?? "";
        set
        {
            _searchKey = value ?? "";
            SearchApiKeyProtected = string.IsNullOrEmpty(_searchKey)
                ? ""
                : DpapiSecret.Protect(_searchKey, SearchEntropy);
        }
    }

    [JsonIgnore] private string? _searchKey;

    /// <summary>密文存在但解不开（换了机器或账户）。UI 据此提示重填，而不是静默当成没填。</summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable =>
        !string.IsNullOrEmpty(ApiKeyProtected)
        && DpapiSecret.TryUnprotect(ApiKeyProtected, Entropy) is null;

    // ---- 落盘 ----

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "AnimaCaptioner");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s is not null)
                {
                    Log.Write("settings loaded from " + FilePath);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed (" + ex.Message + "), using defaults");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 先写临时文件再替换：中途崩溃不会留下半截 JSON 把配置弄坏
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts),
                              new System.Text.UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            Log.Write("settings saved to " + FilePath);
        }
        catch (Exception ex)
        {
            Log.Write("settings save FAILED: " + ex.Message);
            throw;
        }
    }
}
