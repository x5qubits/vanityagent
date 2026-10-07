using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

public sealed class WebFetchTool : ITool
{
    private const int InlineCap    = 16_000;
    private const int MaxReadChars = 500_000;
    private const int MaxImageBytes = 20 * 1024 * 1024;

    private readonly string  _workspace;
    private readonly HttpClient _http;

    public WebFetchTool(string? workspace = null)
    {
        _workspace = workspace ?? Directory.GetCurrentDirectory();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect        = false,
            AutomaticDecompression   = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,*/*;q=0.8");
    }

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "web_fetch",
        Description = "Fetch a URL and return readable text. Links and headings preserved.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["url"]        = new Dictionary<string, object> { ["type"] = "string" },
                ["links_only"] = new Dictionary<string, object> { ["type"] = "boolean",  ["description"] = "Return only links, no body." },
                ["link_text"]  = new Dictionary<string, object> { ["type"] = "string",   ["description"] = "Filter links by text/href substring." },
                ["save_image"] = new Dictionary<string, object> { ["type"] = "string",   ["description"] = "Image URL to download." },
                ["save_as"]    = new Dictionary<string, object> { ["type"] = "string",   ["description"] = "Destination path for save_image or save_pdf." },
                ["save_text"]  = new Dictionary<string, object> { ["type"] = "string",   ["description"] = "File path to save full page text." },
                ["save_pdf"]    = new Dictionary<string, object> { ["type"] = "string",   ["description"] = "URL of a PDF to download and save (use save_as for destination path). Note: blob: URLs cannot be fetched server-side." },
                ["show_images"] = new Dictionary<string, object> { ["type"] = "boolean",  ["description"] = "Include image URLs found on the page (default false)." },
                ["timeout_ms"]  = new Dictionary<string, object> { ["type"] = "integer",  ["description"] = "ms, default 30000." },
            },
            ["required"] = new[] { "url" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc   = JsonDocument.Parse(argsJson);
        var root        = doc.RootElement;
        var rawUrl      = root.GetProperty("url").GetString()?.Trim() ?? "";
        var linksOnly   = root.TryGetProperty("links_only",  out var lo) && lo.GetBoolean();
        var linkText    = root.TryGetProperty("link_text",   out var lt) ? lt.GetString() ?? "" : "";
        var saveImage   = root.TryGetProperty("save_image",  out var si) ? si.GetString()?.Trim() ?? "" : "";
        var saveAs      = root.TryGetProperty("save_as",     out var sa) ? sa.GetString()?.Trim() ?? "" : "";
        var saveText    = root.TryGetProperty("save_text",   out var st) ? st.GetString()?.Trim() ?? "" : "";
        var savePdf     = root.TryGetProperty("save_pdf",    out var sp) ? sp.GetString()?.Trim() ?? "" : "";
        var showImages  = root.TryGetProperty("show_images", out var im) && im.GetBoolean();
        var timeoutMs   = root.TryGetProperty("timeout_ms",  out var tm) ? tm.GetInt32() : 30_000;

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return $"Error: invalid URL: {rawUrl}";

        // Upgrade HTTP → HTTPS
        if (uri.Scheme == Uri.UriSchemeHttp)
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
        else if (uri.Scheme != Uri.UriSchemeHttps)
            return $"Error: unsupported scheme '{uri.Scheme}'. Only http/https supported.";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        // ── Download PDF to disk ──
        if (!string.IsNullOrEmpty(savePdf) && !string.IsNullOrEmpty(saveAs))
        {
            if (savePdf.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                return "Error: blob: URLs are browser-local and cannot be fetched server-side. Use page_view with JS to extract the content, or find the direct PDF URL.";
            if (!Uri.TryCreate(savePdf, UriKind.Absolute, out var pdfUri))
                return "Error: save_pdf must be an absolute http(s) URL.";
            var (pdfBytes, pdfTooBig) = await DownloadCappedAsync(pdfUri, cts.Token).ConfigureAwait(false);
            if (pdfTooBig) return "Error: PDF over 20 MB — skipped.";
            if (pdfBytes.Length == 0) return "Error: PDF URL returned nothing.";
            var dest = ResolvePath(saveAs);
            if (dest is null) return "Error: save_as path is outside the workspace or in a protected directory.";
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, pdfBytes, cts.Token).ConfigureAwait(false);
            return $"Saved PDF {savePdf} → {dest} ({pdfBytes.Length / 1024} KB).";
        }

        // ── Download image to disk ──
        if (!string.IsNullOrEmpty(saveImage) && !string.IsNullOrEmpty(saveAs))
        {
            if (!Uri.TryCreate(saveImage, UriKind.Absolute, out var imgUri))
                return "Error: save_image must be an absolute http(s) URL.";
            var (bytes, tooBig) = await DownloadCappedAsync(imgUri, cts.Token).ConfigureAwait(false);
            if (tooBig) return "Error: image over 20 MB — skipped.";
            if (bytes.Length == 0) return "Error: image URL returned nothing.";
            var dest = ResolvePath(saveAs);
            if (dest is null) return "Error: save_as path is outside the workspace or in a protected directory.";
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, bytes, cts.Token).ConfigureAwait(false);
            return $"Saved {saveImage} → {dest} ({bytes.Length / 1024} KB).";
        }

        // ── Fetch page ──
        Log.Debug($"[web_fetch] GET {uri}");
        var (body, mediaType, redirect) = await FetchAsync(uri, cts.Token).ConfigureAwait(false);

        if (redirect is not null)
            return $"Redirected to {redirect} — call web_fetch again with that URL.";

        var isPdf  = mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase);
        var isHtml = !isPdf && (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                  || mediaType.Contains("xml",  StringComparison.OrdinalIgnoreCase)
                  || mediaType.Length == 0);
        if (isPdf)
            return $"URL: {uri.AbsoluteUri}\n[PDF content detected — use save_pdf with save_as to download it, or page_view to render it in a browser.]";

        var title = isHtml && Regex.Match(body, @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline) is { Success: true } m
            ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";

        var full = isHtml ? HtmlToText(body) : body;

        // ── links_only mode ──
        if (linksOnly)
        {
            var links = ExtractLinks(body)
                .Where(l => linkText.Length == 0 || l.IndexOf(linkText, StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct(StringComparer.Ordinal).ToList();
            return $"Source: {uri.AbsoluteUri}\nLinks: {links.Count}\n" +
                   (links.Count == 0 ? "No matching links." : string.Join("\n", links));
        }

        // ── save_text ──
        string? savedNote = null;
        if (!string.IsNullOrEmpty(saveText))
        {
            try
            {
                var dest = ResolvePath(saveText);
                if (dest is null)
                {
                    savedNote = "save_text failed: path is outside the workspace or in a protected directory.";
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllTextAsync(dest, full, new UTF8Encoding(false), cts.Token).ConfigureAwait(false);
                    savedNote = $"Full text ({full.Length:N0} chars) saved to '{saveText}'.";
                }
            }
            catch (Exception ex) { savedNote = $"save_text failed: {ex.Message}"; }
        }

        var text      = full.Length <= InlineCap ? full : full[..InlineCap];
        var truncated = full.Length > text.Length;

        var sb = new StringBuilder();
        if (title.Length > 0) sb.AppendLine($"Title: {title}");
        sb.AppendLine($"URL: {uri.AbsoluteUri}");
        sb.AppendLine();
        sb.AppendLine(text.Length > 0 ? text : "(no readable text)");
        if (truncated)
            sb.AppendLine($"\n[truncated at {InlineCap} of {full.Length:N0} chars — pass save_text=<path> to get full content]");
        if (savedNote is not null) { sb.AppendLine(); sb.AppendLine(savedNote); }
        if (showImages && isHtml)
        {
            var images = ExtractImageUrls(body, uri).Take(5).ToList();
            if (images.Count > 0)
            {
                sb.AppendLine("\nImages (use save_image+save_as to download):");
                foreach (var img in images) sb.AppendLine("- " + img);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<(string Body, string MediaType, string? Redirect)> FetchAsync(Uri uri, CancellationToken ct)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            using var response = await SendWithRetryAsync(uri, ct).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } loc)
            {
                var target = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                // Cross-host redirect: return to caller
                if (!string.Equals(target.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                    return ("", "", target.AbsoluteUri);
                uri = target;
                continue;
            }

            if (!response.IsSuccessStatusCode)
                return ($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", "", null);

            var body      = await ReadCappedAsync(response, ct).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            return (body, mediaType, null);
        }
        return ("Error: too many redirects.", "", null);
    }

    // One retry on transport faults — avoids agent retrying manually across many turns
    private async Task<HttpResponseMessage> SendWithRetryAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            return await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException
                                   && !ct.IsCancellationRequested)
        {
            await Task.Delay(400, ct).ConfigureAwait(false);
            return await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sb    = new StringBuilder();
        var chunk = new char[16384];
        int read;
        try
        {
            while (sb.Length < MaxReadChars && (read = await reader.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                sb.Append(chunk, 0, Math.Min(read, MaxReadChars - sb.Length));
        }
        catch (IOException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); }
        catch (IOException) { } // connection dropped — return what we read so far
        return sb.ToString();
    }

    private async Task<(byte[] Bytes, bool TooBig)> DownloadCappedAsync(Uri uri, CancellationToken ct)
    {
        // Use SendWithRetryAsync so a transient transport fault gets one retry before failing
        using var response = await SendWithRetryAsync(uri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxImageBytes) return ([], true);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buf    = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        try
        {
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buf.Length + read > MaxImageBytes) return ([], true);
                buf.Write(chunk, 0, read);
            }
        }
        catch (IOException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); }
        catch (IOException ex) { throw new HttpRequestException("Download interrupted mid-stream: " + ex.Message, ex); }
        return (buf.ToArray(), false);
    }

    private string? ResolvePath(string path)
    {
        try
        {
            var full = Infra.VanityPathHelper.NormalizeAndResolveStrict(path, _workspace);
            return Infra.VanityPathHelper.IsDeniedForAgent(full, _workspace, out _) ? null : full;
        }
        catch { return null; }
    }

    // ── HTML extraction ──────────────────────────────────────────────────────

    internal static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        const RegexOptions ro = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        var s = html;
        s = Regex.Replace(s, @"<script\b.*?</script>",   " ", ro);
        s = Regex.Replace(s, @"<style\b.*?</style>",     " ", ro);
        s = Regex.Replace(s, @"<noscript\b.*?</noscript>"," ", ro);
        s = Regex.Replace(s, @"<!--.*?-->",              " ", ro);
        // Preserve anchor text + href
        s = Regex.Replace(s, @"<a\b[^>]*\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))[^>]*>(.*?)</a\s*>", m =>
        {
            var href  = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            var label = m.Groups[4].Value;
            href = WebUtility.HtmlDecode(href).Trim();
            return href.Length > 0 ? $"{label} ({href})" : label;
        }, ro);
        s = Regex.Replace(s, @"<h1[^>]*>", "\n# ",    ro);
        s = Regex.Replace(s, @"<h2[^>]*>", "\n## ",   ro);
        s = Regex.Replace(s, @"<h3[^>]*>", "\n### ",  ro);
        s = Regex.Replace(s, @"<h4[^>]*>", "\n#### ", ro);
        s = Regex.Replace(s, @"<h5[^>]*>", "\n##### ",ro);
        s = Regex.Replace(s, @"<h6[^>]*>", "\n###### ",ro);
        s = Regex.Replace(s, @"<li[^>]*>", "\n- ",    ro);
        s = Regex.Replace(s, @"<t[dh][^>]*>", " | ",  ro);
        s = Regex.Replace(s, @"<(br|/p|/div|/h[1-6]|/tr|/li)[^>]*>", "\n", ro);
        s = Regex.Replace(s, @"<[^>]+>", "", ro);
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    internal static IEnumerable<string> ExtractLinks(string html)
    {
        const RegexOptions ro = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        var clean = Regex.Replace(html ?? "", @"<(script|style)\b[^>]*>.*?</\1>|<!--.*?-->", "", ro);
        foreach (Match a in Regex.Matches(clean, @"<a\b[^>]*\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))[^>]*>(.*?)</a\s*>", ro))
        {
            var href  = WebUtility.HtmlDecode(a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value).Trim();
            if (href.Length == 0 || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var label = WebUtility.HtmlDecode(Regex.Replace(a.Groups[4].Value, "<[^>]+>", " ", ro));
            label = Regex.Replace(label, @"\s+", " ").Trim();
            yield return (label.Length > 0 ? label : "(unlabelled)") + " -> " + href;
        }
    }

    private static List<string> ExtractImageUrls(string html, Uri baseUri)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match m in Regex.Matches(html, @"<img\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase))
        {
            var src = m.Groups[1].Value.Trim();
            if (src.Length == 0 || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(baseUri, src, out var abs) && abs.Scheme is "http" or "https" && seen.Add(abs.AbsoluteUri))
                result.Add(abs.AbsoluteUri);
        }
        return result;
    }
}
