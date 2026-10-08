using System.Text.Json;
using System.Text.RegularExpressions;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

/// <summary>Persistent bash tool: one long-lived bash process per ChatSession.
/// Port of the VanityCoder BashTool, adapted to VanityAgent's ITool interface.</summary>
public sealed class BashTool : ITool
{

    // Output cap. At 12,000 a grep over a site cut the decisive line (character 16,000 of 20,000) out of the
    // middle and the model searched 30 more times for what it had already found (2026-09-21).
    private static readonly int MaxOutputChars = 30_000;

    private readonly ShellHandle _handle = new();
    private readonly string      _cwd;
    private readonly string      _bashPath;

    public BashTool(string? workingDirectory = null)
    {
        _cwd      = workingDirectory ?? Directory.GetCurrentDirectory();
        _bashPath = FindBash();
    }

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "bash",
        Description = "Run bash in a persistent shell. State persists between calls.",
        Parameters = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["command"]    = new Dictionary<string, object> { ["type"] = "string", ["description"] = "The bash command to run." },
                ["timeout_ms"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Time limit for this command in ms: default 120000, up to " + PersistentShell.MaxTimeoutMs + ". A command stopped at the limit returns what it printed until then." },
            },
            ["required"] = new[] { "command" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        using var doc    = JsonDocument.Parse(argsJson);
        var root         = doc.RootElement;
        var command      = (root.TryGetProperty("command", out var cmd) ? cmd : root.TryGetProperty("cmd", out var cmd2) ? cmd2 : default).GetString() ?? "";
        var timeoutMs    = root.TryGetProperty("timeout_ms", out var t) ? Math.Clamp(t.GetInt32(), 1000, PersistentShell.MaxTimeoutMs) : 120_000;


        if (VanityPathHelper.CommandTouchesDeniedPath(command, out var scopeMsg))
            return scopeMsg;

        Log.Debug($"[terminal] $ {Truncate(command, 120)}");

        var shell = _handle.GetOrCreate(_bashPath, _cwd);
        ShellExecResult result;
        try
        {
            result = await shell.ExecAsync(command, timeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            _handle.Close();
            return $"Shell died and was reset: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Shell error: {ex.Message}";
        }

        var stdout = (result.Stdout ?? "").Trim();
        var stderr = (result.Stderr ?? "").Trim();
        if (!shell.IsAlive)
        {
            // `exit`, `exec` or a crash ended the persistent shell itself. This used to read "[aborted]", as if the
            // operator had stopped it; the next call silently got a fresh shell with none of the exported state.
            _handle.Close();
            stderr += (stderr.Length > 0 ? "\n" : "") + "The shell exited on this command (e.g. `exit`); a fresh shell with no exported state starts on the next call.";
        }
        else if (result.Interrupted)
            stderr += (stderr.Length > 0 ? "\n" : "") + "[aborted]";
        else if (result.TimedOut) { }   // stderr already says it ran into the limit and how to get a longer one
        else if (result.Code != 0)
            stderr += (stderr.Length > 0 ? "\n" : "") + $"Exit code {result.Code}";

        bool hasBoth = stdout.Length > 0 && stderr.Length > 0;
        var combined = (stdout + (hasBoth ? "\n" : "") + stderr).Trim();
        return combined.Length <= MaxOutputChars ? combined : PersistOversized(combined);
    }

    // Oversized output: nothing is cut. The full text is saved to a file the read tool can
    // open in slices, and the model gets the first 2KB as a preview plus the path. Head+tail truncation dropped the
    // middle for good, and a grep hit or the one failing build line was often exactly there.
    private const int PreviewChars = 2048;

    private string PersistOversized(string output)
    {
        var dir  = Path.Combine(AgentConfig.ScratchRoot, "tool-results");
        var path = Path.Combine(dir, RandomId() + ".txt");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, output);
        }
        catch (Exception ex)
        {
            return ProcessRunner.Truncate(output, MaxOutputChars) + $"\n[could not persist full output: {ex.Message}]";
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(output);
        var size  = bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0}MB" : $"{bytes / 1024.0:0.0}KB";
        return "<persisted-output>\n"
             + $"Output too large ({size}). Full output saved to: {path}\n"
             + "Search it with the grep tool (path above) or read a part with read_file offset/limit; or run the command again with a narrower filter or `| head -n`.\n"
             + "\n"
             + "Preview (first 2KB):\n"
             + output[..PreviewChars] + "\n"
             + "...\n"
             + "</persisted-output>";
    }

    private static string RandomId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(9, alphabet, (span, a) => { for (var i = 0; i < span.Length; i++) span[i] = a[Random.Shared.Next(a.Length)]; });
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string FindBash()
    {
        // 1. explicit override, 2. PATH scan (pure .NET), 3. Git for Windows under the real Program Files folders.
        var overridePath = Environment.GetEnvironmentVariable("VANITY_BASH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        var exe = OperatingSystem.IsWindows() ? "bash.exe" : "bash";

        if (OperatingSystem.IsWindows())
        {
            // Git for Windows first: System32\bash.exe on PATH is the WSL launcher, not a usable shell.
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var rel in new[] { @"Git\bin\bash.exe", @"Git\usr\bin\bash.exe" })
                {
                    var c = Path.Combine(root, rel);
                    if (File.Exists(c)) return c;
                }
            }
        }
        else
        {
            foreach (var c in new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash" })
                if (File.Exists(c)) return c;
        }

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var d = dir.Trim().Trim('"');
                if (!string.IsNullOrEmpty(windir) && d.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) continue; // WSL stub
                var c = Path.Combine(d, exe);
                if (File.Exists(c)) return c;
            }
            catch { }
        }

        // Fall back to 'bash' on PATH
        try
        {
            var result = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "where",
                Arguments              = "bash",
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });
            if (result != null)
            {
                var line = result.StandardOutput.ReadLine()?.Trim() ?? "";
                result.WaitForExit();
                if (File.Exists(line)) return line;
            }
        }
        catch { }

        return "bash"; // last resort — let OS resolve it
    }
}
