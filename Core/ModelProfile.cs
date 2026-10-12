using System.Text.Json.Serialization;

namespace AnimaCaptioner.Core;

/// <summary>
/// 一个模型站点：接口地址 + 密钥 + 模型名 + 这个模型的推理档位。
///
/// **为什么要按站点存而不是按字段存**：这些值天然是一组。换一个服务就要同时换
/// 地址、密钥、模型名（本地 llama-server 的模型名和云端根本不是一回事），
/// 而档位也跟着服务走——llama-server 只认「关闭思考」那一档、云端认 8 档强度。
/// 拆成四个独立输入框时，用户每换一次服务都要改四处，改漏一处就是一次 401 或
/// 一个静默不生效的档位。
///
/// 密钥仍然走 DPAPI（<see cref="DpapiSecret"/>），明文只在内存里。**熵沿用旧值**
/// （<see cref="KeyEntropy"/>）：这样从旧配置迁移过来时密文可以原样搬走，不必
/// 解密再加密一次——少一次经手就少一次出错的机会。
/// </summary>
public sealed class ModelProfile
{
    /// <summary>主密钥的 DPAPI 熵。**不要改**：改了旧配置里的密文就解不开了。</summary>
    internal const string KeyEntropy = "AnimaCaptioner.v1.ApiKey";

    /// <summary>稳定标识。活动指针（<see cref="AppSettings.ActiveProfileId"/>）指向它。</summary>
    public string Id { get; set; } = NewId();

    /// <summary>用户起的显示名。空着时用 <see cref="DisplayName"/> 从地址/模型名派生。</summary>
    public string Name { get; set; } = "";

    /// <summary>OpenAI 兼容接口的 base，例如 http://127.0.0.1:8080/v1 。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>模型名。服务端不实现 /models 时由用户手填。</summary>
    public string Model { get; set; } = "";

    /// <summary>DPAPI 密文，加密后 Base64。不要直接改这个字段。</summary>
    public string ApiKeyProtected { get; set; } = "";

    /// <summary>
    /// 推理档位（服务端的 `reasoning_effort`）。空串 = 自动（不下发、跟随服务端默认）。
    /// 取值见 <see cref="LlmClient.EffortVocabulary"/>。
    ///
    /// 存在站点上而不是全局：本地 llama-server 与云端要的档位正好相反
    /// （前者靠 enable_thinking 只认「关闭思考」，后者认 8 档强度）。
    /// </summary>
    public string ReasoningEffort { get; set; } = "none";

    /// <summary>新的随机标识。12 位十六进制足够区分（同时存在的站点只有个位数）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>明文 API Key。只在内存里，赋值时立刻加密写入 <see cref="ApiKeyProtected"/>。</summary>
    [JsonIgnore]
    public string ApiKey
    {
        get => _apiKey ??= DpapiSecret.TryUnprotect(ApiKeyProtected, KeyEntropy) ?? "";
        set
        {
            _apiKey = value ?? "";
            ApiKeyProtected = string.IsNullOrEmpty(_apiKey)
                ? ""
                : DpapiSecret.Protect(_apiKey, KeyEntropy);
        }
    }

    [JsonIgnore] private string? _apiKey;

    /// <summary>
    /// 丢掉明文缓存，下次读 <see cref="ApiKey"/> 时重新解密。
    ///
    /// 迁移必须调它：<see cref="ApiKey"/> 的 getter 只解一次并缓存（空密文也会
    /// 缓存成空串），而迁移是在反序列化**之后**才把旧密文搬过来的——不丢缓存的话
    /// 那份密钥读出来永远是空，看起来就像"迁移把密钥弄丢了"。
    /// </summary>
    internal void ForgetCachedKey() => _apiKey = null;

    /// <summary>密文存在但解不开（换了机器或账户）。界面据此提示重填，而不是静默当成没填。</summary>
    [JsonIgnore]
    public bool ApiKeyUnreadable =>
        !string.IsNullOrEmpty(ApiKeyProtected)
        && DpapiSecret.TryUnprotect(ApiKeyProtected, KeyEntropy) is null;

    /// <summary>
    /// 列表里显示的名字。没起名时依次回落到地址的主机名、模型名、占位文字——
    /// 空白行对上"这个站点是什么"毫无帮助，而主机名几乎总是能认出来的那一项。
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
            var host = HostOf(BaseUrl);
            if (host.Length > 0) return host;
            if (!string.IsNullOrWhiteSpace(Model)) return Model.Trim();
            return "未命名站点";
        }
    }

    /// <summary>列表里的副标题：模型名 · 档位。让用户不点开就能看出这条指向哪个模型。</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var model = string.IsNullOrWhiteSpace(Model) ? "（未填模型）" : Model.Trim();
            var effort = LlmClient.NormalizeEffort(ReasoningEffort);
            var label = effort.Length == 0 ? "自动" : effort;
            return model + " · " + label;
        }
    }

    /// <summary>
    /// 从地址里取主机名。手写解析而不上 <see cref="Uri"/>：用户常常先敲
    /// `api.deepseek.com`（没有 scheme）再补 `/v1`，那种串 `Uri` 解析不出来，
    /// 而"还没填完"正是最需要有个名字的时候。
    /// </summary>
    internal static string HostOf(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0) return "";

        var sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep >= 0) s = s[(sep + 3)..];

        var end = s.IndexOfAny(new[] { '/', '?', '#' });
        if (end >= 0) s = s[..end];

        // 去掉 user@host 与 :port
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        var colon = s.LastIndexOf(':');
        if (colon >= 0) s = s[..colon];

        return s.Trim();
    }

    /// <summary>深拷贝。设置对话框在副本上编辑，取消才不会改动真配置。</summary>
    public ModelProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseUrl = BaseUrl,
        Model = Model,
        ApiKeyProtected = ApiKeyProtected,
        ReasoningEffort = ReasoningEffort,
    };
}
