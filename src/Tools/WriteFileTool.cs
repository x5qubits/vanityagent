using System.Text;
using System.Text.Json;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

/// <summary>Writes or overwrites content to a file, creating parent directories if needed.</summary>
public sealed class WriteFileTool : ITool
{
    private readonly string _baseDir;

    public WriteFileTool(string? baseDir = null)
    {
        _baseDir = baseDir ?? Directory.GetCurrentDirectory();
    }

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "write_file",
        Description = "Write content to a file. Overwrites existing file or creates a new one. Automatically creates any parent directories.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["path"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Path to the file." },
                ["content"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Full text content to write." },
            },
            ["required"] = new[] { "path", "content" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc   = JsonDocument.Parse(argsJson);
        var root        = doc.RootElement;
        var relPath     = root.GetProperty("path").GetString() ?? "";
        var content     = root.GetProperty("content").GetString() ?? "";

        string fullPath;
        try { fullPath = VanityPathHelper.NormalizeAndResolveStrict(relPath, _baseDir); }
        catch (ArgumentException ex) { return "Error: " + ex.Message; }

        if (VanityPathHelper.IsDeniedForAgent(fullPath, _baseDir, out var scopeMsg))
            return scopeMsg;

        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(fullPath, content, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
            var lineCount = content.Split('\n').Length;
            Log.Info($"[write_file] wrote {content.Length} chars ({lineCount} lines) to {fullPath}");
            return $"Wrote {lineCount} lines to '{relPath}'.";
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            var msg = ex.Message.Replace(fullPath, relPath, StringComparison.OrdinalIgnoreCase)
                                 .Replace(_baseDir, "", StringComparison.OrdinalIgnoreCase);
            return $"Error writing file '{relPath}': {msg}";
        }
    }
}
