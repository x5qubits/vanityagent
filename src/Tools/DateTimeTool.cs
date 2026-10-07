using System.Text.Json;
using VanityAgent.Llm;
using VanityAgent.Tools;

namespace VanityAgent.Tools;

/// <summary>Returns the current date/time in various formats.</summary>
public sealed class DateTimeTool : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name        = "date_time",
        Description = "Returns the current date and time. Formats: 'local' (default), 'utc', 'unix', 'date', 'time', 'iso'.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["format"] = new Dictionary<string, object>
                {
                    ["type"]        = "string",
                    ["description"] = "One of: 'local' (default), 'utc', 'unix', 'date', 'time', 'iso'.",
                },
            },
            ["required"] = Array.Empty<string>(),
        },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        string format = "local";
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("format", out var f))
                format = f.GetString()?.ToLowerInvariant() ?? "local";
        }
        catch { }

        var now    = DateTime.Now;
        var utcNow = DateTime.UtcNow;

        var result = format switch
        {
            "utc"   => utcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
            "unix"  => new DateTimeOffset(utcNow).ToUnixTimeSeconds().ToString(),
            "date"  => now.ToString("yyyy-MM-dd"),
            "time"  => now.ToString("HH:mm:ss"),
            "iso"   => DateTimeOffset.Now.ToString("o"),
            _       => now.ToString("yyyy-MM-dd HH:mm:ss") + " " + TimeZoneInfo.Local.DisplayName,
        };

        return Task.FromResult(result);
    }
}
