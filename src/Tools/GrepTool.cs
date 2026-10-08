using System.Text.Json;
using System.Text.RegularExpressions;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

/// <summary>Content search with ripgrep conventions.
/// Why a tool and not `bash grep -rn`: a shell grep over a site returned 20,000 characters with the decisive line at
/// character 16,000, past the shell output cap, so the model never saw it and searched 30 more times with other
/// words (2026-09-21). This returns compact, capped, ordered results (newest file first), excludes the harness's
/// own folder and build output, and says how much was left out. Output: "path:line:text" for matches,
/// "path-line-text" for context lines.</summary>
public sealed class GrepTool : ITool
{
    public const int DefaultHeadLimit = 250;
    private const int MaxFileBytes = 10 * 1024 * 1024;
    /// <summary>Largest content result returned inline; above it the whole result is saved and previewed. Below the
    /// history sanitizer's 24k cap, so what comes back is never cut a second time.</summary>
    private const int MaxInlineChars = 50_000;
    private const int PreviewChars = 2_048;
    private readonly string _workspace;
    private bool _singleFile;

    public GrepTool(string workspace) => _workspace = Path.GetFullPath(workspace);

    /// <summary>Writes an oversized result under .vanity/scratch/grep (agent-readable, never deployed) and returns
    /// its workspace-relative path. Dumps older than a day are removed on the way, so the folder stays small.</summary>
    private string SaveOversized(string text)
    {
        var dir = Path.Combine(AgentConfig.ScratchRoot, "grep");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.txt"))
            try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1)) File.Delete(old); } catch { }
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant()}.txt";
        var fullPath = Path.Combine(dir, name);
        File.WriteAllText(fullPath, text, new System.Text.UTF8Encoding(false));
        return fullPath;
    }

    private static readonly Dictionary<string, string[]> TypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = [".cs"], ["csharp"] = [".cs"], ["js"] = [".js", ".jsx", ".mjs", ".cjs"], ["ts"] = [".ts", ".tsx"], ["py"] = [".py"],
        ["java"] = [".java"], ["go"] = [".go"], ["rust"] = [".rs"], ["rb"] = [".rb"], ["php"] = [".php"], ["html"] = [".html", ".htm"],
        ["css"] = [".css", ".scss", ".less"], ["json"] = [".json"], ["xml"] = [".xml", ".csproj", ".props", ".targets"], ["yaml"] = [".yaml", ".yml"],
        ["md"] = [".md", ".markdown"], ["sh"] = [".sh", ".bash"], ["ps1"] = [".ps1", ".psm1", ".psd1"], ["c"] = [".c", ".h"],
        ["cpp"] = [".cpp", ".cc", ".cxx", ".hpp", ".hh"], ["sql"] = [".sql"], ["kotlin"] = [".kt", ".kts"], ["swift"] = [".swift"],
        ["toml"] = [".toml"], ["txt"] = [".txt"], ["twig"] = [".twig"], ["tpl"] = [".tpl"],
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "grep",
        Description = "A powerful search tool built on ripgrep\n\n" +
                      "  Usage:\n" +
                      "  - ALWAYS use Grep for search tasks. NEVER invoke `grep` or `rg` as a Bash command. The Grep tool has been optimized for correct permissions and access.\n" +
                      "  - To search for multiple terms in one call, use regex alternation: term1|term2|term3\n" +
                      "  - Supports full regex syntax (e.g. \"log.*Error\", \"function\\s+\\w+\")\n" +
                      "  - Filter files with glob parameter (e.g. \"*.js\", \"**/*.tsx\") or type parameter (e.g. \"js\", \"py\", \"rust\")\n" +
                      "  - Output modes: \"content\" shows matching lines (default), \"files_with_matches\" shows only file paths, \"count\" shows match counts\n" +
                      "  - Use Agent tool (if available) for open-ended searches requiring multiple rounds\n" +
                      "  - Pattern syntax: Uses ripgrep (not grep) - literal braces need escaping (use `interface\\{\\}` to find `interface{}` in Go code)\n" +
                      "  - Multiline matching: By default patterns match within single lines only. For cross-line patterns like `struct \\{[\\s\\S]*?field`, use `multiline: true`",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["pattern"]     = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "The regular expression pattern to search for in file contents" },
                ["path"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "File or directory to search in (rg PATH). Defaults to current working directory." },
                ["glob"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "Glob pattern to filter files (e.g. \"*.js\", \"*.{ts,tsx}\") - maps to rg --glob" },
                ["type"]        = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "File type to search (rg --type). Common types: js, py, rust, go, java, etc. More efficient than include for standard file types." },
                ["output_mode"] = new Dictionary<string, object> { ["type"] = "string",  ["enum"] = new[] { "content", "files_with_matches", "count" }, ["description"] = "Output mode: \"content\" shows matching lines (supports -A/-B/-C context, -n line numbers, head_limit), \"files_with_matches\" shows file paths (supports head_limit), \"count\" shows match counts (supports head_limit). Defaults to \"content\"." },
                ["-i"]          = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Case insensitive search (rg -i)" },
                ["-n"]          = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Show line numbers in output (rg -n). Requires output_mode: \"content\", ignored otherwise. Defaults to true." },
                ["-A"]          = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of lines to show after each match (rg -A). Requires output_mode: \"content\", ignored otherwise." },
                ["-B"]          = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of lines to show before each match (rg -B). Requires output_mode: \"content\", ignored otherwise." },
                ["-C"]          = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Alias for context." },
                ["context"]     = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Number of lines to show before and after each match (rg -C). Requires output_mode: \"content\", ignored otherwise." },
                ["-o"]          = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Print only the matched (non-empty) parts of each matching line, one match per output line (rg -o / --only-matching). Requires output_mode: \"content\", ignored otherwise. Defaults to false." },
                ["head_limit"]  = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Limit output to first N lines/entries, equivalent to \"| head -N\". Works across all output modes: content (limits output lines), files_with_matches (limits file paths), count (limits count entries). Defaults to 250 when unspecified. Pass 0 for unlimited (use sparingly - large result sets waste context)." },
                ["offset"]      = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Skip first N lines/entries before applying head_limit, equivalent to \"| tail -n +N | head -N\". Works across all output modes. Defaults to 0." },
                ["multiline"]   = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Enable multiline mode where . matches newlines and patterns can span lines (rg -U --multiline-dotall). Default: false." },
            },
            ["required"] = new[] { "pattern" },
        },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException ex) { return Task.FromResult("Error: arguments are not valid JSON: " + ex.Message); }
        string? S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool B(string k, bool dflt = false) => root.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)) : dflt;
        int? N(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n) ? n : null);

        var pattern = S("pattern") ?? "";
        if (pattern.Length == 0) return Task.FromResult("Error: pattern is required.");
        // ripgrep writes a code point as \x{2014}; .NET wants \u2014.
        pattern = Regex.Replace(pattern, @"\\x\{([0-9A-Fa-f]{1,4})\}", m => @"\u" + m.Groups[1].Value.PadLeft(4, '0'));

        string searchPath;
        try { searchPath = string.IsNullOrWhiteSpace(S("path")) || S("path") == "." ? _workspace : VanityPathHelper.NormalizeAndResolveStrict(S("path")!, _workspace); }
        catch (Exception ex) { return Task.FromResult("Error: " + ex.Message); }
        if (VanityPathHelper.IsDeniedForAgent(searchPath, _workspace, out var scopeMsg)) return Task.FromResult(scopeMsg);

        var globFilter = string.IsNullOrWhiteSpace(S("glob")) ? null : S("glob");
        var typeFilter = string.IsNullOrWhiteSpace(S("type")) ? null : S("type");
        var outputMode = S("output_mode") is { Length: > 0 } om ? om : "content";
        var ignoreCase = B("-i");
        var showLineNumbers = B("-n", true);
        var multiline = B("multiline");
        var onlyMatching = B("-o");
        var context = N("context") ?? N("-C") ?? 0;
        var before = N("-B") ?? context;
        var after = N("-A") ?? context;
        var headLimit = N("head_limit") ?? (outputMode == "content" ? 80 : DefaultHeadLimit);
        var offset = Math.Max(0, N("offset") ?? 0);
        if (headLimit <= 0) headLimit = int.MaxValue;

        Regex regex;
        try
        {
            var regexOptions = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (ignoreCase) regexOptions |= RegexOptions.IgnoreCase;
            if (multiline) regexOptions |= RegexOptions.Singleline | RegexOptions.Multiline;
            regex = new Regex(pattern, regexOptions, TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException ex) { return Task.FromResult($"Error: invalid regex pattern: {ex.Message}"); }

        string[]? typeExts = null;
        if (typeFilter is not null && !TypeExtensions.TryGetValue(typeFilter, out typeExts))
            return Task.FromResult($"Error: unknown file type '{typeFilter}'. Known types: {string.Join(", ", TypeExtensions.Keys.OrderBy(k => k))}");

        var globRegex = globFilter is null ? null : GlobMatcher.ToRegex(globFilter, caseInsensitive: true);
        var globOnFileNameOnly = globFilter is not null && !globFilter.Contains('/') && !globFilter.Contains('\\');

        List<FileInfo> candidates;
        _singleFile = File.Exists(searchPath);
        if (_singleFile) candidates = [new FileInfo(searchPath)];
        else if (Directory.Exists(searchPath))
        {
            candidates = FileWalker.EnumerateFiles(searchPath)
                .Where(f =>
                {
                    if (typeExts is not null && !typeExts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)) return false;
                    if (globRegex is not null)
                    {
                        var subject = globOnFileNameOnly ? f.Name : Path.GetRelativePath(searchPath, f.FullName).Replace('\\', '/');
                        if (!globRegex.IsMatch(subject)) return false;
                    }
                    return !VanityPathHelper.IsDeniedForAgent(f.FullName, _workspace, out _);
                })
                .ToList();
        }
        else return Task.FromResult($"Error: path does not exist: {Rel(searchPath)}");

        var skippedBig = 0;
        var scanned = candidates
            .AsParallel().WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount)).WithCancellation(ct)
            .Select(file =>
            {
                if (file.Length > MaxFileBytes) { Interlocked.Increment(ref skippedBig); return null; }
                return ScanFile(file, regex, multiline, onlyMatching, before, after, showLineNumbers, outputMode);
            })
            .Where(r => r is not null).Select(r => r!)
            .OrderByDescending(r => r.LastWrite)
            .ToList();

        // A small content result is what a reader wants to see the bodies of (a selector's rules, a function): with
        // no context asked, the matches are re-read with 8 lines around each, so the definitions arrive in this
        // call instead of in one read_file window per hit. A large result stays a list.
        var autoExpanded = false;
        if (outputMode == "content" && context == 0 && N("-A") is null && N("-B") is null)
        {
            var total = scanned.Sum(r => r.MatchCount);
            if (total > 0 && total <= 12)
            {
                autoExpanded = true;
                headLimit = Math.Max(headLimit, 12 * 17 + 24);   // every match with its 8+8 lines must fit the cap
                scanned = candidates
                    .Where(f => f.Length <= MaxFileBytes && (_singleFile || scanned.Any(r => r.Path == Rel(f.FullName))))
                    .Select(file => ScanFile(file, regex, multiline, onlyMatching, 8, 8, showLineNumbers, outputMode))
                    .Where(r => r is not null).Select(r => r!)
                    .OrderByDescending(r => r.LastWrite)
                    .ToList();
            }
        }
        // A wide content search collapses to per-file counts (see Render) unless the model asked for a window itself.
        var explicitWindow = N("head_limit") is not null || offset > 0;
        var result = Render(scanned, outputMode, offset, headLimit, withContext: before > 0 || after > 0 || autoExpanded, save: SaveOversized, collapseWide: !explicitWindow);
        if (skippedBig > 0) result += $"\n[{skippedBig} file(s) over {MaxFileBytes / (1024 * 1024)} MB were not searched]";
        return Task.FromResult(result);
    }

    private string Rel(string full) => Path.GetRelativePath(_workspace, full);

    /// <summary>The rule block around a line of a stylesheet: up to the first line of its selector list, down to
    /// its closing brace. Capped at 40 lines each way; a line outside any rule returns itself.</summary>
    private static (int Start, int End) CssBlock(string[] lines, int at)
    {
        int start = at, end = at;
        // up: to the line that opens the block, then further up while lines end the selector list with a comma
        int i = at; bool opened = lines[at].Contains('{');
        while (!opened && i > 0 && at - i < 40) { i--; if (lines[i].Contains('}')) { i++; break; } if (lines[i].Contains('{')) { opened = true; break; } }
        start = Math.Max(0, Math.Min(i, at));
        while (start > 0 && at - start < 40 && lines[start - 1].TrimEnd().EndsWith(',')) start--;
        // down: to the closing brace of the block opened at or after start
        int depth = 0; bool seenOpen = false;
        for (int j = start; j < Math.Min(lines.Length, start + 80); j++)
        {
            foreach (var ch in lines[j]) { if (ch == '{') { depth++; seenOpen = true; } else if (ch == '}') depth--; }
            if (seenOpen && depth <= 0) { end = j; break; }
            end = j;
        }
        return (start, Math.Max(end, at));
    }

    private sealed record FileScan(string Path, DateTime LastWrite, int MatchCount, List<string> Lines);

    private FileScan? ScanFile(FileInfo file, Regex regex, bool multiline, bool onlyMatching, int before, int after, bool showLineNumbers, string outputMode)
    {
        try
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> sniff = stackalloc byte[8192];
            var sniffed = fs.Read(sniff);
            if (sniff[..sniffed].IndexOf((byte)0) >= 0) return null;          // binary
            fs.Position = 0;
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var raw = reader.ReadToEnd();
            var lines = raw.Split('\n');
            for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');
            var matchedLines = new SortedDictionary<int, List<Match>>();
            if (multiline)
            {
                var lineStarts = BuildLineStarts(raw);
                foreach (Match m in regex.Matches(raw))
                {
                    var startLine = LineOf(lineStarts, m.Index);
                    var endLine = m.Length > 0 ? LineOf(lineStarts, m.Index + m.Length - 1) : startLine;
                    for (var lineIdx = startLine; lineIdx <= endLine; lineIdx++)
                    {
                        if (!matchedLines.TryGetValue(lineIdx, out var list)) matchedLines[lineIdx] = list = [];
                        if (lineIdx == startLine) list.Add(m);
                    }
                }
            }
            else
                for (var i = 0; i < lines.Length; i++)
                {
                    var matches = regex.Matches(lines[i]);
                    if (matches.Count > 0) matchedLines[i] = matches.ToList();
                }
            if (matchedLines.Count == 0) return null;

            // ripgrep on a single file suppresses the path only in content mode; count and files_with_matches always show it.
            var path = (_singleFile && outputMode == "content") ? "" : Rel(file.FullName);
            var output = new List<string>();
            if (outputMode == "content")
            {
                if (onlyMatching)
                {
                    foreach (var (lineIndex, matches) in matchedLines)
                        foreach (var m in matches.Where(m => m.Value.Length > 0))
                            output.Add(Format(path, lineIndex + 1, m.Value, true, showLineNumbers));
                }
                else
                {
                    var include = new SortedSet<int>();
                    // In a stylesheet a match with context means "show me the rule": the range is widened to the
                    // whole rule block (multi-line selector list down to its closing brace), so one grep for a
                    // selector returns every definition in full - the 5-reads-per-selector hunt (2026-09-22).
                    bool css = file.Extension is ".css" or ".scss" or ".less";
                    foreach (var lineIndex in matchedLines.Keys)
                    {
                        int from = Math.Max(0, lineIndex - before), to = Math.Min(lines.Length - 1, lineIndex + after);
                        if (css && (before > 0 || after > 0)) { var (bs, be) = CssBlock(lines, lineIndex); from = Math.Min(from, bs); to = Math.Max(to, be); }
                        for (var i = from; i <= to; i++) include.Add(i);
                    }
                    var previous = -2;
                    foreach (var i in include)
                    {
                        // A separator marks a gap between context groups; with no context every hit is its own
                        // group and the "--" lines were half the output (45 of 101 lines, Westdental 2026-09-24).
                        if (previous >= 0 && i > previous + 1 && (before > 0 || after > 0)) output.Add("--");
                        output.Add(Format(path, i + 1, lines[i], matchedLines.ContainsKey(i), showLineNumbers, !multiline && matchedLines.TryGetValue(i, out var hit) ? hit[0] : null));
                        previous = i;
                    }
                }
            }
            return new FileScan(path, file.LastWriteTimeUtc, matchedLines.Values.Sum(l => l.Count), output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { return null; }
    }

    private static string Format(string path, int lineNo, string text, bool isMatch, bool showLineNumbers, Match? first = null)
    {
        var sep = isMatch ? ':' : '-';
        if (text.Length > 400)
        {
            // A minified file is one line: show the stretch around the first hit (the rule, the call), which is the
            // answer, instead of an omission notice that left the reader with nothing.
            if (first is { Success: true } && first.Index < text.Length)
            {
                var from = Math.Max(0, first.Index - 160);
                var len = Math.Min(text.Length - from, 160 + Math.Max(first.Length, 1) + 240);
                text = (from > 0 ? "..." : "") + text.Substring(from, len) + (from + len < text.Length ? "..." : "") + $" [long line, {text.Length:N0} chars, match at column {first.Index + 1}]";
            }
            else text = "[Omitted long line]";
        }
        if (path.Length == 0) return showLineNumbers ? $"{lineNo}{sep}{text}" : text;
        return showLineNumbers ? $"{path}{sep}{lineNo}{sep}{text}" : $"{path}{sep}{text}";
    }

    /// <summary>Above this many content lines a search is an inventory, not a read: the model gets which files and
    /// how many hits instead of the first 250 lines. "\.php" over an app returned 250 lines (22,017 chars) twice, cut
    /// at the cap both times, and the coder then wrote its own Python walk to learn what count mode says in 2 KB
    /// (Evidenta deseurilor 2026-09-24). Lines still come on request: head_limit, offset or context.</summary>
    private const int CollapseLines = 60;
    private const int CollapseFiles = 40;

    private static string Render(List<FileScan> scans, string outputMode, int offset, int headLimit, bool withContext = false, Func<string, string>? save = null, bool collapseWide = false)
    {
        switch (outputMode)
        {
            case "files_with_matches":
            {
                var paths = scans.Select(s => s.Path).Skip(offset).Take(headLimit).ToList();
                if (paths.Count == 0) return "No matches found";
                var capped = offset + paths.Count < scans.Count;
                var header = $"Found {paths.Count} files" + (capped ? $" limit: {headLimit}" : "");
                return header + "\n" + string.Join("\n", paths);
            }
            case "count":
            {
                var entries = scans.Select(s => $"{s.Path}:{s.MatchCount}").Skip(offset).Take(headLimit).ToList();
                if (entries.Count == 0) return "No matches found";
                var countResult = string.Join("\n", entries);
                if (scans.Count > offset + entries.Count)
                    countResult += $"\n… [{scans.Count - offset - entries.Count} more file(s) not shown; raise head_limit or pass head_limit=0]";
                var totalMatches = scans.Sum(s => s.MatchCount);
                var totalFiles = scans.Count;
                countResult += $"\n\nFound {totalMatches} total {(totalMatches == 1 ? "occurrence" : "occurrences")} across {totalFiles} {(totalFiles == 1 ? "file" : "files")}.";
                return countResult;
            }
            case "content":
            {
                var all = new List<string>();
                foreach (var scan in scans) { if (all.Count > 0 && withContext) all.Add("--"); all.AddRange(scan.Lines); }
                if (all.Count == 0) return "No matches found";
                if (collapseWide && !withContext && scans.Count > 1 && all.Count > CollapseLines)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var s in scans.Take(CollapseFiles)) sb.Append(s.Path).Append(':').Append(s.MatchCount).Append('\n');
                    if (scans.Count > CollapseFiles) sb.Append($"… {scans.Count - CollapseFiles} more file(s)\n");
                    sb.Append($"\n{scans.Sum(s => s.MatchCount)} matches in {scans.Count} files, too many to list. "
                            + "Narrow with path, glob or type, or pass head_limit for the first lines.");
                    return sb.ToString();
                }
                var window = all.Skip(offset).Take(headLimit).ToList();
                if (window.Count == 0) return "No matches found";
                // Unlimited (head_limit:0 → int.MaxValue): save to file when too large.
                if (headLimit == int.MaxValue)
                {
                    var wText = string.Join("\n", window);
                    var wOffsetFooter = offset > 0 ? $"\n\n[Showing results with pagination = offset: {offset}]" : "";
                    if (wText.Length <= MaxInlineChars) return wText + wOffsetFooter;
                    string wSaved;
                    try { wSaved = save is null ? "" : save(wText); }
                    catch (Exception ex) { wSaved = ""; VanityAgent.Infra.Log.Warn("[grep] oversized result not saved: " + ex.Message); }
                    var wPreview = wText[..Math.Min(wText.Length, PreviewChars)];
                    var wCut = wPreview.LastIndexOf('\n');
                    if (wCut > PreviewChars / 2) wPreview = wPreview[..wCut];
                    return "<persisted-output>\n"
                         + $"Output too large ({wText.Length / 1024.0:F1}KB). Full output saved to: {wSaved}\n\n"
                         + $"Preview (first {PreviewChars / 1024}KB):\n{wPreview}\n...\n"
                         + "</persisted-output>";
                }
                // Capped (default 250 or explicit): always inline; footer only when cap actually cut or offset set.
                var capHit = offset + window.Count < all.Count;
                var parts = new List<string>();
                if (capHit) parts.Add($"limit: {headLimit}");
                if (offset > 0) parts.Add($"offset: {offset}");
                var footer = parts.Count > 0 ? $"\n\n[Showing results with pagination = {string.Join(", ", parts)}]" : "";
                return string.Join("\n", window) + footer;
            }
            default: return $"Error: unknown output_mode '{outputMode}'. Use content, files_with_matches, or count.";
        }
    }

    private static List<int> BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    private static int LineOf(List<int> lineStarts, int index)
    {
        int lo = 0, hi = lineStarts.Count - 1;
        while (lo < hi) { var mid = (lo + hi + 1) / 2; if (lineStarts[mid] <= index) lo = mid; else hi = mid - 1; }
        return lo;
    }
}
