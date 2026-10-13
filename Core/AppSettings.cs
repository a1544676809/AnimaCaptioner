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

    /// <summary>
    /// 构建词库时抓取上游素材用的 HTTP 代理（留空 = 直连）。
    ///
    /// 必须有这一项：上游是 <c>raw.githubusercontent.com</c> 和 <c>danbooru.donmai.us</c>，
    /// 这两个域名在国内网络下直连会超时（实测：无代理时全部失败，用 127.0.0.1:10808 全部 200）。
    /// 程序里其余的网络调用都只打本地或自建服务，所以这是唯一需要它的地方。
    /// 系统代理设置**不会**被自动采用——实测这台机器上 GetSystemWebProxy 返回直连，
    /// 而直连就是不通，所以只能让用户显式填。
    /// </summary>
    public string VocabProxy { get; set; } = "";

    /// <summary>素材目录（上游文件存放处）。默认取词库文件同级的 sources\。</summary>
    public string VocabSourceDir { get; set; } = "";

    /// <summary>
    /// 「完全权限」：助手提出的改动**直接写进提示词**，不再等用户点「应用」。
    ///
    /// **默认关闭，而且必须默认关闭。** 这个软件有一条承重原则：助手只出提案，
    /// 落盘由人点一下（见 TagAgent 的类注释），于是"模型在没人看着的时候改了训练集"
    /// 结构上不可能发生。打开它等于主动放弃这条保证，所以：
    ///
    /// 1. 名字就叫「完全权限」，说明文字写明它到底让渡了什么；
    /// 2. 写入仍然**只算一个撤销点**（ApplyAgentProposal 里那一处 PushUndo），
    ///    Ctrl+Z 一下能全退——这是打开之后唯一还兜着的东西；
    /// 3. 每次自动写入都记一条日志，事后能查是哪一轮写的。
    /// </summary>
    public bool AgentFullAccess { get; set; }

    // ---- 模型站点 ----
    //
    // 地址 / 密钥 / 模型名 / 推理档位现在按**站点**成组保存（见 ModelProfile），
    // 顶层那四个名字变成"当前活动站点"的视图。这个形状是刻意的：
    //
    // 1. **换服务是一次切换，不是改四处。** 本地 llama-server 与云端之间，
    //    地址、密钥、模型名、档位全都不一样（档位甚至是相反的：前者只认
    //    「关闭思考」，后者认 8 档强度）。拆成四个独立输入框时每换一次要改四处，
    //    改漏一处就是 401 或一个静默不生效的档位。
    // 2. **调用方零改动。** LlmClient 的每个方法（以及 TagTranslator、TagSearchAgent、
    //    AgentHost、MainWindow）都从 cfg 上读这四个名字。把它们做成视图之后，
    //    那 50 多处调用一行都不用动，也不可能出现"某个调用点还在读旧字段"。

    /// <summary>保存过的模型站点。至少要有一个（活动指针指向它）。</summary>
    private List<ModelProfile>? _profiles;

    /// <summary>
    /// 保存过的模型站点。**永远非空**：一个都没有时当场补一个默认站点
    /// （本地 llama-server 的惯例），否则 <see cref="ActiveProfile"/> 会是 null、
    /// 四个视图属性全变空串，界面看起来像"模型配置丢了"。
    /// </summary>
    public List<ModelProfile> Profiles
    {
        get
        {
            // 空列表也要补：手改 settings.json 写成 "Profiles": [] 时列表非 null
            // 但空，同样会让四个视图属性全变空串。
            if (_profiles is null || _profiles.Count == 0)
                _profiles = new List<ModelProfile>
                {
                    new() { BaseUrl = DefaultBaseUrl, Model = DefaultModel }
                };
            return _profiles;
        }
        set => _profiles = value;
    }

    /// <summary>当前活动的站点 id。<see cref="ActiveProfile"/> 找不到时回落到第一个。</summary>
    public string ActiveProfileId { get; set; } = "";

    /// <summary>
    /// 站点表的版本号，用于迁移旧配置。0/缺失 = 拆分前（顶层只有一份地址/密钥/模型）。
    ///
    /// 为什么需要它：旧配置里那四个顶层字段与新的 <see cref="Profiles"/> 表达的是
    /// 同一件事。不迁移的话用户的地址、密钥、模型会被"读不出来"——界面显示默认值，
    /// 而一保存就把旧值真的覆盖掉了（密钥那一项尤其不可挽回）。
    /// </summary>
    public int ProfileSchema { get; set; }

    /// <summary>当前站点表版本。改成大于 0 时请同时更新 <see cref="MigrateProfiles"/>。</summary>
    public const int CurrentProfileSchema = 1;

    /// <summary>新建站点时的默认地址与模型名（本地 llama-server 的惯例）。</summary>
    internal const string DefaultBaseUrl = "http://127.0.0.1:8080/v1";
    internal const string DefaultModel = "8b-iq4_xs";

    /// <summary>
    /// 当前活动的站点；一个都没有时返回 null。
    ///
    /// 指针指向不存在的 id 时**回落到第一个**而不是返回 null：手改 settings.json
    /// 写错一个 id 不该让整个模型配置变成"未配置"。
    /// </summary>
    [JsonIgnore]
    public ModelProfile? ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles.FirstOrDefault();

    /// <summary>要改配置时用它：没有站点就当场建一个。视图属性的 setter 都走这里。</summary>
    [JsonIgnore]
    private ModelProfile ActiveOrNew
    {
        get
        {
            if (ActiveProfile is { } a)
            {
                // 指针没对上（空、或指向已删除的站点）时顺手校正，免得每次都靠回落
                if (a.Id != ActiveProfileId) ActiveProfileId = a.Id;
                return a;
            }
            var p = new ModelProfile { BaseUrl = DefaultBaseUrl, Model = DefaultModel };
            Profiles.Add(p);
            ActiveProfileId = p.Id;
            return p;
        }
    }

    /// <summary>加一个站点并让它成为活动站点，返回它。界面上的「添加站点」用它。</summary>
    public ModelProfile AddProfile(string baseUrl = "", string model = "")
    {
        var p = new ModelProfile
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim(),
            Model = model ?? "",
        };
        Profiles.Add(p);
        ActiveProfileId = p.Id;
        return p;
    }

    /// <summary>
    /// 删掉一个站点。**最后一个不允许删**——一个都没有的状态下所有视图属性都会
    /// 落到空串，界面看起来像"模型配置丢了"，而其实只是没站点可指。
    /// 返回是否真的删掉了。
    /// </summary>
    public bool RemoveProfile(string id)
    {
        if (Profiles.Count <= 1) return false;
        var hit = Profiles.FirstOrDefault(p => p.Id == id);
        if (hit is null) return false;

        Profiles.Remove(hit);
        if (ActiveProfileId == id) ActiveProfileId = Profiles[0].Id;
        return true;
    }

    /// <summary>
    /// 把旧的单站点配置迁移成站点表。返回是否真的动过配置。
    ///
    /// **判据是后备字段 <c>_profiles</c> 是不是 null，不是 <see cref="Profiles"/>.Count。**
    /// 后者不行：Getter 会在表为空时自动补一个默认站点（那是为了让四个视图属性永远
    /// 有落脚点），于是"旧配置里根本没有这张表"与"表在但是空的"变得无法区分，
    /// 迁移会以为已经有站点、转头把旧配置的地址/密钥/模型丢掉。反序列化时若 JSON 里
    /// 没有 `Profiles` 键，Setter 就不会被调用，所以 `_profiles is null` 正是那个信号。
    ///
    /// 只有在 <see cref="ProfileSchema"/> 小于当前版本时才跑：跑过之后旧顶层键一律
    /// 置 null（配合 <c>WhenWritingDefault</c>，下次保存它们就消失了），
    /// 否则旧键会和新表长期并存、说法不一。
    ///
    /// 旧键的**空串与缺失是两回事**：`"ReasoningEffort":""` 是"自动"这个真实取值，
    /// 而键不在场才是"没配过、用默认"。所以判据是 `is not null` 而不是
    /// `!IsNullOrWhiteSpace`——把空串当缺失会把用户的"自动"改成"关闭思考"。
    /// </summary>
    private bool MigrateProfiles()
    {
        if (ProfileSchema >= CurrentProfileSchema) return false;

        // 这一句必须在碰 Profiles 属性之前读，否则 getter 会替我们把默认站点补上
        var hadProfilesKey = _profiles is not null;

        var hadLegacy = LegacyApiBaseUrl is not null || LegacyModel is not null
                     || LegacyApiKeyProtected is not null || LegacyReasoningEffort is not null;

        if (hadProfilesKey && !hadLegacy)
        {
            // 新配置：JSON 里有表、旧键也不在场。只需把活动指针扶正。
            if (string.IsNullOrWhiteSpace(ActiveProfileId)
                || !_profiles!.Any(p => p.Id == ActiveProfileId))
                ActiveProfileId = _profiles![0].Id;
            ProfileSchema = CurrentProfileSchema;
            return true;
        }

        // 走到这里有两种情况，处理方式相同——都以旧键为准：
        //   a) 旧配置（没有表），把旧键装进一个新站点；
        //   b) 新配置被手改加了旧键，按旧键覆盖第一个站点。
        // 旧键只可能来自更早的版本或手工编辑，按它走比猜哪个更新更可预期。
        var table = _profiles ?? new List<ModelProfile>();
        var p = table.Count > 0 ? table[0] : new ModelProfile();
        if (hadLegacy)
        {
            if (LegacyApiBaseUrl is not null) p.BaseUrl = LegacyApiBaseUrl;
            if (LegacyModel is not null) p.Model = LegacyModel;
            if (LegacyApiKeyProtected is not null) p.ApiKeyProtected = LegacyApiKeyProtected;

            // 档位要单独对待：它可能来自更老的 bool（MigrateThinking 随后会写在
            // 活动站点的档位上），所以这里只有在旧配置**没有**那个 bool 时才搬档位。
            if (LegacyReasoningEffort is not null && LegacyDisableThinking is null)
                p.ReasoningEffort = LegacyReasoningEffort;

            // 密文搬走之后明文缓存要作废：ApiKey 的 getter 只解一次并缓存，
            // 若之前已经被读过（空密文 -> 缓存了空串），这里换了密文它不会重解。
            p.ForgetCachedKey();
        }

        if (table.Count == 0) table.Add(p);
        _profiles = table;
        ActiveProfileId = p.Id;

        LegacyApiBaseUrl = null;
        LegacyModel = null;
        LegacyApiKeyProtected = null;
        LegacyReasoningEffort = null;

        ProfileSchema = CurrentProfileSchema;
        return true;
    }

    /// <summary>旧配置的顶层接口地址。只用于迁移，不再写出。</summary>
    [JsonPropertyName("ApiBaseUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyApiBaseUrl { get; set; }

    /// <summary>旧配置的顶层模型名。只用于迁移，不再写出。</summary>
    [JsonPropertyName("Model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyModel { get; set; }

    /// <summary>旧配置的顶层密钥密文。只用于迁移，不再写出。</summary>
    [JsonPropertyName("ApiKeyProtected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyApiKeyProtected { get; set; }

    /// <summary>旧配置的顶层推理档位。只用于迁移，不再写出。</summary>
    [JsonPropertyName("ReasoningEffort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyReasoningEffort { get; set; }

    /// <summary>OpenAI 兼容接口的 base。**是活动站点的视图**，不是独立字段。</summary>
    [JsonIgnore]
    public string ApiBaseUrl
    {
        get => ActiveProfile?.BaseUrl ?? "";
        set => ActiveOrNew.BaseUrl = value ?? "";
    }

    /// <summary>模型名。**是活动站点的视图**。</summary>
    [JsonIgnore]
    public string Model
    {
        get => ActiveProfile?.Model ?? "";
        set => ActiveOrNew.Model = value ?? "";
    }

    /// <summary>DPAPI 密文。**是活动站点的视图**。</summary>
    [JsonIgnore]
    public string ApiKeyProtected
    {
        get => ActiveProfile?.ApiKeyProtected ?? "";
        set => ActiveOrNew.ApiKeyProtected = value ?? "";
    }

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
    /// **是活动站点的视图**——档位跟服务走：llama-server 只认「关闭思考」，云端认 8 档。
    ///
    /// 早先这里是个 bool（"关闭思考链"），之所以升级成档位：那个 bool 只能表达
    /// "关"与"不指定"两态，而服务端提供的是 8 档连续强度，用户没法要求"少想一点
    /// 但别完全不想"。映射关系在 <see cref="MigrateThinking"/>：`true → "none"`。
    ///
    /// 默认 "none"（与旧的 bool 默认 true 一致）：主要影响短任务（标签释义）；
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
    [JsonIgnore]
    public string ReasoningEffort
    {
        get => ActiveProfile?.ReasoningEffort ?? "";
        set => ActiveOrNew.ReasoningEffort = value ?? "";
    }

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

    /// <summary>
    /// 明文 API Key。**是活动站点的视图**——加解密与缓存都在
    /// <see cref="ModelProfile"/> 上，那里才是密钥的主人。
    /// </summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => ActiveProfile?.ApiKey ?? "";
        set => ActiveOrNew.ApiKey = value ?? "";
    }

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
    public bool ApiKeyUnreadable => ActiveProfile?.ApiKeyUnreadable ?? false;

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

        // 顺序有讲究：Profiles **先**迁移，因为 MigrateThinking 现在读写的是
        // 活动站点的档位（ReasoningEffort 已经是视图），而活动站点要等 Profiels
        // 建出来才存在。反过来做的话那次迁移会落在"临时新建的空站点"上，
        // 用户原来的地址、密钥、模型名就全丢了。
        if (s.MigrateProfiles()) Log.Write("settings: model profiles migrated to v" + CurrentProfileSchema);
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

    /// <summary>
    /// 序列化成 JSON 文本。<see cref="FromJson"/> 的对称搭档——它存在的主要理由是
    /// 让"某个设置项能不能落盘再读回"变成可断言的事（回归测试直接对转一圈），
    /// 而不用去碰用户真实的 settings.json。<see cref="Save"/> 也走这一条。
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 先写临时文件再替换：中途崩溃不会留下半截 JSON 把配置弄坏
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, ToJson(), new System.Text.UTF8Encoding(false));
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
