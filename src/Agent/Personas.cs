using System.Text;
using VanityAgent.Infra;

namespace VanityAgent.Agent;

/// <summary>A persona: a markdown file with YAML frontmatter whose body replaces the agent's identity. Frontmatter:
/// <c>name</c>, <c>description</c>, <c>tools</c> (the only tools it may use), <c>skills</c> (loaded up front),
/// <c>max_turns</c>.</summary>
public sealed class PersonaDefinition
{
    public string   Name         { get; init; } = "";
    public string   Description  { get; init; } = "";
    public string[] Tools        { get; init; } = [];
    public string[] Skills       { get; init; } = [];
    public int      MaxTurns     { get; init; }
    public string   SystemPrompt { get; init; } = "";
    public string   Source       { get; init; } = "";
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

/// <summary>A skill: a playbook the model can load on demand (<c>skill_view</c>), or that a persona pins. Frontmatter:
/// <c>name</c>, <c>description</c> (what the catalog shows), <c>always: true</c> to inline it on every turn.</summary>
public sealed class SkillDefinition
{
    public string Name        { get; init; } = "";
    public string Description { get; init; } = "";
    public string Playbook    { get; init; } = "";
    public bool   Always      { get; init; }
    public string Source      { get; init; } = "";
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

/// <summary>Minimal YAML frontmatter: <c>key: value</c> lines between two <c>---</c> fences; arrays as
/// <c>[a, b]</c>, <c>a, b</c> or a block of <c>- item</c> lines.</summary>
public static class FrontmatterParser
{
    public static (Dictionary<string, string> Meta, string Body) Parse(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = text.Replace("\r\n", "\n").TrimStart('﻿');
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (meta, text.Trim());
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (meta, text.Trim());
        var header = text[4..end];
        var body = text[(end + 4)..];
        if (body.StartsWith('\n')) body = body[1..];

        string? key = null; var block = new List<string>();
        void Flush() { if (key is not null && block.Count > 0) meta[key] = "[" + string.Join(", ", block) + "]"; block.Clear(); }
        foreach (var raw in header.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().StartsWith("- ") && key is not null && (meta.TryGetValue(key, out var cur) ? cur.Length == 0 : true))
            { block.Add(line.Trim()[2..].Trim().Trim('"', '\'')); continue; }
            Flush();
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.TrimStart().StartsWith('#')) { key = null; continue; }
            key = line[..colon].Trim();
            meta[key] = line[(colon + 1)..].Trim().Trim('"', '\'');
        }
        Flush();
        return (meta, body.Trim());
    }

    public static string[] ParseArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var s = raw.Trim();
        if (s.StartsWith('[') && s.EndsWith(']')) s = s[1..^1];
        return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim('"', '\'')).Where(x => x.Length > 0).ToArray();
    }
}

/// <summary>The personas and skills available to a workspace: the global ones under the agent home and the project's
/// own under <c>&lt;workspace&gt;/.vanity-agent</c>, project files overriding global ones by name.</summary>
public sealed class PromptLibrary
{
    public const string ProjectFolder = ".vanity-agent";

    public IReadOnlyList<PersonaDefinition> Personas { get; }
    public IReadOnlyList<SkillDefinition>   Skills   { get; }
    public string Workspace { get; }

    private PromptLibrary(string workspace, List<PersonaDefinition> personas, List<SkillDefinition> skills)
    { Workspace = workspace; Personas = personas; Skills = skills; }

    public static string ProjectDir(string workspace) => Path.Combine(workspace, ProjectFolder);
    public static bool HasProjectDir(string workspace) => Directory.Exists(ProjectDir(workspace));

