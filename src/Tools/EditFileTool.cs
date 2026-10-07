using System.Text;
using System.Text.Json;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

/// <summary>Performs precise substring replacement in a file.</summary>
public sealed class EditFileTool : ITool
{
    private readonly string _baseDir;

    public EditFileTool(string? baseDir = null)
    {
        _baseDir = baseDir ?? Directory.GetCurrentDirectory();
    }

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "edit_file",
        Description = "Performs exact string replacements in files.\n\n" +
                      "Usage:\n" +
                      "- Paths may be absolute or relative to the working directory.\n" +
                      "- When editing text from read_file tool output, ensure you preserve the exact indentation (tabs/spaces) as it appears AFTER the line number prefix. The line number prefix format is: line number + tab. Everything after that is the actual file content to match. Never include any part of the line number prefix in the old_string or new_string.\n" +
                      "- ALWAYS prefer editing existing files in the codebase. NEVER write new files unless explicitly required.\n" +
                      "- Only use emojis if the user explicitly requests it. Avoid adding emojis to files unless asked.\n" +
                      "- The edit will FAIL if `old_string` is not unique in the file. Either provide a larger string with more surrounding context to make it unique or use `replace_all` to change every instance of `old_string`.\n" +
                      "- Use `replace_all` for replacing and renaming strings across the file. This parameter is useful if you want to rename a variable for instance.\n" +
                      "- Several changes, in one file or across files, go in ONE call through `edits`: they are applied in the order given and each one reports its own result.\n" +
                      "- Keep `old_string` to the lines you change plus the little around them that makes it unique. A long `old_string` misses as soon as any line inside it has changed since you read it.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {

                ["file_path"]     = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The absolute path to the file to modify" },
                ["old_string"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The text to replace" },
                ["new_string"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The text to replace it with (must be different from old_string)" },
                ["replace_all"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Replace all occurrences of old_string (default false)" },
                // The array was always accepted by ExecuteAsync and the coder's rules ask for "one edit_file per change
                // with every file it touches", but it was not declared here, so no model could send it: 377 edit calls
                // in one project build, none with a list, 134 of them straight after another edit (2026-10-04).
                ["edits"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "Several replacements in ONE call instead of one call each, in one file or across files. Each entry: {\"file_path\": \"...\", \"old_string\": \"...\", \"new_string\": \"...\"}, replace_all optional. Applied in the order given; the result has one line per entry, and an entry that fails does not stop the others. Omit file_path, old_string and new_string at the top level when you pass edits.",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["file_path"]   = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The absolute path to the file to modify" },
                            ["old_string"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The text to replace" },
                            ["new_string"]  = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The text to replace it with (must be different from old_string)" },
                            ["replace_all"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Replace all occurrences of old_string (default false)" },
                        },
                        ["required"] = new[] { "file_path", "old_string", "new_string" },
                    },
                },
            },
            // No top-level "required": a call carries either one replacement or edits, and the tool says so when both are missing.
        },
    };

