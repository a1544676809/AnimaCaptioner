using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>
/// 联网搜索客户端，供模型通过 function calling 调用。
///
/// 所有后端共用一套返回结构，上层工具循环不必关心用的是哪家：
///
/// - **shim**（默认）：192.168.1.4 上已部署的搜索网关。它对外是
///   Anthropic 兼容的 `POST /v1/messages`，`web_search_20250305` 这个
///   server tool 由自建 SearXNG 兑现，所以搜索在本机内网完成、不依赖外部 API。
/// - **searxng**：直连一个能访问的 SearXNG 实例的 JSON 接口。
/// - **tavily / brave / serper**：商业 API，只需填 Key。
///
/// 关于 shim 这一路，有两个**实测得出**、非试不可的细节（都踩过）：
///
/// 1. **必须覆盖 TLS SNI**。Caddy 按 SNI 选站点，直连 `192.168.1.4:49977` 时
///    SNI 会是那个 IP，Caddy 找不到站点，直接回 `TLS alert: InternalError`。
///    `SocketsHttpHandler.SslOptions.TargetHost` **不改变 SNI**（实测无效，
///    内层就是上面那个 alert）；只有自己建 `SslStream` 并
///    `AuthenticateAsClientAsync(TargetHost = ...)` 才管用，所以走 ConnectCallback。
/// 2. **`Host` 头也必须写成 SNI 名**。用 IP 当 Host 时 Caddy 返回
///    `200 OK` + `Content-Length: 0`——一个**静默的空响应**，既不报错也没内容，
///    排查起来毫无线索。
///
/// 另一个刻意的约束：**回灌给模型的内容必须截断**。8B 模型上下文和注意力都有限，
/// 把整页正文塞回去会把标签本身挤出视野，反而答得更差。所以每条只留标题 + 网址 +
/// 一段摘要，并按 <see cref="AppSettings.SearchMaxResults"/> 限条数。
/// </summary>
public sealed class SearchClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly HttpClient _shimHttp;

    /// <summary>当前这次请求要用的 SNI/Host 名。发请求前从配置取。</summary>
    private string _shimSni = "nas.ddger.top";

    /// <summary>单条摘要字符上限。够说明"这是什么"，又不至于淹没提示词。</summary>
    private const int SnippetChars = 400;

    /// <summary>回灌内容总字符上限，兜底防止多条长摘要叠加过大。</summary>
    private const int TotalChars = 2400;

    public string LastError { get; private set; } = "";

    public sealed record Hit(string Title, string Url, string Snippet);

    public sealed record SearchResult(bool Ok, List<Hit> Hits, string Error);

    public SearchClient()
    {
        // shim 走局域网 IP，但 TLS 握手要报 SNI 名、HTTP 还要带同名 Host。
        // 只能自己建流，HttpClient 的常规开关做不到（见类注释）。
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var sni = _shimSni;
                var tcp = new TcpClient();
                await tcp.ConnectAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct)
                         .ConfigureAwait(false);
                var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = sni,   // <- 关键：覆盖 SNI
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, ct).ConfigureAwait(false);
                }
                catch
                {
                    ssl.Dispose();
                    tcp.Dispose();
                    throw;
                }
                return ssl;
            },
        };
        _shimHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>把结果序列化成回灌给模型的 JSON。空结果也要如实说明，别让模型以为"没有就是不存在"。</summary>
    public static string ToToolContent(SearchResult r, string query)
    {
        if (!r.Ok)
            return JsonSerializer.Serialize(new { error = r.Error, query });

        if (r.Hits.Count == 0)
            return JsonSerializer.Serialize(new
            {
                query,
                note = "No results. The tag may be obscure, misspelled, or not a real tag.",
                results = Array.Empty<object>()
            });

        var budget = TotalChars;
        var items = new List<object>();
        foreach (var h in r.Hits)
        {
            if (budget <= 0) break;
            var snip = h.Snippet.Length <= SnippetChars ? h.Snippet : h.Snippet[..SnippetChars] + "…";
            if (snip.Length > budget) snip = snip[..Math.Max(0, budget)];
            budget -= snip.Length;
            items.Add(new { title = h.Title, url = h.Url, snippet = snip });
        }

        return JsonSerializer.Serialize(new { query, results = items },
                                       new JsonSerializerOptions
                                       {
                                           Encoder = System.Text.Encodings.Web.JavaScriptEncoder
                                                       .UnsafeRelaxedJsonEscaping
                                       });
    }

    public async Task<SearchResult> SearchAsync(AppSettings cfg, string query, CancellationToken ct = default)
    {
        LastError = "";
        query = (query ?? "").Trim();
        if (query.Length == 0) return new SearchResult(false, new(), "搜索词为空");

        _shimSni = string.IsNullOrWhiteSpace(cfg.SearchSniHost) ? "nas.ddger.top" : cfg.SearchSniHost.Trim();

        try
        {
            return cfg.SearchProvider?.ToLowerInvariant() switch
            {
                "tavily" => await TavilyAsync(cfg, query, ct).ConfigureAwait(false),
                "brave" => await BraveAsync(cfg, query, ct).ConfigureAwait(false),
                "serper" => await SerperAsync(cfg, query, ct).ConfigureAwait(false),
                "searxng" => await SearxngAsync(cfg, query, ct).ConfigureAwait(false),
                _ => await ShimAsync(cfg, query, ct).ConfigureAwait(false),
            };
        }
        catch (TaskCanceledException)
        {
            LastError = "搜索超时";
            return new SearchResult(false, new(), "搜索超时");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("search failed: " + ex);
            return new SearchResult(false, new(), ex.Message);
        }
    }

    // ---- shim：192.168.1.4 上已部署的 Anthropic 兼容搜索网关 ----

    /// <summary>
    /// 用 Anthropic 的 server tool 协议要搜索结果。
    ///
    /// 请求体里带上 `web_search_20250305` 工具，垫片会把 SearXNG 的结果放进
    /// `web_search_tool_result` 块。**摘要不在那个块里**——结果项只有
    /// url/title/page_age，正文在 `citations[].cited_text`，要靠 url 配对取回。
    /// 这个结构是照 Anthropic 官方 schema 做的，不配对就等于没有摘要可用。
    /// </summary>
    private async Task<SearchResult> ShimAsync(AppSettings cfg, string query, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(cfg.SearchBaseUrl)
            ? "https://192.168.1.4:49977"
            : cfg.SearchBaseUrl.Trim().TrimEnd('/');

        var body = new JsonObject
        {
            ["model"] = "searxng-shim",
            ["max_tokens"] = 1024,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = query }
            },
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search",
                    ["max_uses"] = 3,
                }
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
        };

        // Host 必须和 SNI 一致，否则 Caddy 回 200 + 空体（见类注释）
        req.Headers.Host = _shimSni;
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        var key = cfg.SearchApiKey?.Trim() ?? "";
        if (key.Length > 0)
        {
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var resp = await _shimHttp.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return Fail($"搜索网关 HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：" + Trunc(raw, 300));

        if (raw.Length == 0)
            return Fail("搜索网关返回了空响应——通常是 Host 头与 TLS SNI 不一致（Caddy 会静默返回 200 空体）");

        using var doc = JsonDocument.Parse(raw);

        // 先收摘要：url -> cited_text
        var snippets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("content", out var blocks) &&
            blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in blocks.EnumerateArray())
            {
                if (!b.TryGetProperty("citations", out var cits) ||
                    cits.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in cits.EnumerateArray())
                {
                    var u = Str(c, "url");
                    var t = Str(c, "cited_text");
                    if (u.Length > 0 && t.Length > 0) snippets.TryAdd(u, t);
                }
            }
        }

        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("content", out var blocks2) &&
            blocks2.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in blocks2.EnumerateArray())
            {
                if (Str(b, "type") != "web_search_tool_result") continue;
                if (!b.TryGetProperty("content", out var inner) ||
                    inner.ValueKind != JsonValueKind.Array) continue;

                foreach (var it in inner.EnumerateArray())
                {
                    if (hits.Count >= cfg.SearchMaxResults) break;
                    var url = Str(it, "url");
                    if (url.Length == 0) continue;
                    hits.Add(new Hit(Str(it, "title"), url,
                                     snippets.TryGetValue(url, out var s) ? s : ""));
                }
            }
        }

        Log.Write($"search shim q='{query}' hits={hits.Count} snips={snippets.Count} url={baseUrl}");
        return new SearchResult(true, hits, "");
    }

    // ---- SearXNG：直连实例的 JSON 接口，无需 Key ----

    private async Task<SearchResult> SearxngAsync(AppSettings cfg, string query, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(cfg.SearchBaseUrl)
            ? "http://127.0.0.1:8888"
            : cfg.SearchBaseUrl.Trim().TrimEnd('/');

        var url = $"{baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(cfg.SearchApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.SearchApiKey.Trim());

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // format=json 需要实例在 settings.yml 里开启 search.formats: [html, json]，
            // 没开时多返回 403。这个提示能省掉大量排查时间。
            var hint = (int)resp.StatusCode is 403 or 404
                ? "（SearXNG 需在 settings.yml 的 search.formats 里加上 json，否则 JSON 接口返回 403）"
                : "";
            return Fail($"SearXNG HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}{hint}");
        }

        using var doc = JsonDocument.Parse(raw);
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                if (hits.Count >= cfg.SearchMaxResults) break;
                hits.Add(new Hit(
                    Str(it, "title"),
                    Str(it, "url"),
                    FirstNonEmpty(Str(it, "content"), Str(it, "snippet"))));
            }

            // number_of_results 经常是 0 但 results 非空，所以不拿它当计数。
            // 引擎全挂时要说明白，否则"没结果"会被误当成"这标签不存在"。
            if (hits.Count == 0 && doc.RootElement.TryGetProperty("unresponsive_engines", out var ue) &&
                ue.ValueKind == JsonValueKind.Array && ue.GetArrayLength() > 0)
                return Fail("SearXNG 没有可用引擎（unresponsive_engines 非空），检查实例的代理与引擎配置");
        }

        Log.Write($"search searxng q='{query}' hits={hits.Count} url={baseUrl}");
        return new SearchResult(true, hits, "");
    }

    // ---- Tavily：Key 走 Authorization 头 ----

    private async Task<SearchResult> TavilyAsync(AppSettings cfg, string query, CancellationToken ct)
    {
        var key = cfg.SearchApiKey?.Trim() ?? "";
        if (key.Length == 0) return Fail("Tavily 需要 API Key");

        var body = new JsonObject
        {
            ["query"] = query,
            ["max_results"] = Math.Clamp(cfg.SearchMaxResults, 1, 10),
            ["include_answer"] = false,   // 只要素材，不让上游先替模型下结论
            ["search_depth"] = "basic",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return Fail($"Tavily HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：" + Trunc(raw, 300));

        using var doc = JsonDocument.Parse(raw);
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
                hits.Add(new Hit(Str(it, "title"), Str(it, "url"), Str(it, "content")));

        Log.Write($"search tavily q='{query}' hits={hits.Count}");
        return new SearchResult(true, hits, "");
    }

    // ---- Brave：Key 走 X-Subscription-Token 头 ----

    private async Task<SearchResult> BraveAsync(AppSettings cfg, string query, CancellationToken ct)
    {
        var key = cfg.SearchApiKey?.Trim() ?? "";
        if (key.Length == 0) return Fail("Brave Search 需要 API Key");

        var n = Math.Clamp(cfg.SearchMaxResults, 1, 20);
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={n}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("X-Subscription-Token", key);
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return Fail($"Brave HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：" + Trunc(raw, 300));

        using var doc = JsonDocument.Parse(raw);
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
                hits.Add(new Hit(Str(it, "title"), Str(it, "url"),
                                 FirstNonEmpty(Str(it, "description"), Str(it, "snippet"))));
        }

        Log.Write($"search brave q='{query}' hits={hits.Count}");
        return new SearchResult(true, hits, "");
    }

    // ---- Serper：Key 走 X-API-KEY 头，返回 Google 结果。注意字段名是 link/snippet ----

    private async Task<SearchResult> SerperAsync(AppSettings cfg, string query, CancellationToken ct)
    {
        var key = cfg.SearchApiKey?.Trim() ?? "";
        if (key.Length == 0) return Fail("Serper 需要 API Key");

        var body = new JsonObject
        {
            ["q"] = query,
            ["num"] = Math.Clamp(cfg.SearchMaxResults, 1, 20),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search")
        {
            Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
        };
        req.Headers.TryAddWithoutValidation("X-API-KEY", key);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return Fail($"Serper HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：" + Trunc(raw, 300));

        using var doc = JsonDocument.Parse(raw);
        var hits = new List<Hit>();
        if (doc.RootElement.TryGetProperty("organic", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
            {
                if (hits.Count >= cfg.SearchMaxResults) break;
                hits.Add(new Hit(Str(it, "title"), Str(it, "link"), Str(it, "snippet")));
            }

        Log.Write($"search serper q='{query}' hits={hits.Count}");
        return new SearchResult(true, hits, "");
    }

    /// <summary>连通性自检。用固定的无歧义查询词，避免被实例的引擎故障误导。</summary>
    public async Task<(bool Ok, string Message)> TestAsync(AppSettings cfg, CancellationToken ct = default)
    {
        var r = await SearchAsync(cfg, "Danbooru tag", ct).ConfigureAwait(false);
        if (!r.Ok) return (false, r.Error);
        if (r.Hits.Count == 0)
            return (false, "接口通了但没有返回结果——检查实例的引擎是否可用");
        var withSnip = r.Hits.Count(h => h.Snippet.Length > 0);
        return (true, $"连接成功，返回 {r.Hits.Count} 条（{withSnip} 条带摘要）。首条：{Trunc(r.Hits[0].Title, 60)}");
    }

    private SearchResult Fail(string m) { LastError = m; return new SearchResult(false, new(), m); }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Replace("\n", " ").Replace("\r", " ").Trim()
            : "";

    private static string FirstNonEmpty(params string[] xs) =>
        xs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose()
    {
        _http.Dispose();
        _shimHttp.Dispose();
    }
}
