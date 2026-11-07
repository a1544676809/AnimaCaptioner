using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnimaCaptioner.Core;

/// <summary>
/// Ported from <c>AnimaAIProxy/Core/ArticleExtractor.cs</c> and <c>AnimaAIProxy/Core/PageReader.cs</c>.
/// Fetches and extracts clean readable Markdown/text from Character Wikis, Moegirl, Danbooru Wiki,
/// and reference pages to ground character appearance tags and lore during AI captioning.
/// </summary>
public static partial class ArticleExtractor
{
    public sealed record PageContent(
        string Title,
        string Description,
        string Markdown,
        bool Truncated);

    public static PageContent Extract(string html, string baseUrl, int maxChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new PageContent(string.Empty, string.Empty, string.Empty, false);

        string title = ExtractTagContent(html, "title");
        string description = ExtractMetaDescription(html);

        // Strip scripts, styles, nav, header, footer, svg, noscript
        string body = StripNoiseBlocksRegex().Replace(html, " ");

        // Convert headings, list items, paragraphs, and line breaks to lightweight Markdown structure
        body = HeadingRegex().Replace(body, m => $"\n\n### {StripTags(m.Groups[2].Value).Trim()}\n");
        body = ListItemRegex().Replace(body, m => $"\n- {StripTags(m.Groups[1].Value).Trim()}");
        body = BlockBreakRegex().Replace(body, "\n");

        // Strip remaining HTML tags and decode entities
        string plain = WebUtility.HtmlDecode(StripTags(body));
        plain = CollapseSpacesRegex().Replace(plain, " ");
        plain = CollapseNewlinesRegex().Replace(plain, "\n\n").Trim();

        bool truncated = false;
        if (maxChars > 0 && plain.Length > maxChars)
        {
            plain = plain[..maxChars].TrimEnd() + "\n...[truncated]";
            truncated = true;
        }

        return new PageContent(
            Title: WebUtility.HtmlDecode(title).Trim(),
            Description: WebUtility.HtmlDecode(description).Trim(),
            Markdown: plain,
            Truncated: truncated);
    }

    private static string ExtractTagContent(string html, string tag)
    {
        var match = Regex.Match(
            html,
            $@"<{tag}[^>]*>(.*?)</{tag}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? StripTags(match.Groups[1].Value) : string.Empty;
    }

    private static string ExtractMetaDescription(string html)
    {
        var match = MetaDescRegex().Match(html);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string StripTags(string input) =>
        HtmlTagRegex().Replace(input, " ");

    [GeneratedRegex(@"<(script|style|noscript|svg|nav|footer|header|aside|iframe)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StripNoiseBlocksRegex();

    [GeneratedRegex(@"<h([1-4])\b[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<li\b[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"</?(p|div|section|article|tr|br|hr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<meta\b[^>]*name=[""']description[""'][^>]*content=[""']([^""']*)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaDescRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex CollapseSpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex CollapseNewlinesRegex();
}

/// <summary>
/// Asynchronous web page reader supporting the <c>web_fetch</c> agent tool.
/// </summary>
public sealed class PageReader : IDisposable
{
    public sealed record FetchResult(
        bool Ok,
        string Url,
        string Title,
        string Text,
        string Engine,
        string Error,
        bool Truncated,
        string Note);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public PageReader(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AnimaCaptioner/2.0");
            _ownsClient = true;
        }
    }

    public async Task<FetchResult> ReadAsync(
        string url,
        int maxChars = 4000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new FetchResult(false, url ?? string.Empty, string.Empty, string.Empty, "http", "Invalid HTTP/HTTPS URL.", false, string.Empty);
        }

        if (IsPrivateOrLoopbackHost(uri.Host))
        {
            return new FetchResult(false, uri.ToString(), string.Empty, string.Empty, "http", "Blocked local/private network address.", false, string.Empty);
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new FetchResult(
                    false,
                    uri.ToString(),
                    string.Empty,
                    string.Empty,
                    "http",
                    $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
                    false,
                    string.Empty);
            }

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var extracted = ArticleExtractor.Extract(html, uri.ToString(), Math.Clamp(maxChars, 500, 16000));
            return new FetchResult(
                Ok: true,
                Url: uri.ToString(),
                Title: extracted.Title,
                Text: extracted.Markdown,
                Engine: "ArticleExtractor",
                Error: string.Empty,
                Truncated: extracted.Truncated,
                Note: extracted.Description);
        }
        catch (Exception ex)
        {
            return new FetchResult(false, uri.ToString(), string.Empty, string.Empty, "http", ex.Message, false, string.Empty);
        }
    }

    public async Task<string> ExecuteReadPageToolAsync(string jsonArguments, CancellationToken ct = default)
    {
        try
        {
            var node = JsonNode.Parse(jsonArguments) as JsonObject;
            var url = node?["url"]?.GetValue<string>() ?? string.Empty;
            int maxChars = node?["max_chars"]?.GetValue<int>() ?? 4000;

            var result = await ReadAsync(url, maxChars, ct).ConfigureAwait(false);
            var payload = new JsonObject
            {
                ["ok"] = result.Ok,
                ["url"] = result.Url,
                ["title"] = result.Title,
                ["text"] = result.Text,
                ["truncated"] = result.Truncated,
                ["error"] = result.Error
            };
            return payload.ToJsonString();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    private static bool IsPrivateOrLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
                return true;

            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
            {
                if (bytes[0] == 10 ||
                    bytes[0] == 127 ||
                    (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168) ||
                    (bytes[0] == 169 && bytes[1] == 254))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