    private static bool Flag(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc   = JsonDocument.Parse(argsJson);
        var root        = doc.RootElement;
        if (root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
        {
            var lines = new List<string>();
            foreach (var e in edits.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var r = await EditOneAsync(e.TryGetProperty("file_path", out var p) ? p.GetString() ?? "" : "",
                                           e.TryGetProperty("old_string", out var o) ? o.GetString() ?? "" : "",
                                           e.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : "",
                                           Flag(e, "replace_all") || Flag(root, "replace_all"), ct).ConfigureAwait(false);
                lines.Add("- " + r);
            }
            return lines.Count == 0 ? "Error: edits is empty." : string.Join("\n", lines);
        }
        return await EditOneAsync(root.TryGetProperty("file_path", out var pp) ? pp.GetString() ?? "" : "",
                                  root.TryGetProperty("old_string", out var ot) ? ot.GetString() ?? "" : "",
                                  root.TryGetProperty("new_string", out var nt) ? nt.GetString() ?? "" : "",
                                  Flag(root, "replace_all"), ct).ConfigureAwait(false);
    }

    private async Task<string> EditOneAsync(string relPath, string oldText, string newText, bool replaceAll, CancellationToken ct)
    {
        if (relPath.Length == 0) return "Error: file_path is required.";
        if (oldText == newText) return "Error: old_string and new_string are identical; nothing to change.";

        string fullPath;
        try { fullPath = VanityPathHelper.NormalizeAndResolveStrict(relPath, _baseDir); }
        catch (ArgumentException ex) { return "Error: " + ex.Message; }

        if (VanityPathHelper.IsDeniedForAgent(fullPath, _baseDir, out var scopeMsg))
            return scopeMsg;

        if (!File.Exists(fullPath))
            return $"Error: File '{relPath}' not found.";

        try
        {
            var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);

            // Normalize line endings and strip trailing whitespace per line so blank separator
            // lines in old_string (which models often write as \t\n) match bare \n in files.
            static string Norm(string s)
            {
                var lines = s.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
                return string.Join("\n", lines);
            }
            var normContent = Norm(content);
            var normOldText = Norm(oldText);
            var normNewText = Norm(newText);

            int idx = normContent.IndexOf(normOldText, StringComparison.Ordinal);
            if (idx < 0)
            {
                Log.Warn($"[edit_file] old_string not found in {fullPath}");
                return $"String to replace not found in '{relPath}'. {NearestLines(content, oldText)}\nString: {oldText}";
            }

            int occurrences = 0;
            for (int at = idx; at >= 0; at = normContent.IndexOf(normOldText, at + Math.Max(1, normOldText.Length), StringComparison.Ordinal)) occurrences++;
            if (occurrences > 1 && !replaceAll)
            {
                return $"Found {occurrences} matches of the string to replace, but replace_all is false. To replace all occurrences, set replace_all to true. To replace only one occurrence, please provide more context to uniquely identify the instance.\nString: {oldText}";
            }

            var updated = replaceAll
                ? normContent.Replace(normOldText, normNewText, StringComparison.Ordinal)
                : normContent.Remove(idx, normOldText.Length).Insert(idx, normNewText);
            if (updated == normContent) return $"String to replace not found in file.\nString: {oldText}";

            // Preserve CRLF if original file used it
            if (content.Contains("\r\n"))
                updated = updated.Replace("\n", "\r\n");

            await File.WriteAllTextAsync(fullPath, updated, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
            Log.Info($"[edit_file] successfully edited {fullPath}");
            return $"The file {relPath} has been updated successfully.";
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            var msg = ex.Message.Replace(fullPath, relPath, StringComparison.OrdinalIgnoreCase)
                                 .Replace(_baseDir, "", StringComparison.OrdinalIgnoreCase);
            return $"Error editing file '{relPath}': {msg}";
        }
    }

    /// <summary>Where old_string almost matches: the first file position whose lines equal old_string's lines ignoring
    /// indentation, and the first line that differs, with the indentation of both written out (tabs and spaces counted).
    /// "Not found" with no direction cost a coder eight calls blaming line endings for a tab count.</summary>
    internal static string NearestLines(string fileContent, string oldText)
    {
        var file = fileContent.Replace("\r\n", "\n").Split('\n');
        var old = oldText.Replace("\r\n", "\n").Split('\n').SkipWhile(l => l.Trim().Length == 0).ToArray();
        if (old.Length == 0) return "old_string is empty.";
        int bestAt = -1, bestRun = 0;
        for (int i = 0; i < file.Length; i++)
        {
            int run = 0;
            while (run < old.Length && i + run < file.Length && file[i + run].Trim() == old[run].Trim()) run++;
            if (run > bestRun) { bestRun = run; bestAt = i; }
        }
        static string Indent(string l)
        {
            int tabs = l.TakeWhile(c => c == '\t').Count();
            int spaces = l.Skip(tabs).TakeWhile(c => c == ' ').Count();
            return tabs > 0 && spaces > 0 ? $"{tabs} tab(s) + {spaces} space(s)" : tabs > 0 ? $"{tabs} tab(s)" : spaces > 0 ? $"{spaces} space(s)" : "no indentation";
        }
        static string Clip(string l) => l.Trim().Length > 140 ? l.Trim()[..140] + "..." : l.Trim();
        if (bestRun == 0)
            return "No line of old_string exists in the file even ignoring indentation: the text is not there as written (a different spelling, or an earlier edit changed it). Search for a short unique part of it with grep, then copy the lines from read_file.";
        if (bestRun == old.Length)
        {
            for (int k = 0; k < old.Length; k++)
                if (file[bestAt + k] != old[k])
                    return $"The text exists at line {bestAt + 1}, only the leading whitespace differs. First difference, old_string line {k + 1}: the file has {Indent(file[bestAt + k])}, you sent {Indent(old[k])}. Copy the lines from read_file without changing their indentation.";
            return $"The text exists at line {bestAt + 1} and differs only in trailing whitespace or line endings.";
        }
        if (bestAt + bestRun >= file.Length)
            return $"The nearest match starts at line {bestAt + 1}, but the file ends after {bestRun} matching line(s) while old_string has {old.Length}. Shorten old_string to the lines that exist.";
        var f = file[bestAt + bestRun]; var o = old[bestRun];
        return $"The nearest match starts at line {bestAt + 1}: its first {bestRun} line(s) equal yours (indentation aside), then old_string line {bestRun + 1} differs. The file has: {Clip(f)} - you sent: {Clip(o)}. Re-read lines {bestAt + 1}-{bestAt + old.Length} with read_file and copy them as they are.";
    }
}
