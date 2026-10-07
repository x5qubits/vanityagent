using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

public sealed class WebSearchTool : ITool
{
    private static readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(25),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36" } },
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "web_search",
        Description = "Search the web, returns titles, URLs, snippets.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["type"] = "string" },
                ["limit"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max results (default 5)." },
            },
            ["required"] = new[] { "query" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root      = doc.RootElement;
        var query     = root.GetProperty("query").GetString() ?? "";
        var limit     = root.TryGetProperty("limit", out var l) ? Math.Clamp(l.GetInt32(), 1, 10) : 5;

        Log.Debug($"[web_search] {query}");

        var browser = BrowserLocator.FindBrowser();
        if (browser is not null)
        {
            var html = await DumpDomAsync(browser,
                "https://www.google.com/search?q=" + Uri.EscapeDataString(query) + "&num=10&hl=en&pws=0",
                ct).ConfigureAwait(false);
            if (html is not null && !LooksBlocked(html))
            {
                var results = ParseGoogle(html);
                if (results.Count > 0)
                    return FormatResults(results, limit);
            }
        }

        return await DuckDuckGoFallbackAsync(query, limit, ct).ConfigureAwait(false);
    }

    private static async Task<string?> DumpDomAsync(string browser, string url, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "vanityagent-search-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            var result = await ProcessRunner.RunAsync(browser,
            [
                "--headless=new", "--disable-gpu", "--no-first-run", "--disable-extensions",
                "--window-size=1280,1024", "--virtual-time-budget=6000", "--lang=en-US",
                "--user-data-dir=" + work, "--dump-dom", url,
            ], work, 30_000, ct).ConfigureAwait(false);
            return result.TimedOut || result.StdOut.Length < 1000 ? null : result.StdOut;
        }
        catch { return null; }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
            try
            {
                foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "vanityagent-search-*"))
                    if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddHours(-2))
                        try { Directory.Delete(d, true); } catch { }
            }
            catch { }
        }
    }

    private static bool LooksBlocked(string html) =>
        html.Contains("consent.google.", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("g-recaptcha", StringComparison.OrdinalIgnoreCase);

    internal static List<(string Title, string Url, string? Snippet)> ParseGoogle(string html)
    {
        var results = new List<(string, string, string?)>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match h3 in Regex.Matches(html, @"<h3[^>]*>(?<title>.*?)</h3>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var title = CleanText(h3.Groups["title"].Value);
            if (title.Length == 0) continue;

            var windowStart = Math.Max(0, h3.Index - 1500);
            var before      = html[windowStart..h3.Index];
            var href        = Regex.Matches(before, @"<a\s[^>]*?href=""(?<u>https?://[^""]+)""", RegexOptions.IgnoreCase)
                                   .Select(m => m.Groups["u"].Value).LastOrDefault();
            if (href is null) continue;
            var url = WebUtility.HtmlDecode(href);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (uri.Host.Contains("google", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(uri.AbsoluteUri)) continue;

            var after      = html[h3.Index..Math.Min(html.Length, h3.Index + 6000)];
            var snipMatch  = Regex.Match(after, @"class=""[^""]*VwiC3b[^""]*""[^>]*>(?<d>.*?)</(?:div|span)>",
                                         RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snippet    = snipMatch.Success ? CleanText(snipMatch.Groups["d"].Value) : null;
            results.Add((title, uri.AbsoluteUri, string.IsNullOrEmpty(snippet) ? null : snippet));
        }
        return results;
    }

    private async Task<string> DuckDuckGoFallbackAsync(string query, int limit, CancellationToken ct)
    {
        try
        {
            var url  = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            var html = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var results = ParseDuckDuckGo(html);
            if (results.Count == 0) return "No results.";
            return FormatResults(results, limit) + "\n\n[via DuckDuckGo]";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Search failed: {ex.Message}"; }
    }

    internal static List<(string Title, string Url, string? Snippet)> ParseDuckDuckGo(string html)
    {
        var results = new List<(string, string, string?)>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchors = Regex.Matches(html,
            @"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""(?<u>[^""]+)""[^>]*>(?<t>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var snippets = Regex.Matches(html,
            @"class=""[^""]*result__snippet[^""]*""[^>]*>(?<d>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        for (var i = 0; i < anchors.Count; i++)
        {
            var raw = WebUtility.HtmlDecode(anchors[i].Groups["u"].Value);
            if (raw.Contains("y.js", StringComparison.OrdinalIgnoreCase)) continue;
            var url = UnwrapUddg(raw);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (!seen.Add(uri.AbsoluteUri)) continue;
            var title   = CleanText(anchors[i].Groups["t"].Value);
            var snippet = i < snippets.Count ? CleanText(snippets[i].Groups["d"].Value) : null;
            if (title.Length == 0) continue;
            results.Add((title, uri.AbsoluteUri, string.IsNullOrEmpty(snippet) ? null : snippet));
        }
        return results;
    }

    private static string UnwrapUddg(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        var at = href.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return href;
        var value = href[(at + 5)..];
        var amp   = value.IndexOf('&');
        if (amp >= 0) value = value[..amp];
        return Uri.UnescapeDataString(value);
    }

    private static string FormatResults(List<(string Title, string Url, string? Snippet)> results, int limit)
    {
        var sb = new StringBuilder();
        foreach (var (title, url, snippet) in results.Take(limit))
        {
            sb.AppendLine($"**{title}**");
            sb.AppendLine(url);
            if (!string.IsNullOrEmpty(snippet)) sb.AppendLine(snippet);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string CleanText(string html)
    {
        var text = Regex.Replace(html, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
