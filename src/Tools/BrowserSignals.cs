using System.Text.Json.Nodes;

namespace VanityAgent.Tools;

/// <summary>
/// What the browser reports about a page while <c>page_view</c> renders it, from the CDP events the session already
/// receives: uncaught script errors (<c>Runtime.exceptionThrown</c>), <c>console.error</c> calls
/// (<c>Runtime.consoleAPICalled</c>), and resources that failed to load or came back 4xx/5xx
/// (<c>Network.responseReceived</c>, <c>Network.loadingFailed</c>). None of it is visible in a screenshot, all of it
/// is a defect the visitor meets, and it holds for any site, any stack, any asset. The report goes into the tool's
/// text next to the page digest, so the model reads it on every capture without being told to look.
/// </summary>
internal sealed class BrowserSignals
{
    private const int Cap = 8;
    private readonly List<string> _errors = new();     // uncaught exceptions and console.error, "message (file:line)"
    private readonly List<string> _failed = new();     // "404 assets/img/x.svg (Image)", "net::ERR_NAME_NOT_RESOLVED cdn.x/y.js (Script)"
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Url, string Type)> _requests = new(StringComparer.Ordinal);

    public void Take(JsonNode evt)
    {
        var method = evt["method"]?.GetValue<string>() ?? "";
        var p = evt["params"];
        if (p is null) return;
        switch (method)
        {
            case "Runtime.exceptionThrown":
            {
                var d = p["exceptionDetails"];
                var text = d?["exception"]?["description"]?.GetValue<string>() ?? d?["text"]?.GetValue<string>() ?? "";
                text = FirstLine(text);
                if (text.Length == 0) return;
                var url = Short(d?["url"]?.GetValue<string>() ?? d?["stackTrace"]?["callFrames"]?[0]?["url"]?.GetValue<string>() ?? "");
                var line = d?["lineNumber"]?.GetValue<int>() ?? d?["stackTrace"]?["callFrames"]?[0]?["lineNumber"]?.GetValue<int>() ?? -1;
                Add(_errors, text + (url.Length > 0 ? " (" + url + (line >= 0 ? ":" + (line + 1) : "") + ")" : ""));
                return;
            }
            case "Runtime.consoleAPICalled":
            {
                if (p["type"]?.GetValue<string>() != "error") return;
                var args = p["args"] as JsonArray;
                var text = args is null ? "" : string.Join(" ", args.Select(a => a?["value"]?.ToString() ?? a?["description"]?.GetValue<string>() ?? "").Where(s => s.Length > 0));
                text = FirstLine(text);
                if (text.Length == 0) return;
                var frame = p["stackTrace"]?["callFrames"]?[0];
                var url = Short(frame?["url"]?.GetValue<string>() ?? "");
                var line = frame?["lineNumber"]?.GetValue<int>() ?? -1;
                Add(_errors, "console.error: " + text + (url.Length > 0 ? " (" + url + (line >= 0 ? ":" + (line + 1) : "") + ")" : ""));
                return;
            }
            case "Network.requestWillBeSent":
            {
                var id = p["requestId"]?.GetValue<string>();
                if (id is null) return;
                _requests[id] = (p["request"]?["url"]?.GetValue<string>() ?? "", p["type"]?.GetValue<string>() ?? "");
                return;
            }
            case "Network.responseReceived":
            {
                var status = p["response"]?["status"]?.GetValue<int>() ?? 0;
                if (status < 400) return;
                var url = p["response"]?["url"]?.GetValue<string>() ?? "";
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
                Add(_failed, status + " " + Short(url) + Kind(p["type"]?.GetValue<string>()));
                return;
            }
            case "Network.loadingFailed":
            {
                var err = p["errorText"]?.GetValue<string>() ?? "";
                if (err.Length == 0 || err.Contains("ERR_ABORTED", StringComparison.Ordinal)) return;   // a navigation or a lazy load cancelled on purpose
                if (p["canceled"]?.GetValue<bool>() == true) return;
                var id = p["requestId"]?.GetValue<string>() ?? "";
                var (url, type) = _requests.TryGetValue(id, out var r) ? r : ("", p["type"]?.GetValue<string>() ?? "");
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
                Add(_failed, err + (url.Length > 0 ? " " + Short(url) : "") + Kind(type));
                return;
            }
        }
    }

    /// <summary>Empty when the browser had nothing to say; else one or two lines in the layout-check voice.</summary>
    public string Report()
    {
        var sb = new System.Text.StringBuilder();
        if (_errors.Count > 0)
            sb.Append("[browser console] ").Append(_errors.Count).Append(" script error(s): ").Append(string.Join("; ", _errors))
              .Append(". An uncaught error stops the rest of that script: menus, forms, autofill and anything it drives may be dead on the page. Fix the script (the file and line are named), then verify again.");
        if (_failed.Count > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("[network] ").Append(_failed.Count).Append(" request(s) failed: ").Append(string.Join("; ", _failed))
              .Append(". A missing image is a broken icon or an empty space on the page; a missing script or stylesheet breaks what depends on it. Check the path and the file on the server (curl -I).");
        }
        return sb.ToString();
    }

    public int ErrorCount => _errors.Count;
    public int FailedCount => _failed.Count;

    private void Add(List<string> list, string entry)
    {
        if (list.Count >= Cap || !_seen.Add(entry)) return;
        list.Add(entry);
    }

    private static string FirstLine(string s)
    {
        s = s.Trim();
        int nl = s.IndexOf('\n');
        if (nl > 0) s = s[..nl].TrimEnd();
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    /// <summary>The path without scheme and host: enough to find the file, short enough to list eight of them.</summary>
    private static string Short(string url)
    {
        if (url.Length == 0) return "";
        try
        {
            var u = new Uri(url);
            var path = u.AbsolutePath.TrimStart('/');
            if (path.Length == 0) path = "/";
            return path.Length > 70 ? "…" + path[^70..] : path;
        }
        catch { return url.Length > 70 ? "…" + url[^70..] : url; }
    }

    private static string Kind(string? type) => string.IsNullOrEmpty(type) || type == "Other" ? "" : " (" + type + ")";
}
