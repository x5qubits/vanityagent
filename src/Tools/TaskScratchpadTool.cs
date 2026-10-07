using System.Text.Json;
using VanityAgent.Llm;
using VanityAgent.Tools;

namespace VanityAgent.Tools;

/// <summary>
/// In-task working notes. Resets at process startup. Useful for complex multi-step tasks.
/// Actions: read, write, append, clear.
/// </summary>
public sealed class TaskScratchpadTool : ITool
{
    private string _content = "";

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "task_scratchpad",
        Description = "In-task working notes (in-process only, resets on restart). Actions: 'read', 'write' (replace all), 'append', 'clear'.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object>
                {
                    ["type"]        = "string",
                    ["description"] = "One of: 'read', 'write', 'append', 'clear'.",
                },
                ["text"] = new Dictionary<string, object>
                {
                    ["type"]        = "string",
                    ["description"] = "Content to write or append (required for 'write' and 'append').",
                },
            },
            ["required"] = new[] { "action" },
        },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root   = doc.RootElement;
        var action = root.TryGetProperty("action", out var ap) ? ap.GetString()?.ToLowerInvariant() ?? "read" : "read";
        var text   = root.TryGetProperty("text",   out var tp) ? tp.GetString() ?? "" : "";
        if (action is "write" or "append" && text.Length == 0)
            return Task.FromResult($"Error: 'text' is required for '{action}'. Use 'clear' to empty the scratchpad.");

        switch (action)
        {
            case "write":
                _content = text;
                return Task.FromResult($"Scratchpad updated ({_content.Length} chars).");

            case "append":
                if (_content.Length > 0 && !_content.EndsWith('\n'))
                    _content += "\n";
                _content += text;
                return Task.FromResult($"Appended. Scratchpad is now {_content.Length} chars.");

            case "clear":
                _content = "";
                return Task.FromResult("Scratchpad cleared.");

            case "read":
            default:
                return Task.FromResult(string.IsNullOrEmpty(_content)
                    ? "[Scratchpad is empty]"
                    : _content);
        }
    }
}
