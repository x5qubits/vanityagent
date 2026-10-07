using System.Globalization;
using System.Text;

namespace VanityAgent.Agent;

/// <summary>The one system prompt of the agent: who it is, how it works, the environment it runs in, the project's
/// own instruction file (AGENTS.md or VANITY.md in the workspace) and the notes it remembers.</summary>
public static class SystemPrompt
{
    /// <summary>Instruction files read from the workspace: the project directory's own file first, then the
    /// conventional names in the workspace root. Every one that exists is included.</summary>
    public static readonly string[] InstructionFiles = [PromptLibrary.ProjectFolder + "/instructions.md", "AGENTS.md", "VANITY.md", ".vanity-agent.md"];

    public static string Build(string workspace, string? model, IEnumerable<string> toolNames, string? memoryBlock, bool subAgent = false,
        PersonaDefinition? persona = null, IReadOnlyList<SkillDefinition>? activeSkills = null, IReadOnlyList<SkillDefinition>? loadableSkills = null)
    {
        var sb = new StringBuilder();
        if (persona is not null && persona.SystemPrompt.Length > 0)
        {
            // The persona's body is the identity; the mechanics below (rules, tools, environment) stay.
            sb.AppendLine(persona.SystemPrompt.Trim());
            sb.AppendLine();
            sb.AppendLine($"(You are running as the persona \"{persona.Name}\" of vanity-agent, a command-line agent on the operator's machine.)");
        }
        else
            sb.AppendLine(subAgent ? Identity.Replace("the operator's", "a calling agent's") : Identity);
        sb.AppendLine();
        sb.AppendLine("# Working rules");
        sb.AppendLine(Rules);
        sb.AppendLine();
        sb.AppendLine("# Tools");
        sb.AppendLine("You have these tools: " + string.Join(", ", toolNames) + ".");
        sb.AppendLine(ToolGuide);
        sb.AppendLine();
        sb.AppendLine("# Environment");
        sb.AppendLine(EnvInfo(workspace, model));

        foreach (var (file, text) in ProjectInstructions(workspace))
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine($"# Project instructions ({file})");
            sb.AppendLine(text.Trim());
        }

        if (activeSkills is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("# Active skills");
            sb.AppendLine("Playbooks that apply to this session; follow them.");
            foreach (var s in activeSkills)
            {
                sb.AppendLine();
                sb.AppendLine($"## {s.Name}");
                if (s.Description.Length > 0) sb.AppendLine($"> {s.Description}");
                sb.AppendLine(s.Playbook.Trim());
            }
        }
        if (loadableSkills is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("# Skills you may load");
            sb.AppendLine("Playbooks for specific kinds of work. When a task matches one, call skill_view with its name before starting, and follow it.");
            foreach (var s in loadableSkills) sb.AppendLine($"- {s.Name}: {s.Description}");
        }

        if (!string.IsNullOrWhiteSpace(memoryBlock))
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine(memoryBlock.Trim());
        }
        return sb.ToString().Trim();
    }

    private const string Identity =
        "You are vanity-agent, a general-purpose command-line agent. You help with software engineering, system administration, " +
        "research, writing and data work on the operator's machine, using the tools you are given. You act: when a request needs " +
        "files read, commands run or pages fetched, you do it rather than describing how.";

    private const string Rules =
        "- Understand before changing: read the relevant files or run the relevant commands first; never guess at file contents or command output.\n" +
        "- Make the change the operator asked for, completely. Do not narrow the task, and do not widen it with unrequested refactors, " +
        "documentation files or emojis.\n" +
        "- Prefer editing existing files to creating new ones. Keep the project's conventions (style, naming, structure).\n" +
        "- Verify your work when you can: run the build, the tests or the command, and report the actual result. If something fails, say so " +
        "with the output; never claim success you did not observe.\n" +
        "- Be careful with destructive actions (deleting files, force-pushing, dropping data, rewriting history). Do them only when the request " +
        "clearly asks for them; otherwise say what you would do and stop.\n" +
        "- Keep replies short and concrete: what you found, what you did, what is next. Use plain text; use a fenced code block for commands, " +
        "code or error text. Reference files by path.\n" +
        "- When the request is a question or an analysis, answer it from what you observed; do not change files unless asked.\n" +
        "- Work in the operator's language when they write in one other than English.";

    private const string ToolGuide =
        "- Several independent tool calls go in ONE turn: the harness runs them in parallel. Read several files in one read_file call " +
        "(the files list), apply several replacements in one edit_file call (the edits list), write several files in one write_files call.\n" +
        "- Use grep and glob to search, read_file to read; use bash for commands, builds, tests and git. Do not use bash to cat, grep or find " +
        "when a dedicated tool exists.\n" +
        "- bash runs a persistent POSIX shell (bash); on Windows it is Git Bash. Shell state persists between calls. Quote paths with spaces.\n" +
        "- Tool output over the size limit is saved to a file and you get a preview with the path: read it with read_file or search it with grep.\n" +
        "- task_scratchpad keeps your working plan for a long task; memory stores facts worth keeping for later sessions in this project " +
        "(paths, commands that work, decisions). Save a note when you learn something the next session would otherwise rediscover.\n" +
        "- agent delegates a self-contained sub-task to a fresh agent with the same tools and returns its report; use it for broad " +
        "searches or independent parallel work, not for the main task.";

    public static List<(string File, string Text)> ProjectInstructions(string workspace)
    {
        var found = new List<(string, string)>();
        foreach (var name in InstructionFiles)
        {
            var path = Path.Combine(workspace, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            try
            {
                var text = File.ReadAllText(path);
                if (text.Length > 40_000) text = text[..40_000] + "\n[instructions cut at 40,000 characters]";
                if (text.Trim().Length > 0) found.Add((name, text));
            }
            catch { }
        }
        return found;
    }

    public static string EnvInfo(string workspace, string? model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<env>");
        sb.AppendLine("Working directory: " + workspace.Replace('\\', '/'));
        sb.AppendLine("Is directory a git repo: " + (GitRoot(workspace) is not null ? "Yes" : "No"));
        sb.AppendLine("Platform: " + (OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux"));
        sb.AppendLine("OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        sb.AppendLine("Shell: bash (POSIX syntax; not cmd or PowerShell)");
        sb.AppendLine("Today's date: " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(model)) sb.AppendLine("Model: " + model);
        if (Infra.VanityPathHelper.Sandbox) sb.AppendLine("Sandbox: on (file tools are confined to the working directory)");
        sb.Append("</env>");
        return sb.ToString();
    }

    private static string? GitRoot(string dir)
    {
        try
        {
            var d = new DirectoryInfo(dir);
            while (d is not null)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
                d = d.Parent;
            }
        }
        catch { }
        return null;
    }
}
