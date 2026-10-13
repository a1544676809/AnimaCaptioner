using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnimaCaptioner.Core;

/// <summary>
/// 抓取上游素材时那些"错了也不会报错"的解析逻辑。刻意放在 Core 而不是设置页里，
/// 因为这里每一件事都做过就静默少数据——正是必须有回归测试覆盖的那类代码。
/// </summary>
public static class VocabFetch
{
    /// <summary>单页请求的条数上限。Danbooru 接受 1000。</summary>
    public const int PageLimit = 1000;

    /// <summary>防跑飞的上限。4.1 万条 / 1000 ≈ 42 页，200 页足够富余。</summary>
    public const int MaxPages = 200;

    /// <summary>把分页地址里的 <c>{0}</c> 换成页码。</summary>
    public static string PageUrl(string template, int page) =>
        string.Format(CultureInfo.InvariantCulture, template, page);

    /// <summary>
    /// 判断某一页是否已经到底。
    ///
    /// 判据是"这一页有没有带来**新**记录"，而不是"这一页是不是空的"：抓取期间上游会新增
    /// 数据，页面边界随之漂移，同一批记录可能在两页里重复出现。若只按条数判断，
    /// 一页全是重复就会误认为还有下一页（或反之提前收工）。
    /// </summary>
    public static bool IsLastPage(int rawCount, int newCount) =>
        newCount == 0 || rawCount < PageLimit;

    /// <summary>
    /// 把若干页 JSON 数组合并成一个完整数组。
    ///
    /// 按 <c>id</c> 去重——上游分页会漂移，重复记录是常态而非异常。缺少 <c>id</c> 字段的
    /// 记录不参与去重：把"没有 id"当成同一个 id 会把第二条起全部吞掉。
    ///
    /// 返回的是可以直接落盘的 JSON 文本；<paramref name="count"/> 是合并后的条数。
    /// 用原始文本片段拼接而不是反序列化再序列化：省一遍解析，也避免上游字段
    /// （如 <c>null</c> 与缺失的区别）在往返中被改写。
    /// </summary>
    public static string MergePagedJson(IEnumerable<string> pageBodies, out int count)
    {
        var seen = new HashSet<long>();
        var parts = new List<string>();

        foreach (var body in pageBodies)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { continue; }   // 坏页跳过，不因为一页毁掉整批

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    long id = 0;
                    if (el.ValueKind == JsonValueKind.Object &&
                        el.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.Number)
                        id = idEl.GetInt64();

                    if (id != 0 && !seen.Add(id)) continue;
                    parts.Add(el.GetRawText());
                }
            }
        }

        count = parts.Count;
        return "[" + string.Join(",", parts) + "]";
    }

    /// <summary>
    /// 某一页里带来了多少条**新**记录。与 <see cref="MergePagedJson"/> 共用同一套去重规则，
    /// 免得两处判断不一致（一个说还有下一页、另一个说全重复）。
    /// </summary>
    public static int CountNewInPage(string body, HashSet<long> seen, out int rawCount)
    {
        rawCount = 0;
        var added = 0;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return 0; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                rawCount++;
                long id = 0;
                if (el.ValueKind == JsonValueKind.Object &&
                    el.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.Number)
                    id = idEl.GetInt64();
                if (id != 0 && !seen.Add(id)) continue;
                added++;
            }
        }
        return added;
    }

    /// <summary>
    /// 建一个抓素材用的 <see cref="HttpClient"/>。
    ///
    /// <paramref name="proxy"/> 留空就是直连。用户代理**必须**显式给：上游是
    /// <c>raw.githubusercontent.com</c> 和 <c>danbooru.donmai.us</c>，
    /// 国内直连会超时，而系统的代理设置不一定会被 <see cref="HttpClient"/> 采用
    /// （实测本机 <c>GetSystemWebProxy</c> 返回直连、而直连不通）。
    /// User-Agent 也是必需的：Danbooru 对没有 UA 的请求直接回 403。
    /// </summary>
    public static HttpClient CreateClient(string proxy, TimeSpan? timeout = null)
    {
        HttpClient http;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            if (!Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var proxyUri))
                throw new ArgumentException("代理地址看不懂：" + proxy, nameof(proxy));
            http = new HttpClient(new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy(proxyUri),
                UseProxy = true
            })
            { Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        }
        else
        {
            http = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        }
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AnimaCaptioner/1.0");
        return http;
    }

    /// <summary>下载一个文件到指定路径。抛异常即失败，调用方负责决定要不要继续。</summary>
    public static async Task DownloadFileAsync(HttpClient http, string url, string dest,
        CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct);
    }

    /// <summary>
    /// 抓一个**分页**的 JSON 数组接口，拼成一个完整数组写进 <paramref name="dest"/>。
    ///
    /// 不能只抓第一页：Danbooru 的 <c>tag_aliases.json</c> 有 4.1 万条、单页最多 1000，
    /// 只拿首页不会报错——改名表会静默少掉 97%，症状是"旧写法查不到新标签"，
    /// 而不是任何可见的失败。
    /// </summary>
    public static async Task<int> DownloadPagedAsync(HttpClient http, string urlTemplate,
        string dest, Action<int>? progress = null, CancellationToken ct = default)
    {
        var seen = new HashSet<long>();
        var bodies = new List<string>();

        for (var page = 1; page <= MaxPages; page++)
        {
            var url = PageUrl(urlTemplate, page);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);

            var added = CountNewInPage(body, seen, out var raw);
            bodies.Add(body);
            progress?.Invoke(seen.Count);
            if (IsLastPage(raw, added)) break;
        }

        var merged = MergePagedJson(bodies, out var count);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllTextAsync(dest, merged, new UTF8Encoding(false), ct);
        return count;
    }

    /// <summary>下载清单里的一个文件；分页接口自动翻页。返回实际写入的记录数（非分页为 -1）。</summary>
    public static async Task<int> DownloadSourceAsync(HttpClient http, VocabSources.File f,
        string dest, Action<int>? progress = null, CancellationToken ct = default)
    {
        if (f.Paginated)
            return await DownloadPagedAsync(http, f.Url, dest, progress, ct);
        await DownloadFileAsync(http, f.Url, dest, ct);
        return -1;
    }
}