    public static PromptLibrary Load(string workspace)
    {
        var personas = new Dictionary<string, PersonaDefinition>(StringComparer.OrdinalIgnoreCase);
        var skills   = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { AgentConfig.Dir, ProjectDir(workspace) })   // project last: it wins
        {
            foreach (var p in LoadPersonas(Path.Combine(root, "personas"))) personas[p.Name] = p;
            foreach (var s in LoadSkills(Path.Combine(root, "skills")))     skills[s.Name]   = s;
        }
        return new PromptLibrary(workspace,
            personas.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            skills.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public PersonaDefinition? Persona(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Personas.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public SkillDefinition? Skill(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Skills.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<PersonaDefinition> LoadPersonas(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).OrderBy(f => f))
        {
            PersonaDefinition? p = null;
            try
            {
                var (meta, body) = FrontmatterParser.Parse(File.ReadAllText(file));
                var name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileNameWithoutExtension(file);
                p = new PersonaDefinition
                {
                    Name = name,
                    Description = meta.TryGetValue("description", out var d) ? d : "",
                    Tools = FrontmatterParser.ParseArray(meta.TryGetValue("tools", out var t) ? t : null),
                    Skills = FrontmatterParser.ParseArray(meta.TryGetValue("skills", out var s) ? s : null),
                    MaxTurns = meta.TryGetValue("max_turns", out var m) && int.TryParse(m, out var mt) ? mt
                             : meta.TryGetValue("max_iterations", out var m2) && int.TryParse(m2, out var mt2) ? mt2 : 0,
                    SystemPrompt = body,
                    Source = file,
                    Extra = meta,
                };
            }
            catch (Exception ex) { Log.Warn($"[personas] {file}: {ex.Message}"); }
            if (p is not null) yield return p;
        }
    }

    private static IEnumerable<SkillDefinition> LoadSkills(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).OrderBy(f => f))
        {
            SkillDefinition? s = null;
            try
            {
                var (meta, body) = FrontmatterParser.Parse(File.ReadAllText(file));
                var name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileNameWithoutExtension(file);
                s = new SkillDefinition
                {
                    Name = name,
                    Description = meta.TryGetValue("description", out var d) ? d : body.Split('\n')[0].TrimStart('#', ' '),
                    Playbook = body,
                    Always = meta.TryGetValue("always", out var a) && (a.Equals("true", StringComparison.OrdinalIgnoreCase) || a == "1"),
                    Source = file,
                    Extra = meta,
                };
            }
            catch (Exception ex) { Log.Warn($"[skills] {file}: {ex.Message}"); }
            if (s is not null) yield return s;
        }
    }

    // ── scaffolding ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates <c>&lt;workspace&gt;/.vanity-agent</c> with an instructions file, an example persona and an
    /// example skill. Existing files are left alone. Returns what was created.</summary>
    public static string Scaffold(string workspace)
    {
        var dir = ProjectDir(workspace);
        var created = new List<string>();
        void Put(string rel, string content)
        {
            var path = Path.Combine(dir, rel);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
            created.Add(rel.Replace('\\', '/'));
        }
        Put("instructions.md", InstructionsTemplate);
        Put(Path.Combine("personas", "reviewer.md"), ReviewerPersona);
        Put(Path.Combine("personas", "writer.md"), WriterPersona);
        Put(Path.Combine("skills", "code-review.md"), CodeReviewSkill);
        Put(Path.Combine("skills", "commit-message.md"), CommitMessageSkill);
        return created.Count == 0
            ? $"{ProjectFolder}/ already has every example file; nothing created."
            : $"created in {ProjectFolder}/: " + string.Join(", ", created);
    }

    /// <summary>The same examples under the agent home, so every workspace has them.</summary>
    public static string ScaffoldGlobal()
    {
        var created = new List<string>();
        void Put(string rel, string content)
        {
            var path = Path.Combine(AgentConfig.Dir, rel);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
            created.Add(rel.Replace('\\', '/'));
        }
        Put(Path.Combine("personas", "reviewer.md"), ReviewerPersona);
        Put(Path.Combine("skills", "code-review.md"), CodeReviewSkill);
        Put(Path.Combine("skills", "commit-message.md"), CommitMessageSkill);
        return created.Count == 0 ? "nothing created." : "created: " + string.Join(", ", created);
    }

    private const string InstructionsTemplate = """
        # Project instructions

        Read by the agent on every turn. Keep it short and factual: what the project is, how to build and test it,
        the conventions to follow, what not to touch.

        ## Build and test

        - build: `...`
        - test: `...`
        - run: `...`

        ## Conventions

        - ...

        ## Do not touch

        - ...
        """;

    private const string ReviewerPersona = """
        ---
        name: reviewer
        description: Reviews changes for correctness and risk, reports findings, never edits files.
        tools: [read_file, grep, glob, bash, web_fetch, skill_view, memory, task_scratchpad]
        skills: [code-review]
        max_turns: 40
        ---
        You are a meticulous code reviewer. You read the change and the code around it, run the build and the tests when
        they exist, and report what is wrong, ranked by severity, each finding with the file, the line and the concrete
        failure it causes. You do not edit files and you do not propose rewrites of what works. When nothing is wrong,
        you say so in one line.
        """;

    private const string WriterPersona = """
        ---
        name: writer
        description: Writes and edits documentation, READMEs and release notes from what the code actually does.
        skills: []
        ---
        You are a technical writer. You document what the code does, verified by reading it, in short plain sentences:
        one idea per sentence, no marketing, no emojis. You keep the project's existing structure and tone, and you ask
        the code, not your memory, when a detail matters.
        """;

    private const string CodeReviewSkill = """
        ---
        name: code-review
        description: How to review a diff or a pull request and how to report the findings.
        ---
        # Code review

        1. Get the change: `git diff`, `git diff --staged`, or `git diff <base>...HEAD`; `git log --oneline -10` for context.
        2. For every changed function, read the callers (grep the name) before judging the change.
        3. Check, in this order: wrong behaviour on real inputs; error handling and resource cleanup; concurrency;
           security (injection, secrets, paths); performance on large inputs; missing or misleading tests.
        4. Run the build and the tests when the project has them; quote the real output.
        5. Report: one finding per bullet, most severe first, `path:line`, what happens and with which input.
           Style remarks go last, under their own heading, or not at all.
        """;

    private const string CommitMessageSkill = """
        ---
        name: commit-message
        description: Writing a commit message for the staged changes.
        ---
        # Commit message

        - Read `git diff --staged`; never describe what was not staged.
        - First line: imperative, under 72 characters, what the change does ("Add retry to the upload client").
        - Blank line, then why it was needed and anything a reader would not guess from the diff.
        - No ticket numbers unless the project uses them; no emojis.
        """;
}
