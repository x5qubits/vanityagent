using System.Text.Json;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

/// <summary>File search by glob, ported from the Claude Code tool port (VaniyTools GlobTool): case-insensitive,
/// files only, dot entries and the harness's own folder skipped, sorted by modification time (newest first), the
/// first 100 returned with a note when more exist. Paths come back relative to the workspace.</summary>
public sealed class GlobTool : ITool
{
    private const int Limit = 100;
    private readonly string _workspace;

    public GlobTool(string workspace) => _workspace = Path.GetFullPath(workspace);

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "glob",
        Description = "Fast file pattern matching for any codebase size: \"**/*.js\", \"src/**/*.ts\", \"*.php\". Returns matching file paths, newest first. Use it to find files by name; use grep for what they contain.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["pattern"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The glob pattern to match files against" },
                ["path"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The directory to search in, relative to the workspace. Defaults to the workspace root." },
            },
            ["required"] = new[] { "pattern" },
        },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException ex) { return Task.FromResult("Error: arguments are not valid JSON: " + ex.Message); }
        var pattern = (root.TryGetProperty("pattern", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() ?? "" : "").Replace('\\', '/').Trim();
        if (pattern.Length == 0) return Task.FromResult("Error: pattern is required.");
        var rawPath = root.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString()?.Trim() ?? "" : "";

        string cwd;
        try { cwd = rawPath.Length == 0 || rawPath == "." ? _workspace : VanityPathHelper.NormalizeAndResolveStrict(rawPath, _workspace); }
        catch (Exception ex) { return Task.FromResult("Error: " + ex.Message); }
        if (VanityPathHelper.IsDeniedForAgent(cwd, _workspace, out var scopeMsg)) return Task.FromResult(scopeMsg);
        if (!Directory.Exists(cwd)) return Task.FromResult("No files found");

        // "src/**/*.ts" style patterns carry their own base folder; strip a leading "./".
        if (pattern.StartsWith("./")) pattern = pattern[2..];
        var regex = GlobMatcher.ToRegex(pattern, caseInsensitive: true);
        var matches = new List<FileInfo>();
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".vanity", ".git", ".vs", ".idea", "node_modules" };
        foreach (var file in FileWalker.EnumerateFiles(cwd))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(cwd, file.FullName).Replace('\\', '/');
            if (relative.Split('/').Any(seg => skipDirs.Contains(seg))) continue;
            if (!regex.IsMatch(relative)) continue;
            if (VanityPathHelper.IsDeniedForAgent(file.FullName, _workspace, out _)) continue;
            matches.Add(file);
        }

        var sorted = matches.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var files = sorted.Take(Limit).Select(f => Path.GetRelativePath(_workspace, f.FullName).Replace('\\', '/')).ToList();
        if (files.Count == 0) return Task.FromResult("No files found");
        var result = string.Join("\n", files);
        if (sorted.Count > Limit) result += $"\n… [{sorted.Count - Limit} more not shown - use a more specific path or pattern]";
        return Task.FromResult(result);
    }
}
