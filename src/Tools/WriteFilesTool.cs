using System.Text;
using System.Text.Json;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

/// <summary>write_files: several complete files in ONE call. The batch form is what lets a builder finish a
/// whole playbook turn (header + footer + css) without spending one model round-trip per file.</summary>
public sealed class WriteFilesTool : ITool
{
    private readonly string _baseDir;

    public WriteFilesTool(string? baseDir = null) { _baseDir = baseDir ?? Directory.GetCurrentDirectory(); }

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "write_files",
        Description = "Writes files to the local filesystem.\n\n" +
                      "Usage:\n" +
                      "- This tool will overwrite the existing file if there is one at the provided path.\n" +
                      "- Paths may be absolute or relative to the working directory.\n" +
                      "- Prefer edit_file for modifying existing files — it only sends the diff. Only use this tool to create new files or for complete rewrites.\n" +
                      "- NEVER create documentation files (*.md) or README files unless explicitly requested by the User.\n" +
                      "- Only use emojis if the user explicitly requests it. Avoid writing emojis to files unless asked.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["files"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "Files to write.",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["path"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Path relative to the workspace." },
                            ["content"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full file content." },
                        },
                        ["required"] = new[] { "path", "content" },
                    },
                },
            },
            ["required"] = new[] { "files" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argsJson);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return "Error: 'files' array is required.";

        var sb = new StringBuilder();
        int ok = 0, failed = 0;
        foreach (var f in files.EnumerateArray())
        {
            var relPath = f.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            var content = f.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(relPath)) { failed++; sb.AppendLine("- (missing path): skipped"); continue; }
            try
            {
                var full = VanityPathHelper.NormalizeAndResolveStrict(relPath, _baseDir);
                if (VanityPathHelper.IsDeniedForAgent(full, _baseDir, out var scopeMsg))
                { failed++; sb.AppendLine($"- {relPath}: {scopeMsg}"); continue; }
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
                ok++;
                sb.AppendLine($"File created successfully at: {relPath} ({content.Split('\n').Length} lines)");
                Log.Info($"[write_files] wrote {content.Length} chars to {full}");
            }
            catch (Exception ex)
            {
                failed++;
                sb.AppendLine($"- {relPath}: Error: {ex.Message.Replace(_baseDir, "", StringComparison.OrdinalIgnoreCase)}");
            }
        }
        return $"Wrote {ok} file(s){(failed > 0 ? $", {failed} failed" : "")}:\n{sb.ToString().TrimEnd()}";
    }
}
