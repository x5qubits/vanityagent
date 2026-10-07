using VanityAgent.Llm;
using VanityAgent.Tools;

namespace VanityAgent.Tools;

/// <summary>Lists every tool registered in the ToolRegistry — names and descriptions.</summary>
public sealed class ListToolsTool : ITool
{
    private readonly IToolRegistry _registry;
    public ListToolsTool(IToolRegistry registry) => _registry = registry;

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "list_tools",
        Description = "List all registered tools.",
        Parameters  = new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        var lines = _registry.All
            .OrderBy(t => t.Name)
            .Select(t => $"- {t.Name}: {t.Description}");
        return Task.FromResult(string.Join("\n", lines));
    }
}
