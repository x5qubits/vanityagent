using System.Text;
using System.Text.Json;
using VanityAgent.Infra;
using VanityAgent.Llm;

namespace VanityAgent.Tools;

/// <summary>Delegate self-contained sub-tasks to fresh agents that run in parallel and report back. The runner is
/// supplied by the host (it builds a new loop with the same tools, minus this one).</summary>
public sealed class AgentTool : ITool
{
    public delegate Task<string> Runner(string task, string? persona, CancellationToken ct);

    private readonly Runner _run;
    private readonly int _maxParallel;

    public AgentTool(Runner run, int maxParallel = 4) { _run = run; _maxParallel = Math.Max(1, maxParallel); }

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "agent",
        Description = "Delegate a self-contained task to a fresh sub-agent with the same tools (except agent) and get its report back, optionally as a named persona. " +
                      "All jobs in one call run IN PARALLEL and this call returns when every job is done: pass several jobs only when they " +
                      "are independent (no shared files, no ordering). Each job sees only its own task text, so put everything it needs in it " +
                      "(paths, what to look for, what to return). Good for broad searches and independent parallel work; the main task stays with you.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["jobs"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "Jobs to run in parallel.",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["task"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The complete task for the sub-agent, including what to return." },
                            ["label"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "A short name for the job (shown in the report)." },
                            ["persona"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Optional: run the job as this persona (one of the defined personas, e.g. reviewer)." },
                        },
                        ["required"] = new[] { "task" },
                    },
                },
                ["task"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "A single task (shorthand for one job)." },
            },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = doc.RootElement;
        var jobs = new List<(string Label, string Task, string? Persona)>();
        if (root.TryGetProperty("jobs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var j in arr.EnumerateArray())
            {
                var task = j.ValueKind == JsonValueKind.String ? j.GetString() ?? "" : j.TryGetProperty("task", out var t) ? t.GetString() ?? "" : "";
                var label = j.ValueKind == JsonValueKind.Object && j.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                var persona = j.ValueKind == JsonValueKind.Object && j.TryGetProperty("persona", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() : null;
                if (task.Trim().Length > 0) jobs.Add((label.Length > 0 ? label : (persona ?? $"job {jobs.Count + 1}"), task, persona));
            }
        if (root.TryGetProperty("task", out var single) && single.ValueKind == JsonValueKind.String && single.GetString()!.Trim().Length > 0)
        {
            var persona = root.TryGetProperty("persona", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() : null;
            jobs.Add((persona ?? "job " + (jobs.Count + 1), single.GetString()!, persona));
        }
        if (jobs.Count == 0) return "Error: give a task, or a jobs array with at least one task.";

        if (AgentToolContext.Depth >= 1)
            return "Error: a sub-agent cannot spawn further sub-agents. Do the work with the other tools.";

        Log.Info($"[agent] spawning {jobs.Count} sub-agent(s)");
        using var gate = new SemaphoreSlim(_maxParallel, _maxParallel);
        var runs = jobs.Select(async j =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var text = await _run(Wrap(j.Task), j.Persona, ct).ConfigureAwait(false);
                return (j.Label, Ok: true, Text: text, sw.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (j.Label, Ok: false, Text: ex.Message, sw.Elapsed); }
            finally { gate.Release(); }
        }).ToList();
        var results = await Task.WhenAll(runs).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append($"=== Sub-agent results ({results.Length}) ===\n");
        foreach (var r in results)
        {
            var secs = r.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            sb.Append('\n').Append($"--- {r.Label} · {(r.Ok ? "done" : "FAILED")} in {secs}s ---\n");
            sb.Append(r.Text.Trim()).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Wrap(string task) =>
        "DELEGATED_TASK\n" +
        "Do this work now, in this run, with your tools, and return the result in your final reply (findings, file paths, " +
        "what you changed, what you verified). Be complete and concrete; the caller sees only your reply.\n\n" +
        task.Trim();
}
