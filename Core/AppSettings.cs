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
    /// 实测（llama-server b11393 + Qwen3-8B）：把推理档位设为「关闭思考」后模型
    /// **不会**发出 tool_calls，只把标签原样吐回来；有档位（思考开着）才能走通工具
    /// 循环。所以这条开关生效的前提是 <see cref="ReasoningEffort"/> 不是 "none"，
    /// UI 里要说明。
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
    /// 推理档位（服务端的 `reasoning_effort`）。空串 = 自动，即不下发、跟随服务端
    /// 自己的默认档；其余取值为 <see cref="Core.LlmClient.EffortVocabulary"/> 里那八个。
    ///
    /// 早先这里是个 bool（"关闭思考链"），之所以升级成档位：那个 bool 只能表达
    /// "关"与"不指定"两态，而服务端提供的是 8 档连续强度，用户没法要求"少想一点
    /// 但别完全不想"。映射关系在 <see cref="MigrateThinking"/>：`true → "none"`。
    ///
    /// **默认 "none"**（与旧的 bool 默认 true 一致）：主要影响短任务（标签释义）；
    /// 散文翻译开与关实测质量相同（各 3 次采样、共 12 个样本全干净），只是开思考
    /// 慢 10–15 倍。非 none 档位时程序会自动给思考留 token 余量
    /// （见 LlmClient.ReasoningHeadroom），且思考内容永不进译文。
    ///
    /// **两种服务端要两种关法，程序按 `/models` 报的能力自动选**：llama-server 认
    /// `chat_template_kwargs.enable_thinking=false`；而 DeepSeek 这类云服务
    /// **把它静默忽略**（实测带与不带都照样思考），真正管用的是被严格校验的
    /// `reasoning_effort`。服务端没在 `/models` 里声明 `effort` 时不下发档位
    /// （见 LlmClient.EffortToSend），界面会把这件事写出来，不静默吞掉。
    /// </summary>
    public string ReasoningEffort { get; set; } = "none";

    /// <summary>
    /// 旧字段：bool「关闭思考链」。**只用于把老配置读进来迁移**，不再写出。
    ///
    /// `WhenWritingDefault` + 迁移里置 null 的两步配合，让它自我清理：迁移过一次
    /// 再保存，settings.json 里这个键就没了，不会长期留着一个和
    /// <see cref="ReasoningEffort"/> 说法不一致的死字段。
    ///
    /// 顺序：**旧字段在场时以它为准**。因为它是旧版本写的，而新版本一保存就会把它
    /// 抹掉，所以"两者同时在场"只可能来自手工编辑 settings.json——那时按旧字段
    /// 迁移、由用户在新界面上重新选一次，比猜哪个更新更可预期。
    /// </summary>
    [JsonPropertyName("DisableThinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool? LegacyDisableThinking { get; set; }

    public int MaxTokens { get; set; } = 400;
    public double Temperature { get; set; } = 0.3;
    public int TimeoutSeconds { get; set; } = 300;

    // ---- AI 助手 ----

    /// <summary>
    /// 打开 AI 助手面板：右栏下半部分从"确定性的中译英翻译器"换成能读图、能查词库、
    /// 能提案的对话面板。
    ///
    /// **默认关闭**，两个理由：它要一个支持图片输入的接口才完整；而更重要的，
    /// 翻译器那条路是四轮实测调出来的确定性转换（长度跟输入走、方向词不许丢、
    /// 不许加质量词，见 <see cref="LlmClient.ProseSystemPrompt"/>），把它藏起来
    /// 不该是默认行为。助手面板里仍然留着一键翻译，所以关掉不等于失去它。
    /// </summary>
    public bool AgentEnabled { get; set; }

    /// <summary>
    /// 标注模式下把当前图片一起发给模型。**默认开**——读图自动打标是这个面板
    /// 存在的主要理由。
    ///
    /// 但服务端在 `/models` 里明确报过"这个模型只吃文本"时，界面会把「带当前
    /// 图片」禁掉并说明原因，而不是发一次注定 400 的请求（实测 llama-server
    /// 根本不报这个字段，所以那种情况一律放行——**"不知道"不等于"不支持"**）。
    /// </summary>
    public bool AgentAttachImage { get; set; } = true;

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

    /// <summary>标签搜索窗口上次的尺寸（物理像素）。0 表示还没存过，用默认值。</summary>
    public int TagSearchWidth { get; set; }
    public int TagSearchHeight { get; set; }

    /// <summary>标签搜索窗口上次用的模式："db"（词库搜索）或 "ai"（问模型）。</summary>
    public string TagSearchMode { get; set; } = "db";

    /// <summary>左栏与中栏的宽度比例，供分隔条记忆。</summary>
    public double LeftStar { get; set; } = 2;
    public double MidStar { get; set; } = 2;
    public double RightStar { get; set; } = 3;

    /// <summary>
    /// 写标签模式下的一组比例。**必须与标注模式分开存**：两个模式的分栏语义
    /// 不同（标注是三栏：图片 / 标签 / 提示词；写标签时左栏是提示词库），
    /// 共用一组值会让一个模式里拖过的布局在切到另一个模式时把对方的布局改掉。
    /// 0 表示还没存过，回落到 <see cref="DraftLeftStar"/> 等的默认值。
    /// </summary>
    public double DraftLeftStar { get; set; }
    public double DraftMidStar { get; set; }
    public double DraftRightStar { get; set; }

    // ---- 写标签模式 ----

    /// <summary>
    /// 上次停在哪个模式。下次启动直接回到它。
    /// 存字符串而不是枚举，避免枚举成员改名后旧配置反序列化失败。
    /// </summary>
    public string LastMode { get; set; } = "label";

    /// <summary>上次编辑过的那条提示词库条目的 id。切回写标签模式时用它恢复。</summary>
    public string DraftLastPromptId { get; set; } = "";

    /// <summary>
    /// 写标签模式下，给标签里的圆括号转义（<c>(</c> → <c>\(</c>），让
    /// <c>210 (reverse:1999)</c> 这类标签原样进编码器。
    ///
    /// <para>**只在写标签模式生效**，而且这一点不能松：标注模式的 caption 是直接喂给
    /// 分词器训练的，反斜杠会变成垃圾 token 污染训练集——转义是推理侧语法，不是训练侧。</para>
    ///
    /// <para>默认开。Anima 自带索引 108,257 个标签里 <b>25,815 个含圆括号</b>、
    /// 其中 25,813 个会被 ComfyUI 的提示词解析器改写；另有 104 个更糟——
    /// <c>(reverse:1999)</c> 的 <c>:1999</c> 被当成权重，那个词直接消失。
    /// 判定与依据见 <see cref="TagEscape"/>。</para>
    /// </summary>
    public bool EscapeTagParens { get; set; } = true;

    // ---- 编辑 ----

    /// <summary>
    /// 撤销栈的最大步数。0 表示关闭撤销。
    ///
    /// <para>两个模式共用同一个值：撤销栈存的是"当前文档的标签 + 散文"，
    /// 而标注的一张图与写标签的一份草稿在这一点上没有区别，按模式分两套
    /// 只会让用户在设置里看到两个含义相同的输入框。</para>
    ///
    /// <para>越界值由 <see cref="PromptHistory.MaxSteps"/> 的 setter 收敛，
    /// 所以这里不做校验——手改 settings.json 填进一个荒唐的值也不会出事。</para>
    /// </summary>
    public int UndoMaxSteps { get; set; } = PromptHistory.DefaultMaxSteps;

    /// <summary>
    /// tag 列表里各大类的框线颜色，"#RRGGBB"。
    /// 键是 TagSections 里的大类 key（quality/meta/period/safety/count/
    /// character/series/artist/general）。
    /// 缺失的键回落到 TagSections 的默认色，所以这里不需要预填全部九个。
    /// </summary>
    public Dictionary<string, string> SectionColors { get; set; } = new();

    /// <summary>
    /// 配色表的版本号，用于迁移旧配置。0/缺失 = 拆分前的六段方案。
    ///
    /// 为什么需要它：拆分前 quality/meta/year/safety 合成一段、共用一个 "meta" 键；
    /// 拆开后 "meta" 变成了「元信息」这一段的真 key。不迁移的话，旧配置里存的
    /// 那个颜色会被当成元信息的颜色读出来，而它很可能等于质量段的默认色
    /// （旧默认正是 #E8A33D），于是两段同色、看上去像没生效。
    /// </summary>
    public int ColorSchema { get; set; }

    /// <summary>当前配色表版本。改成大于 0 时请同时更新 <see cref="MigrateColorSchema"/>。</summary>
    public const int CurrentColorSchema = 2;

    /// <summary>
    /// 把旧的六段配色迁移到九段。返回是否真的动过配置（调用方据此决定要不要落盘）。
    ///
    /// 迁移规则：旧的 "meta" 是四类共用的颜色，把它挪给**质量**段——它是四类里的
    /// 第一段，也是官方推荐前缀的开头（`masterpiece, best quality, score_7, safe`）。
    /// 元信息/年代/安全三段回落到各自的新默认色，这样拆分的意义才看得出来。
    /// 已经显式设过 "quality" 的不覆盖。
    /// </summary>
    private bool MigrateColorSchema()
    {
        if (ColorSchema >= CurrentColorSchema) return false;
        var changed = false;

        if (!SectionColors.ContainsKey(TagSections.Quality)
            && SectionColors.TryGetValue("meta", out var legacy)
            && !string.IsNullOrWhiteSpace(legacy))
        {
            SectionColors[TagSections.Quality] = legacy;
            changed = true;
        }
        // 旧 key 必须移除：它现在指的是「元信息」，留着会让元信息继承四类共用的旧色
        if (ColorSchema < 2 && SectionColors.Remove("meta")) changed = true;

        ColorSchema = CurrentColorSchema;
        return changed;
    }

    /// <summary>
    /// 把旧的 bool「关闭思考链」迁移成推理档位。返回是否真的动过配置。
    ///
    /// 映射只有两条：`true → "none"`（真的关掉）、`false → ""`（自动，即不下发档位、
    /// 跟随服务端默认）。`false` 之所以不能映射成某个具体档位：它当时的语义就是
    /// "不去管服务端怎么想"，映射成 "high" 之类会凭空替用户做选择。
    /// </summary>
    private bool MigrateThinking()
    {
        if (LegacyDisableThinking is not { } legacy) return false;
        ReasoningEffort = legacy ? "none" : "";
        LegacyDisableThinking = null;   // 迁移过就不再写出，旧键随下一次 Save 消失
        return true;
    }

    /// <summary>取某个大类的颜色十六进制值；没有配置过时返回空串（调用方回落默认）。</summary>
    public string GetSectionColor(string key) =>
        SectionColors.TryGetValue(key, out var v) ? v : "";

    /// <summary>写入某个大类的颜色。</summary>
    public void SetSectionColor(string key, string hex) => SectionColors[key] = hex;

    /// <summary>把某个大类恢复成默认色。</summary>
    public void ResetSectionColor(string key) => SectionColors.Remove(key);

    /// <summary>九个大类是否都还是默认色（设置页据此决定"恢复默认"要不要可点）。</summary>
    [JsonIgnore]
    public bool SectionColorsAreDefault =>
        SectionColors.Count == 0 ||
        SectionColors.All(kv =>
            // 用 ColorOf 而不是 Get：配色表比大类多一项「自然语言」，
            // 而 Get(Prose) 会回落到 general 的默认色，把散文色判成"等于默认"。
            string.Equals(kv.Value, TagSections.ColorOf(kv.Key).DefaultHex,
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

    /// <summary>
    /// 从 JSON 文本解析出一份配置，并跑完全部迁移。
    ///
    /// **抽出来是为了可测**：<see cref="Load"/> 读写的是固定的用户路径，回归测试不能
    /// 碰它，而"老配置能不能正确迁移"恰恰是最容易静默出错的一步（迁移写错不会报错，
    /// 只会让用户的选择悄悄变掉）。tests/Defaults.cs 直接喂 JSON 字符串断言结果。
    /// </summary>
    public static AppSettings? FromJson(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
        if (s is null) return null;

        // 旧配置的配色键要迁移（六段 -> 九段）；bool 关闭思考要迁移成推理档位。
        // 迁移只在内存里做，下次任何一次 Save() 会把结果写回去。
        if (s.MigrateColorSchema()) Log.Write("settings: colour schema migrated to v" + CurrentColorSchema);
        if (s.MigrateThinking()) Log.Write("settings: reasoning effort migrated to '" + s.ReasoningEffort + "'");
        return s;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = FromJson(File.ReadAllText(FilePath));
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
