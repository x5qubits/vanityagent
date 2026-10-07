using System.Text;
using System.Text.Json;
using VanityAgent.Agent;
using VanityAgent.Auth;
using VanityAgent.Infra;
using VanityAgent.Llm;
using VanityAgent.Memory;
using VanityAgent.Tools;

namespace VanityAgent.Host;

/// <summary>The console: argument parsing, the first-run setup, the REPL with its slash commands, and the rendering
/// of what the agent does while it works.</summary>
public sealed class ConsoleHost
{
    private const string Version = "1.0.0";

    private readonly Options _o;
    private string _workspace;
    private AiOptions _opts = new();
    private LlmRouter _router = null!;
    private ToolRegistry _tools = null!;
    private AgentLoop _loop = null!;
    private UsageTracker _usage = null!;
    private JsonFileMemoryStore _memory = null!;
    private MemoryAutoSave? _autoSave;
    private CancellationTokenSource? _turnCts;
    private PromptLibrary _library = null!;
    private PersonaDefinition? _persona;
    private string? _personaName;
    private readonly HashSet<string> _pinnedSkills = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Options
    {
        public string? Cwd;
        public string? Profile;
        public string? Model;
        public int MaxTurns = 60;
        public bool Sandbox;
        public bool Verbose;
        public bool NoMemory;
        public string? Login;
        public string? Persona;
        public bool Usage;
        public List<string> Skills = [];
        public List<string> Deny = [];
        public List<string> Prompt = [];
        public bool Help, ShowVersion;
    }

    // ── entry ────────────────────────────────────────────────────────────────────────────────────────────────────

    public static async Task<int> RunAsync(string[] args)
    {
        // A crash must leave a trace: the exception goes to the log and to stderr in full before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { Log.Error("UNHANDLED: " + e.ExceptionObject); Log.Flush(); } catch { }
            try { Console.Error.WriteLine("fatal: " + e.ExceptionObject); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { try { Log.Error("UNOBSERVED: " + e.Exception); } catch { } e.SetObserved(); };
        try { Console.OutputEncoding = Encoding.UTF8; Console.InputEncoding = Encoding.UTF8; } catch { }
        var o = Parse(args);
        if (o.Help) { PrintUsage(); return 0; }
        if (o.ShowVersion) { Console.WriteLine("vanity-agent " + Version); return 0; }

        AgentConfig.EnsureDirs();
        Log.Initialize(Path.Combine(AgentConfig.LogsDir, "vanity-agent.log"));
        Log.Verbose = o.Verbose;
        // The per-call prompt log (logs/_shared/prompts) carries project content; VANITY_AGENT_PROMPT_LOG=0 turns it off.
        LlmCallLogger.Enabled = Environment.GetEnvironmentVariable("VANITY_AGENT_PROMPT_LOG") is not ("0" or "false" or "off");
        Log.Info("vanity-agent " + Version + " starting");
        OAuthTokenRefresher.Persist = (_, p) => AgentConfig.PersistOAuth(p);
        OAuthTokenRefresher.Reload  = (_, name) => AgentConfig.ReloadProfile(name);
        VanityPathHelper.Sandbox = o.Sandbox;
        VanityPathHelper.DeniedPaths = o.Deny.ToArray();

        var host = new ConsoleHost(o);
        try
        {
            if (o.Login is not null)
            {
                await host.LoginAsync(o.Login, null, CancellationToken.None);
                return 0;
            }
            if (!await host.EnsureProfilesAsync()) { HoldWindow(); return 2; }
            host.Build();
            if (o.Usage) { await host.PrintUsageStatsAsync(CancellationToken.None); return 0; }

            string? prompt = o.Prompt.Count > 0 ? string.Join(" ", o.Prompt) : null;
            if (prompt is null && Console.IsInputRedirected)
            {
                var piped = (await Console.In.ReadToEndAsync()).Trim();
                if (piped.Length > 0) prompt = piped;
            }
            if (prompt is not null) return await host.OneShotAsync(prompt);
            await host.ReplAsync();
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex)
        {
            Log.Error(ex);
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine("details: " + AgentConfig.LogsDir);
            HoldWindow();
            return 1;
        }
        finally { Log.Flush(); }
    }

    /// <summary>A window opened by double-clicking the exe closes the instant the process ends, taking the error
    /// with it. When the console is interactive, wait for Enter before leaving on a failure.</summary>
    private static void HoldWindow()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected) return;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  Press Enter to close.");
            Console.ResetColor();
            Console.ReadLine();
        }
        catch { }
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "-h": case "--help": o.Help = true; break;
                case "-V": case "--version": o.ShowVersion = true; break;
                case "-C": case "--cwd": case "--workspace": o.Cwd = Next(); break;
                case "-P": case "--profile": o.Profile = Next(); break;
                case "-m": case "--model": o.Model = Next(); break;
                case "--max-turns": o.MaxTurns = int.Parse(Next()); break;
                case "--sandbox": o.Sandbox = true; break;
                case "-v": case "--verbose": o.Verbose = true; break;
                case "--no-memory": o.NoMemory = true; break;
                case "--deny": o.Deny.Add(Next()); break;
                case "--login": o.Login = Next(); break;
                case "--persona": o.Persona = Next(); break;
                case "--usage": o.Usage = true; break;
                case "--skill": o.Skills.Add(Next()); break;
                case "-p": case "--print": if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) o.Prompt.Add(Next()); break;
                default:
                    if (a.StartsWith('-') && o.Prompt.Count == 0) throw new ArgumentException("unknown option " + a + " (see --help)");
                    o.Prompt.Add(a); break;
            }
        }
        return o;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            vanity-agent - a general-purpose command-line agent

            usage: vanity-agent [options] [prompt...]

              With a prompt (or text on stdin) it answers once and exits; without one it opens a chat in the
              current directory. Type /help inside the chat for the commands.

            options:
              -C, --cwd <dir>        work in <dir> instead of the current directory
              -P, --profile <name>   use this AI profile first (see /profiles)
              -m, --model <model>    use this model on the active profile for this run
              --max-turns <n>        tool-call turns one request may take (default 60)
              --sandbox              confine the file tools to the working directory
              --deny <path>          a folder the agent must not touch (repeatable)
              --no-memory            do not load or save project memory notes
              --login <provider>     sign in and exit: openai | grok | antigravity | anthropic
              --usage                show what is left on each login (weekly / five-hour limits) and what this project spent, then exit
              --persona <name>       run as a persona from .vanity-agent/personas or ~/.vanity-agent/personas
              --skill <name>         pin a skill for the session (repeatable)
              -v, --verbose          echo the diagnostic log to the console
              -V, --version          print the version
              -h, --help             this text

            state lives in %USERPROFILE%\.vanity-agent (VANITY_AGENT_HOME overrides it).
            """);
    }

    private ConsoleHost(Options o)
    {
        _o = o;
        _workspace = Path.GetFullPath(o.Cwd ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(_workspace)) throw new DirectoryNotFoundException(_workspace);
    }

    // ── setup ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At least one usable profile, or the first-run setup. False when the operator declined.</summary>
    private async Task<bool> EnsureProfilesAsync()
    {
        _opts = AgentConfig.Load();
        if (_opts.Profiles.Any(Usable)) return true;
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"No AI profile configured. Run `vanity-agent` interactively once, or `vanity-agent --login openai`, or create {AgentConfig.ConfigFile} (see config.example.json).");
            return false;
        }
        Dim("");
        Cyan("  Welcome to vanity-agent. No AI provider is configured yet.");
        Dim($"  Config: {AgentConfig.ConfigFile}");
        Dim("");
        var ok = await SetupWizardAsync(CancellationToken.None);
        _opts = AgentConfig.Load();
        return ok && _opts.Profiles.Any(Usable);
    }

    private static bool Usable(AiProfile p) =>
        p.Enabled && (p.ApiKeys.Length == 0 || p.ApiKeys.Any(k => !string.IsNullOrWhiteSpace(k) && !k.StartsWith("YOUR_")));

    private async Task<bool> SetupWizardAsync(CancellationToken ct)
    {
        Console.WriteLine("  How do you want to connect?");
        Console.WriteLine("    1  OpenAI       - sign in with ChatGPT (subscription, device code)");
        Console.WriteLine("    2  OpenAI       - API key");
        Console.WriteLine("    3  Anthropic    - API key or token");
        Console.WriteLine("    4  Google       - Antigravity sign in (subscription, browser)");
        Console.WriteLine("    5  Gemini       - API key");
        Console.WriteLine("    6  Grok (xAI)   - sign in (subscription, device code)");
        Console.WriteLine("    7  Grok (xAI)   - API key");
        Console.WriteLine("    8  DeepSeek / Mistral / Groq / OpenRouter / other OpenAI-compatible - API key");
        Console.WriteLine("    9  Ollama       - local, no key");
        Console.WriteLine("    0  quit");
        var choice = (await AskAsync("  Choice: ", ct) ?? "").Trim();
        try
        {
            switch (choice)
            {
                case "1": await LoginAsync("openai", null, ct); return true;
                case "2": await KeyAsync("openai", null, null, ct); return true;
                case "3": await LoginAsync("anthropic", null, ct); return true;
                case "4": await LoginAsync("antigravity", null, ct); return true;
                case "5": await KeyAsync("gemini", null, null, ct); return true;
                case "6": await LoginAsync("grok", null, ct); return true;
                case "7": await KeyAsync("grok", null, null, ct); return true;
                case "8": await KeyAsync(null, null, null, ct); return true;
                case "9": await KeyAsync("ollama", null, null, ct); return true;
                default: return false;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Red("  " + ex.Message); return false; }
    }

    private void Build()
    {
        _opts = AgentConfig.Load();
        if (_o.Profile is not null) AgentConfig.MoveFirst(_opts, _o.Profile);
        if (_o.Model is not null && _opts.Profiles.Count > 0)
        {
            var p = _opts.Profiles[0];
            p.Models = new[] { _o.Model }.Concat(p.Models.Where(m => !m.Equals(_o.Model, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        foreach (var p in _opts.Profiles) if (p.Layers.Length == 0) p.Layers = ["any"];
        _router = new LlmRouter(_opts, LlmRouter.CallLogger);

        // Per-workspace state: in the project's own .vanity-agent folder when it exists (so it travels with the
        // project), otherwise under the agent home.
        var project = PromptLibrary.HasProjectDir(_workspace) ? PromptLibrary.ProjectDir(_workspace) : AgentConfig.ProjectDir(_workspace);
        _usage  = new UsageTracker(Path.Combine(project, "usage.json"));
        _memory = new JsonFileMemoryStore(Path.Combine(project, "memory.json"));
        // The smart memory: after every turn that used tools, a background model call records what the steps proved.
        _autoSave = _o.NoMemory ? null : new MemoryAutoSave(_memory, SideCallAsync, line => Dim("  [" + line + "]"));

        _library = PromptLibrary.Load(_workspace);
        _personaName ??= _o.Persona;
        _persona = _library.Persona(_personaName);
        if (_personaName is not null && _persona is null)
        {
            Red($"  no persona '{_personaName}' (/personas lists them); running as the default agent");
            _personaName = null;
        }
        foreach (var s in _o.Skills) if (_library.Skill(s) is not null) _pinnedSkills.Add(s); else Red($"  no skill '{s}' (/skills lists them)");
        _tools  = BuildTools(includeAgent: true, _persona);

        var sessionId = Guid.NewGuid().ToString("N");
        var maxTurns = _persona is { MaxTurns: > 0 } ? _persona.MaxTurns : _o.MaxTurns;
        _loop = new AgentLoop(new LayerClient(this), _tools, "", ConsoleEvents(depth: 0), _usage, maxTurns, "main", sessionId, deferredTools: DeferredByDefault);
    }

    /// <summary>Tools whose schemas cost thousands of prompt tokens and are rarely needed: listed in one line each and
    /// loaded on demand with load_tools (see DeferredTools). Everything else is in the schema on every call.</summary>
    private static readonly string[] DeferredByDefault = ["computer", "page_view", "image_gen"];

    private ToolRegistry BuildTools(bool includeAgent, PersonaDefinition? persona)
    {
        var tools = new ToolRegistry();
        tools.Register(new SkillViewTool(() => _library));
        tools.Register(new BashTool(_workspace));
        tools.Register(new ReadFileTool(_workspace));
        tools.Register(new WriteFileTool(_workspace));
        tools.Register(new WriteFilesTool(_workspace));
        tools.Register(new EditFileTool(_workspace));
        tools.Register(new GrepTool(_workspace));
        tools.Register(new GlobTool(_workspace));
        tools.Register(new WebFetchTool(_workspace));
        tools.Register(new WebSearchTool());
        tools.Register(new ImageGenTool(_workspace, () => _opts));
        if (OperatingSystem.IsWindows())
        {
            tools.Register(new PageViewTool(_workspace));   // headless Edge/Chrome screenshots + DOM probes
            tools.Register(new ComputerTool());             // screen capture and mouse/keyboard control
        }
        tools.Register(new DateTimeTool());
        tools.Register(new TaskScratchpadTool());
        if (!_o.NoMemory) tools.Register(new MemoryTool(_memory));
        if (includeAgent) tools.Register(new AgentTool(RunSubAgentAsync));
        tools.Register(new ListToolsTool(tools));

        // A persona with a `tools:` list gets exactly those (plus skill_view and list_tools, which are harmless).
        if (persona is { Tools.Length: > 0 })
        {
            var allowed = new ToolRegistry();
            foreach (var t in tools.Tools)
                if (persona.Tools.Contains(t.Definition.Name, StringComparer.OrdinalIgnoreCase) || t.Definition.Name is "skill_view" or "list_tools")
                    allowed.Register(t);
            tools = allowed;
        }
        return tools;
    }

    /// <summary>The skills inlined in the prompt (the persona's, the pinned ones, the `always` ones) and the rest,
    /// which the model may load with skill_view.</summary>
    private (List<SkillDefinition> Active, List<SkillDefinition> Loadable) SkillsFor(PersonaDefinition? persona, bool includePinned)
    {
        var active = new List<SkillDefinition>();
        foreach (var s in _library.Skills)
        {
            bool pinned = s.Always
                || (persona?.Skills.Contains(s.Name, StringComparer.OrdinalIgnoreCase) ?? false)
                || (includePinned && _pinnedSkills.Contains(s.Name));
            if (pinned) active.Add(s);
        }
        var loadable = _library.Skills.Where(s => !active.Contains(s)).ToList();
        return (active, loadable);
    }

    /// <summary>A sub-agent: a fresh loop, its own shell, every tool but `agent`, optionally a persona.</summary>
    private async Task<string> RunSubAgentAsync(string task, string? personaName, CancellationToken ct)
    {
        var persona = _library.Persona(personaName);
        if (personaName is not null && persona is null)
            return $"Error: no persona named '{personaName}'. Defined personas: {string.Join(", ", _library.Personas.Select(p => p.Name))}";
        var tools = BuildTools(includeAgent: false, persona);
        var id = "sub-" + Guid.NewGuid().ToString("N")[..6];
        var maxTurns = persona is { MaxTurns: > 0 } ? persona.MaxTurns : Math.Max(10, _o.MaxTurns / 2);
        var loop = new AgentLoop(new LayerClient(this), tools, "", ConsoleEvents(depth: 1), _usage, maxTurns, id, deferredTools: DeferredByDefault);
        var prevDepth = AgentToolContext.Depth;
        AgentToolContext.Depth = 1;
        try
        {
            var (active, loadable) = SkillsFor(persona, includePinned: false);
            loop.SystemPrompt = SystemPrompt.Build(_workspace, ActiveModel(), tools.All.Select(t => t.Name), await MemoryBlockAsync(task, ct),
                subAgent: true, persona: persona, activeSkills: active, loadableSkills: loadable);
            var result = await loop.SendAsync(task, ct);
            _autoSave?.Handle(task, result, loop.LastSteps.ToList(), _workspace);
            return result;
        }
        finally { AgentToolContext.Depth = prevDepth; }
    }

    private AgentEvents ConsoleEvents(int depth)
    {
        var pad = new string(' ', 2 + depth * 4);
        var tag = depth == 0 ? "" : "[sub] ";
        return new AgentEvents
        {
            OnStep = (iter, max) => { if (depth == 0) { EndStream(); SpinStart(iter == 1 ? "waiting for " + (ActiveModel() ?? "the model") : $"waiting for {ActiveModel()} (step {iter})"); } },
            OnModel = (_, _) => { if (depth == 0) { SpinStop(); EndStream(); } },
            OnToolCall = (tool, preview) => { Console.ForegroundColor = ConsoleColor.DarkYellow; Console.WriteLine($"{pad}→ {tag}{tool}{(preview.Length > 0 ? "  " + preview : "")}"); Console.ResetColor(); },
            OnToolResult = (tool, sec, chars, err) => { Console.ForegroundColor = err ? ConsoleColor.Red : ConsoleColor.DarkGreen; Console.WriteLine($"{pad}{(err ? "✗" : "←")} {tag}{tool}  ({sec.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s · {chars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} chars)"); Console.ResetColor(); },
            // Reasoning that already streamed live is not printed a second time when the call returns.
            OnThought = text => { if (depth > 0 || _streamedThought > 0) return; Console.ForegroundColor = ConsoleColor.DarkGray; foreach (var line in Wrap(text.Trim(), 110)) Console.WriteLine(pad + line); Console.ResetColor(); },
        };
    }

    // ── live thoughts: the model's reasoning as it streams (providers that stream it; others show it at the end) ──

    private int _streamedThought;
    private string? _streamKind;
    private int _streamCol;

    private void OnDelta(string kind, string chunk)
    {
        if (AgentToolContext.Depth != 0 || Console.IsOutputRedirected || kind != "thought") return;
        lock (ConsoleGate)
        {
            if (_streamKind is null)
            {
                SpinStop();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  · ");
                _streamKind = kind; _streamCol = 4;
            }
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var ch in chunk.Replace("\r", ""))
            {
                if (ch == '\n') { Console.Write("\n    "); _streamCol = 4; continue; }
                if (_streamCol >= 110 && ch == ' ') { Console.Write("\n    "); _streamCol = 4; continue; }
                Console.Write(ch); _streamCol++;
            }
            Console.ResetColor();
            _streamedThought += chunk.Length;
        }
    }

    private void EndStream()
    {
        lock (ConsoleGate)
        {
            if (_streamKind is not null) { Console.WriteLine(); _streamKind = null; }
        }
    }

    private string? ActiveModel()
    {
        var p = _opts.Profiles.FirstOrDefault(Usable);
        return p is null ? null : p.Models.FirstOrDefault();
    }

    private AiProfile? ActiveProfile() => _opts.Profiles.FirstOrDefault(Usable);

    /// <summary>The memory block for this request: the notes whole when the store is small, a model-filtered
    /// selection when it is large. Waits briefly for the previous turn's analysis so its facts are included.</summary>
    private async Task<string?> MemoryBlockAsync(string? task, CancellationToken ct)
    {
        if (_o.NoMemory) return null;
        await MemoryAutoSave.WhenSavedAsync(_memory, TimeSpan.FromSeconds(4), ct);
        var block = await PromptMemory.BlockAsync(_memory, task, SideCallAsync, ct);
        return block.Length == 0 ? null : block;
    }

    /// <summary>A short side call on the configured provider (the memory analyzer, judge and filter): no tools, a
    /// hard output cap, the same failover as every other call.</summary>
    private async Task<string> SideCallAsync(string system, string user, CancellationToken ct)
    {
        var before = LlmCallScope.MaxOutputTokens.Value;
        LlmCallScope.MaxOutputTokens.Value = 2500;
        var prevDelta = LlmCallScope.OnDelta.Value;
        LlmCallScope.OnDelta.Value = null;   // never stream a side call's reasoning to the console
        try
        {
            var r = await _router.CallWithLayerAsync("any", system, [ConversationMessage.FromUser(user)], [], ct);
            _usage?.Track(r.PromptTokens ?? 0, r.CompletionTokens ?? 0, r.CachedTokens ?? 0, r.ModelName);
            return r.Text ?? "";
        }
        finally { LlmCallScope.MaxOutputTokens.Value = before; LlmCallScope.OnDelta.Value = prevDelta; }
    }

    /// <summary>The loop's client: every call goes to the current router (rebuilt after a config change) on the one layer.</summary>
    private sealed class LayerClient(ConsoleHost host) : ILlmClient
    {
        public Task<LlmResponse> CallAsync(string systemPrompt, IReadOnlyList<ConversationMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
            => host._router.CallWithLayerAsync("any", systemPrompt, history, tools, ct);
        public Task<LlmResponse> CallWithLayerAsync(string layer, string systemPrompt, IReadOnlyList<ConversationMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken ct, long userId = 0)
            => host._router.CallWithLayerAsync(layer, systemPrompt, history, tools, ct);
    }

    // ── running ──────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> OneShotAsync(string prompt)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var reply = await TurnAsync(prompt, cts.Token, printReply: false);
        if (reply is null) return 1;
        Console.WriteLine(reply);
        await MemoryAutoSave.WhenSavedAsync(_memory, TimeSpan.FromSeconds(60), CancellationToken.None);   // let the analysis land before exit
        return 0;
    }

    private async Task ReplAsync()
    {
        var p = ActiveProfile();
        Console.WriteLine();
        Cyan("  vanity-agent " + Version + " · " + _workspace);
        Dim($"  profile: {p?.Name} ({p?.Provider}/{ActiveModel()})" + (OAuthTokenRefresher.UsesOAuth(p!) ? " · signed in" + (string.IsNullOrEmpty(p!.OAuthAccountId) ? "" : " as " + p.OAuthAccountId) : " · api key"));
        PrintContextLine();
        Dim("  /help for commands · Ctrl+C stops the current task · /quit to exit");
        Console.WriteLine();

        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl+C during a task stops the task; at the prompt it ends the program.
            var cts = _turnCts;
            if (cts is { IsCancellationRequested: false }) { e.Cancel = true; cts.Cancel(); Dim("  [stopping…]"); }
            else { Log.Flush(); }
        };

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("You: ");
            Console.ResetColor();
            var input = Console.ReadLine();
            if (input is null) break;
            input = input.Trim();
            if (input.Length == 0) continue;
            if (input.StartsWith('/'))
            {
                if (!await CommandAsync(input)) break;
                continue;
            }
            using var cts = new CancellationTokenSource();
            _turnCts = cts;
            try { await TurnAsync(input, cts.Token, printReply: true); }
            finally { _turnCts = null; }
        }
        await MemoryAutoSave.WhenSavedAsync(_memory, TimeSpan.FromSeconds(20), CancellationToken.None);
        Log.Info("vanity-agent done");
    }

    private void PrintContextLine()
    {
        var parts = new List<string>();
        var ins = SystemPrompt.ProjectInstructions(_workspace);
        if (ins.Count > 0) parts.Add("instructions: " + string.Join(", ", ins.Select(i => i.File)));
        if (PromptLibrary.HasProjectDir(_workspace)) parts.Add("project dir: " + PromptLibrary.ProjectFolder + "/");
        if (_persona is not null) parts.Add("persona: " + _persona.Name);
        var (active, loadable) = SkillsFor(_persona, includePinned: true);
        if (active.Count > 0) parts.Add("skills: " + string.Join(", ", active.Select(s => s.Name)));
        if (loadable.Count > 0) parts.Add($"{loadable.Count} loadable skill(s)");
        if (parts.Count > 0) Dim("  " + string.Join(" · ", parts));
    }

    private async Task<string?> TurnAsync(string message, CancellationToken ct, bool printReply)
    {
        try
        {
            _library = PromptLibrary.Load(_workspace);   // re-read every turn: a persona or skill edited on disk applies at once
            if (_personaName is not null) _persona = _library.Persona(_personaName) ?? _persona;
            var (active, loadable) = SkillsFor(_persona, includePinned: true);
            _loop.SystemPrompt = SystemPrompt.Build(_workspace, ActiveModel(), _tools.All.Select(t => t.Name), await MemoryBlockAsync(message, ct),
                persona: _persona, activeSkills: active, loadableSkills: loadable);
            _streamedThought = 0; _streamKind = null;
            LlmCallScope.OnDelta.Value = OnDelta;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reply = await _loop.SendAsync(message, ct);
            clock.Stop();
            EndStream();
            _autoSave?.Handle(message, reply, _loop.LastSteps.ToList(), _workspace);
            if (printReply)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Agent: ");
                Console.ResetColor();
                Console.WriteLine(reply.Trim());
                var (pt, ctok, cached) = _loop.LastTokenUsage;
                Dim($"  [{_loop.LastModel} · {clock.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s · tokens: {pt:N0} in / {ctok:N0} out / {cached:N0} cached]");
                Console.WriteLine();
            }
            return reply;
        }
        catch (OperationCanceledException) { SpinStop(); Dim("  [stopped]"); return null; }
        catch (Exception ex)
        {
            SpinStop();
            Log.Error(ex);
            Red("  [error] " + ex.Message);
            if (ex.Message.Contains("No enabled AI profiles") || ex.Message.Contains("All ") && ex.Message.Contains("profile(s) failed"))
                Dim("  Check /profiles, /models and the key or login of the active profile. The log is in " + AgentConfig.LogsDir);
            return null;
        }
        finally { SpinStop(); EndStream(); LlmCallScope.OnDelta.Value = null; }
    }

    // ── commands ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Handles a slash command; false means quit.</summary>
    private async Task<bool> CommandAsync(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var rest = parts.Skip(1).ToArray();
        var ct = CancellationToken.None;
        try
        {
            switch (cmd)
            {
                case "/quit": case "/exit": case "/q": return false;
                case "/help": case "/?": PrintHelp(); break;
                case "/login": await LoginAsync(rest.ElementAtOrDefault(0), rest.ElementAtOrDefault(1), ct); Rebuild(); break;
                case "/key": await KeyAsync(rest.ElementAtOrDefault(0), rest.ElementAtOrDefault(1), rest.ElementAtOrDefault(2), ct); Rebuild(); break;
                case "/profiles": case "/profile": PrintProfiles(); break;
                case "/use": Use(rest.ElementAtOrDefault(0)); break;
                case "/model": SetModel(rest.ElementAtOrDefault(0)); break;
                case "/models": await ListModelsAsync(ct); break;
                case "/remove": case "/logout": Remove(rest.ElementAtOrDefault(0)); break;
                case "/reset": case "/clear": case "/new": _loop.Reset(); Dim("  [conversation cleared]"); break;
                case "/usage": await PrintUsageStatsAsync(ct); break;
                case "/cwd": case "/cd": ChangeWorkspace(rest.Length > 0 ? string.Join(' ', rest) : null); break;
                case "/tools": foreach (var t in _tools.All.OrderBy(t => t.Name)) Console.WriteLine($"  {t.Name,-16} {FirstLine(t.Description)}"); break;
                case "/memory": await MemoryCommandAsync(rest, ct); break;
                case "/persona": SetPersona(rest.ElementAtOrDefault(0)); break;
                case "/personas": PrintPersonas(); break;
                case "/skills": PrintSkills(); break;
                case "/skill": PinSkill(rest.ElementAtOrDefault(0)); break;
                case "/init": Dim("  " + PromptLibrary.Scaffold(_workspace)); Build(); PrintContextLine(); break;
                case "/project": Console.WriteLine("  " + PromptLibrary.ProjectDir(_workspace) + (PromptLibrary.HasProjectDir(_workspace) ? "" : "  (not created yet; /init creates it)")); Console.WriteLine("  global personas/skills: " + AgentConfig.Dir); break;
                case "/sandbox": VanityPathHelper.Sandbox = rest.ElementAtOrDefault(0) is not "off"; Dim("  [sandbox " + (VanityPathHelper.Sandbox ? "on" : "off") + "]"); break;
                case "/verbose": Log.Verbose = rest.ElementAtOrDefault(0) is not "off"; Dim("  [verbose " + (Log.Verbose ? "on" : "off") + "]"); break;
                case "/config": Console.WriteLine("  " + AgentConfig.ConfigFile); Console.WriteLine("  " + AgentConfig.ProjectDir(_workspace)); foreach (var kv in AgentConfig.Settings) Console.WriteLine($"  setting {kv.Key} = {Mask(kv.Value)}"); break;
                case "/set": case "/secret": await SetSettingAsync(rest, ct); break;
                case "/tune": Tune(rest); break;
                default: Red($"  unknown command {cmd}; /help lists them"); break;
            }
        }
        catch (OperationCanceledException) { Dim("  [cancelled]"); }
        catch (Exception ex) { Log.Error(ex); Red("  " + ex.Message); }
        return true;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
              /login [openai|grok|antigravity|anthropic] [name]   sign in with a subscription (or paste an Anthropic key/token)
              /key [provider] [key] [model]      add a profile with an API key (interactive when arguments are missing)
              /profiles                          list the configured profiles; the first usable one answers
              /use <name>                        make a profile the active one
              /model <model>                     set the model of the active profile
              /tune [setting value]              model settings of the active profile: temperature, top_p, max_tokens, thinking on|off,
                                                 timeout <s>, ctx <tokens>; no arguments shows them. Gemini/Antigravity thinking depth is
                                                 the model name's suffix (-low, -medium, -high), so /model picks it there
              /models                            list the models the active profile can use (asked from the provider)
              /remove <name>                     delete a profile (and its stored tokens)
              /reset                             clear the conversation
              /usage                             token usage of this process
              /cwd [dir]                         show or change the working directory
              /tools                             list the tools
              /memory [list|add <text>|delete <n>]   project notes the agent remembers between sessions
              /init                              create .vanity-agent/ here with instructions.md, example personas and skills
              /project                           where the project directory and the global personas/skills live
              /personas, /persona <name|off>     list personas; switch persona (conversation is kept)
              /skills, /skill <name>             list skills; pin or unpin a skill for this session
              /sandbox on|off                    confine the file tools to the working directory
              /verbose on|off                    echo the diagnostic log
              /config                            where the config and project state live, and the settings stored there
              /set <name> [value]                store a machine-local setting in config.json (asked hidden when the value is omitted);
                                                 GoogleClientSecret = the Antigravity client secret Google needs to refresh the login
              /quit                              exit
            """);
    }

    private void Rebuild()
    {
        var keepHistory = _loop?.History.ToList();
        Build();
        Dim($"  [active: {ActiveProfile()?.Name} · {ActiveModel()}]");
    }

    private async Task LoginAsync(string? provider, string? name, CancellationToken ct)
    {
        provider = (provider ?? "").Trim().ToLowerInvariant();
        if (provider is "google" or "gemini-cli") provider = "antigravity";
        if (provider is "chatgpt" or "codex") provider = "openai";
        if (provider is "xai") provider = "grok";
        if (provider is "claude") provider = "anthropic";
        if (provider is not ("openai" or "grok" or "antigravity" or "anthropic"))
        {
            Console.WriteLine("  Sign in with: /login openai (ChatGPT) · /login grok (xAI) · /login antigravity (Google) · /login anthropic (paste key/token)");
            Console.WriteLine("  API keys for any provider: /key");
            return;
        }
        var ui = new LoginUi { Say = Console.WriteLine, Emphasis = s => { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(s); Console.ResetColor(); }, Ask = AskAsync };
        var profileName = string.IsNullOrWhiteSpace(name) ? provider switch { "openai" => "chatgpt", "antigravity" => "antigravity", "grok" => "grok", _ => "anthropic" } : name!;
        AiProfile profile;
        switch (provider)
        {
            case "openai": profile = await OAuthFlows.LoginOpenAiAsync(profileName, ui, ct); break;
            case "grok": profile = await OAuthFlows.LoginGrokAsync(profileName, ui, ct); break;
            case "antigravity": profile = await OAuthFlows.LoginAntigravityAsync(profileName, "cli", ui, ct); break;
            default:
                Console.WriteLine("  Paste an Anthropic API key (sk-ant-api...) from https://console.anthropic.com/settings/keys,");
                Console.WriteLine("  or a bearer token (sk-ant-oat...). The key is stored in " + AgentConfig.ConfigFile);
                var secret = await ReadSecretAsync("  Key or token: ", ct);
                profile = OAuthFlows.AnthropicFromSecret(profileName, secret ?? "");
                break;
        }
        // Keep the models an existing profile of that name already had.
        var existing = AgentConfig.Load().Profiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (existing is { Models.Length: > 0 }) profile.Models = existing.Models;
        AgentConfig.Upsert(profile);
        Green($"  Signed in: profile '{profile.Name}' ({profile.Provider}" + (string.IsNullOrEmpty(profile.OAuthAccountId) ? "" : ", " + profile.OAuthAccountId) + $") · model {profile.Models.FirstOrDefault()}");
        Dim("  /model <name> changes the model; /models lists what the login can use.");
    }

    private static readonly (string Id, string Label, string DefaultModel)[] KeyProviders =
    [
        ("openai",     "OpenAI",                      "gpt-5.5"),
        ("anthropic",  "Anthropic",                   "claude-sonnet-5-5"),
        ("gemini",     "Google Gemini",               "gemini-3.1-pro"),
        ("grok",       "Grok (xAI)",                  "grok-4.5"),
        ("deepseek",   "DeepSeek",                    "deepseek-chat"),
        ("mistral",    "Mistral",                     "mistral-large-latest"),
        ("groq",       "Groq",                        "llama-3.3-70b-versatile"),
        ("openrouter", "OpenRouter",                  "anthropic/claude-sonnet-4.5"),
        ("perplexity", "Perplexity",                  "sonar-pro"),
        ("ollama",     "Ollama (local)",              "qwen3:32b"),
        ("custom",     "Custom OpenAI-compatible URL", ""),
    ];

    private async Task KeyAsync(string? provider, string? key, string? model, CancellationToken ct)
    {
        provider = provider?.Trim().ToLowerInvariant();
        if (provider is "xai") provider = "grok";
        if (provider is "google") provider = "gemini";
        if (provider is null || !KeyProviders.Any(k => k.Id == provider))
        {
            Console.WriteLine("  Providers:");
            for (int i = 0; i < KeyProviders.Length; i++) Console.WriteLine($"    {i + 1,2}  {KeyProviders[i].Label}");
            var pick = (await AskAsync("  Provider (number or name): ", ct) ?? "").Trim().ToLowerInvariant();
            provider = int.TryParse(pick, out var n) && n >= 1 && n <= KeyProviders.Length ? KeyProviders[n - 1].Id : pick;
            if (!KeyProviders.Any(k => k.Id == provider)) throw new ArgumentException("unknown provider " + provider);
        }
        var def = KeyProviders.First(k => k.Id == provider);
        var profile = new AiProfile { Name = provider, Provider = provider, Enabled = true, DisableThinking = true, JsonMode = true, Layers = ["any"] };

        if (provider == "custom")
        {
            var url = (await AskAsync("  Base URL (e.g. https://api.example.com or http://localhost:8080/v1): ", ct) ?? "").Trim();
            if (url.Length == 0) throw new ArgumentException("a base URL is required");
            profile.BaseUrl = url;
            profile.Provider = "custom";
            var pname = (await AskAsync("  Profile name [custom]: ", ct) ?? "").Trim();
            if (pname.Length > 0) profile.Name = pname;
        }
        if (provider == "ollama")
        {
            var url = (await AskAsync("  Ollama URL [http://localhost:11434]: ", ct) ?? "").Trim();
            if (url.Length > 0) profile.BaseUrl = url;
            profile.ApiKeys = [];
        }
        else
        {
            key ??= await ReadSecretAsync($"  {def.Label} API key: ", ct);
            key = key?.Trim().Trim('"', '\'') ?? "";
            if (key.Length == 0) throw new ArgumentException("an API key is required");
            if (provider == "anthropic")
            {
                var anth = OAuthFlows.AnthropicFromSecret(profile.Name, key);
                profile = anth;
            }
            else profile.ApiKeys = [key];
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            var m = (await AskAsync($"  Model [{def.DefaultModel}]: ", ct) ?? "").Trim();
            model = m.Length > 0 ? m : def.DefaultModel;
        }
        if (model.Length > 0 && !(provider == "anthropic" && profile.Models.Length > 0 && model == def.DefaultModel))
            profile.Models = [model];
        if (profile.Models.Length == 0) throw new ArgumentException("a model name is required");
        AgentConfig.Upsert(profile);
        Green($"  Saved profile '{profile.Name}' ({profile.Provider}) · model {profile.Models[0]}");
    }

    private void PrintProfiles()
    {
        var opts = AgentConfig.Load();
        if (opts.Profiles.Count == 0) { Dim("  (no profiles; /login or /key)"); return; }
        bool first = true;
        foreach (var p in opts.Profiles)
        {
            var usable = Usable(p);
            var marker = usable && first ? "*" : " ";
            if (usable && first) first = false;
            var auth = OAuthTokenRefresher.UsesOAuth(p)
                ? "login" + (string.IsNullOrEmpty(p.OAuthAccountId) ? "" : ":" + p.OAuthAccountId) + (p.OAuthExpiresAt > 0 && p.OAuthExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? " (token expired; refreshes on use)" : "")
                : p.ApiKeys.Length == 0 ? "no key" : "key " + Mask(p.ApiKeys[0]);
            Console.ForegroundColor = usable ? ConsoleColor.White : ConsoleColor.DarkGray;
            Console.WriteLine($"  {marker} {p.Name,-14} {p.Provider,-12} {string.Join(", ", p.Models),-40} {auth}{(p.Enabled ? "" : " (disabled)")}{(string.IsNullOrEmpty(p.BaseUrl) ? "" : " " + p.BaseUrl)}");
            Console.ResetColor();
        }
        Dim("  * = answers first; the others are fallbacks in this order. /use <name> changes the order.");
    }

    private static string Mask(string key)
    {
        key = key.Trim();
        return key.Length <= 8 ? "****" : key[..4] + "…" + key[^4..];
    }

    private void Use(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { PrintProfiles(); return; }
        var opts = AgentConfig.Load();
        var p = opts.Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (p is null) { Red($"  no profile '{name}'"); return; }
        p.Enabled = true;
        AgentConfig.MoveFirst(opts, p.Name);
        AgentConfig.Save(opts);
        _o.Profile = null;
        Rebuild();
    }

    private void SetModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) { Console.WriteLine("  model: " + ActiveModel()); return; }
        var opts = AgentConfig.Load();
        var active = ActiveProfile();
        var p = active is null ? null : opts.Profiles.FirstOrDefault(x => x.Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase));
        if (p is null) { Red("  no active profile"); return; }
        p.Models = new[] { model }.Concat(p.Models.Where(m => !m.Equals(model, StringComparison.OrdinalIgnoreCase))).ToArray();
        AgentConfig.Save(opts);
        _o.Model = null;
        Rebuild();
    }

    private void Remove(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Red("  /remove <profile name>"); return; }
        var before = AgentConfig.Load().Profiles.Count;
        var after = AgentConfig.Remove(name).Profiles.Count;
        if (after == before) { Red($"  no profile '{name}'"); return; }
        Dim($"  [removed '{name}']");
        if (after > 0) Rebuild(); else Dim("  no profiles left; /login or /key to add one");
    }

    private async Task ListModelsAsync(CancellationToken ct)
    {
        var p = ActiveProfile();
        if (p is null) { Red("  no active profile"); return; }
        await OAuthTokenRefresher.EnsureFreshAsync(p, 0, ct: ct);
        var models = await ModelCatalog.ListAsync(p, ct);
        if (models.Count == 0) { Dim("  (the provider returned no models)"); return; }
        Console.WriteLine($"  {models.Count} model(s) on '{p.Name}' ({p.Provider}):");
        foreach (var m in models) Console.WriteLine("    " + m);
        Dim("  /model <name> selects one. A \"flash\" or \"mini\" model answers in seconds; a \"pro\"/\"high\" one can wait half a minute in the provider's queue before its first token.");
    }

    private void ChangeWorkspace(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) { Console.WriteLine("  " + _workspace); return; }
        var full = VanityPathHelper.NormalizeAndResolve(dir, _workspace);
        if (!Directory.Exists(full)) { Red("  no such directory: " + full); return; }
        _workspace = full;
        Build();
        Dim("  [workspace: " + _workspace + " · conversation cleared]");
    }

    internal async Task PrintUsageStatsAsync(CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // 1. What is LEFT on each login: the provider's own weekly / five-hour pools, the same rows its app shows.
        var profiles = AgentConfig.Load().Profiles.Where(Usable).ToList();
        foreach (var p in profiles)
        {
            var isLogin = OAuthTokenRefresher.UsesOAuth(p);
            if (!isLogin && !p.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase) && !p.Provider.Equals("oneprovider", StringComparison.OrdinalIgnoreCase))
            {
                Dim($"  {p.Name} ({p.Provider}, api key): {AiUsageProbe.CreditsNote(p.Provider)}");
                continue;
            }
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  {p.Name} ({p.Provider}{(string.IsNullOrEmpty(p.OAuthAccountId) ? "" : ", " + p.OAuthAccountId)})");
            Console.ResetColor();
            AiUsageProbe.Usage? u = null;
            try { u = await AiUsageProbe.ProbeAsync(p, 0, includeRaw: false, ct); } catch { }
            if (u is null) { Dim("  · limits unavailable (the provider did not answer the usage call; the token may need /login again)"); continue; }
            Console.WriteLine(string.IsNullOrWhiteSpace(u.plan) ? "" : $" · plan: {u.plan}");
            if (u.balance is not null)
                Console.WriteLine($"    balance: {u.balance.remaining.ToString("0.00", inv)} {u.balance.unit} left" + (u.balance.limit is { } lim ? $" of {lim.ToString("0.00", inv)}" : ""));
            foreach (var pool in u.pools)
            {
                if (pool.label.Length > 0) Console.WriteLine("    " + pool.label);
                if (pool.buckets.Count > 0)
                    foreach (var b in pool.buckets)
                        PrintLimit(b.label, b.remaining, b.reset, b.description);
                else
                {
                    if (pool.weeklyRemaining is { } w) PrintLimit("Weekly limit remaining", w, pool.weeklyReset, null);
                    if (pool.fiveHourRemaining is { } f) PrintLimit("Five hour limit remaining", f, pool.fiveHourReset, null);
                }
            }
        }

        // 2. What this agent spent: the current process, then the project's stored months.
        var (calls, pt, ctok, _) = LlmRouter.GetUsageTotals();
        Console.WriteLine(calls == 0
            ? "  this session: no model calls yet"
            : $"  this session: {calls:N0} call(s) · {pt:N0} in / {ctok:N0} out · {_usage.LiveCached:N0} cached");
        if (calls > 0) Dim(LlmRouter.DescribeUsage());
        var months = _usage.Months();
        if (months.Count == 0) { Dim("  this project: nothing recorded yet"); return; }
        Console.WriteLine("  this project, by month (every session, every model):");
        foreach (var m in months.Take(6))
            Console.WriteLine($"    {m.Month}  {m.Calls,6:N0} calls  {m.Prompt,12:N0} in  {m.Completion,10:N0} out  {m.Cached,10:N0} cached   ~${m.CostUsd.ToString("0.00", inv)}");
        Dim("  cost is a rough list-price estimate per model family; subscription logins are not billed per token. file: " + _usage.Path);
    }

    private static void PrintLimit(string label, double remaining, string? reset, string? description)
    {
        Console.ForegroundColor = remaining <= 10 ? ConsoleColor.Red : remaining <= 30 ? ConsoleColor.Yellow : ConsoleColor.Green;
        Console.Write($"      {label,-28} {remaining,4:0}%");
        Console.ResetColor();
        var when = ResetHint(reset);
        if (!string.IsNullOrEmpty(description)) Dim("   " + description);
        else if (when.Length > 0) Dim("   resets " + when);
        else Console.WriteLine();
    }

    private static string ResetHint(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso) || !DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) return "";
        var left = at - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "now";
        if (left.TotalDays >= 1) return $"in {(int)left.TotalDays} day{((int)left.TotalDays == 1 ? "" : "s")}, {left.Hours} hour{(left.Hours == 1 ? "" : "s")}";
        if (left.TotalHours >= 1) return $"in {(int)left.TotalHours} hour{((int)left.TotalHours == 1 ? "" : "s")}, {left.Minutes} min";
        return $"in {left.Minutes} min";
    }

    private async Task SetSettingAsync(string[] rest, CancellationToken ct)
    {
        var key = rest.ElementAtOrDefault(0)?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            Console.WriteLine("  /set <name> [value]   known names: GoogleClientSecret, GoogleClientSecretAlt, GoogleAppClientSecret");
            foreach (var kv in AgentConfig.Settings) Console.WriteLine($"    {kv.Key} = {Mask(kv.Value)}");
            return;
        }
        if (key.Equals("google", StringComparison.OrdinalIgnoreCase)) key = "GoogleClientSecret";
        var value = rest.Length > 1 ? string.Join(' ', rest.Skip(1)).Trim() : await ReadSecretAsync($"  {key}: ", ct);
        AgentConfig.SetSetting(key, value);
        Dim(string.IsNullOrEmpty(value) ? $"  [{key} removed]" : $"  [{key} stored in {AgentConfig.ConfigFile}]");
    }

    /// <summary>Model settings of the active profile, persisted: temperature, top_p, max_tokens, thinking, timeout, ctx.</summary>
    private void Tune(string[] rest)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var opts = AgentConfig.Load();
        var active = ActiveProfile();
        var p = active is null ? null : opts.Profiles.FirstOrDefault(x => x.Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase));
        if (p is null) { Red("  no active profile"); return; }
        if (rest.Length == 0)
        {
            Console.WriteLine($"  {p.Name} ({p.Provider} / {p.Models.FirstOrDefault()})");
            Console.WriteLine($"    temperature  {(p.Temperature is { } t ? t.ToString(inv) : "provider default")}");
            Console.WriteLine($"    top_p        {(p.TopP is { } tp ? tp.ToString(inv) : "provider default")}");
            Console.WriteLine($"    max_tokens   {(p.MaxTokens > 0 ? p.MaxTokens.ToString(inv) : "default (16000 chat, 32000 Gemini)")}");
            Console.WriteLine($"    thinking     {(p.DisableThinking ? "off" : "on (provider default)")}");
            Console.WriteLine($"    timeout      {p.RequestTimeoutMs / 1000}s");
            Console.WriteLine($"    ctx          {(p.NumCtx > 0 ? p.NumCtx.ToString(inv) + " (Ollama num_ctx)" : "model default")}");
            Dim("  /tune temperature 0.2 · /tune top_p 0.9 · /tune max_tokens 8000 · /tune thinking off · /tune timeout 120 · /tune ctx 32768 · /tune reset");
            Dim("  thinking depth on Gemini/Antigravity is the model suffix: /model gemini-3.8-flash-low|medium|high");
            return;
        }
        var key = rest[0].ToLowerInvariant().Replace("-", "_");
        var val = rest.ElementAtOrDefault(1)?.Trim().ToLowerInvariant() ?? "";
        double D() => double.TryParse(val, System.Globalization.NumberStyles.Float, inv, out var d) ? d : throw new ArgumentException($"'{val}' is not a number");
        int I() => int.TryParse(val, out var i) ? i : throw new ArgumentException($"'{val}' is not a whole number");
        bool Off() => val is "off" or "false" or "0" or "no" or "none";
        switch (key)
        {
            case "temperature": case "temp": p.Temperature = val is "" or "default" or "reset" ? null : Math.Clamp(D(), 0, 2); break;
            case "top_p": case "topp": p.TopP = val is "" or "default" or "reset" ? null : Math.Clamp(D(), 0, 1); break;
            case "max_tokens": case "maxtokens": case "max": p.MaxTokens = val is "" or "default" or "reset" ? 0 : Math.Max(256, I()); break;
            case "thinking": case "reasoning": p.DisableThinking = Off(); break;
            case "timeout": p.RequestTimeoutMs = Math.Max(10, I()) * 1000; break;
            case "ctx": case "num_ctx": case "context": p.NumCtx = val is "" or "default" or "reset" ? 0 : Math.Max(1024, I()); break;
            case "reset": p.Temperature = null; p.TopP = null; p.MaxTokens = 0; p.DisableThinking = false; p.NumCtx = 0; break;
            default: Red("  unknown setting; /tune without arguments lists them"); return;
        }
        AgentConfig.Save(opts);
        Rebuild();
        Tune([]);
    }

    private void SetPersona(string? name)
    {
        _library = PromptLibrary.Load(_workspace);
        if (string.IsNullOrWhiteSpace(name)) { PrintPersonas(); return; }
        if (name is "off" or "default" or "none")
        {
            _personaName = null; _persona = null;
        }
        else
        {
            var p = _library.Persona(name);
            if (p is null) { Red($"  no persona '{name}'; /personas lists them, /init creates examples"); return; }
            _personaName = p.Name; _persona = p;
        }
        // The tools and the turn cap follow the persona; the conversation itself is kept.
        _tools = BuildTools(includeAgent: true, _persona);
        var keep = _loop.History.ToList();
        var maxTurns = _persona is { MaxTurns: > 0 } ? _persona.MaxTurns : _o.MaxTurns;
        _loop = new AgentLoop(new LayerClient(this), _tools, "", ConsoleEvents(depth: 0), _usage, maxTurns, "main", Guid.NewGuid().ToString("N"), keep, DeferredByDefault);
        Dim(_persona is null ? "  [persona off: default agent]" : $"  [persona: {_persona.Name} · tools: {(_persona.Tools.Length > 0 ? string.Join(", ", _persona.Tools) : "all")}]");
    }

    private void PrintPersonas()
    {
        _library = PromptLibrary.Load(_workspace);
        if (_library.Personas.Count == 0) { Dim("  (no personas; /init creates examples in .vanity-agent/personas, or put .md files in " + Path.Combine(AgentConfig.Dir, "personas") + ")"); return; }
        foreach (var p in _library.Personas)
        {
            var active = _persona is not null && p.Name.Equals(_persona.Name, StringComparison.OrdinalIgnoreCase);
            Console.ForegroundColor = active ? ConsoleColor.Green : ConsoleColor.White;
            Console.WriteLine($"  {(active ? "*" : " ")} {p.Name,-14} {p.Description}");
            Console.ResetColor();
            Dim($"      {Rel(p.Source)}" + (p.Tools.Length > 0 ? " · tools: " + string.Join(", ", p.Tools) : "") + (p.Skills.Length > 0 ? " · skills: " + string.Join(", ", p.Skills) : ""));
        }
        Dim("  /persona <name> switches, /persona off returns to the default agent.");
    }

    private void PrintSkills()
    {
        _library = PromptLibrary.Load(_workspace);
        if (_library.Skills.Count == 0) { Dim("  (no skills; /init creates examples in .vanity-agent/skills, or put .md files in " + Path.Combine(AgentConfig.Dir, "skills") + ")"); return; }
        var (active, _) = SkillsFor(_persona, includePinned: true);
        foreach (var s in _library.Skills)
        {
            var on = active.Contains(s);
            Console.ForegroundColor = on ? ConsoleColor.Green : ConsoleColor.White;
            Console.WriteLine($"  {(on ? "*" : " ")} {s.Name,-18} {s.Description}");
            Console.ResetColor();
            Dim($"      {Rel(s.Source)}" + (s.Always ? " · always" : _pinnedSkills.Contains(s.Name) ? " · pinned" : on ? " · from persona" : " · loadable on demand"));
        }
        Dim("  * = in the prompt every turn; the others are listed to the model and loaded with skill_view when needed. /skill <name> pins one.");
    }

    private void PinSkill(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { PrintSkills(); return; }
        _library = PromptLibrary.Load(_workspace);
        var s = _library.Skill(name);
        if (s is null) { Red($"  no skill '{name}'; /skills lists them"); return; }
        if (_pinnedSkills.Remove(s.Name)) Dim($"  [skill {s.Name} unpinned: loadable on demand]");
        else { _pinnedSkills.Add(s.Name); Dim($"  [skill {s.Name} pinned: in the prompt every turn]"); }
    }

    private string Rel(string path)
    {
        try
        {
            if (VanityPathHelper.IsInside(path, _workspace)) return Path.GetRelativePath(_workspace, path).Replace('\\', '/');
            if (VanityPathHelper.IsInside(path, AgentConfig.Dir)) return "~/.vanity-agent/" + Path.GetRelativePath(AgentConfig.Dir, path).Replace('\\', '/');
        }
        catch { }
        return path;
    }

    private async Task MemoryCommandAsync(string[] rest, CancellationToken ct)
    {
        var sub = rest.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "list";
        switch (sub)
        {
            case "add":
                var text = string.Join(' ', rest.Skip(1)).Trim();
                if (text.Length == 0) { Red("  /memory add <text>"); return; }
                var title = text.Length > 60 ? text[..57] + "..." : text;
                await _memory.SaveAsync(new MemoryEntry { AgentId = "operator", Content = text, Metadata = new() { ["title"] = title } }, ct);
                Dim("  [saved]"); break;
            case "delete": case "rm":
                if (!int.TryParse(rest.ElementAtOrDefault(1), out var n) || _memory.ByNumber(n) is not { } e) { Red("  /memory delete <number>"); return; }
                await _memory.DeleteAsync(e.Id, ct);
                Dim($"  [deleted #{n}]"); break;
            default:
                var notes = await _memory.ListAsync(ct);
                if (notes.Count == 0) { Dim("  (no notes yet; the agent saves facts with the memory tool, or /memory add <text>)"); return; }
                foreach (var note in notes)
                {
                    var t = note.Metadata.TryGetValue("title", out var tt) ? tt : note.Content.Split('\n')[0];
                    Console.WriteLine($"  #{_memory.NumberOf(note.Id),-3} {t}  ({note.CreatedAt:yyyy-MM-dd}, {note.AgentId})");
                    Dim("       " + FirstLine(note.Content, 160));
                }
                Dim("  file: " + _memory.Path); break;
        }
    }

    // ── console helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A line from the console that can be abandoned: the login's "paste the URL or wait for the browser"
    /// prompt is cancelled when the browser comes back first. A plain Console.ReadLine on a background task cannot
    /// be cancelled; it kept owning the keyboard and ate the operator's next message (the first "hi" after a Google
    /// login went nowhere). So the keyboard is only read once a key is actually pressed.</summary>
    private static async Task<string?> AskAsync(string prompt, CancellationToken ct)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return await Task.Run(() => Console.ReadLine(), ct);
        while (!ct.IsCancellationRequested)
        {
            bool key;
            try { key = Console.KeyAvailable; }
            catch (InvalidOperationException) { return await Task.Run(() => Console.ReadLine(), ct); }
            if (key) return Console.ReadLine();
            await Task.Delay(60, CancellationToken.None);
        }
        Console.WriteLine();
        return null;
    }

    // ── spinner: something is visible while the model is thinking ────────────────────────────────────────────────

    private static readonly object ConsoleGate = new();
    private Spinner? _spinner;

    private void SpinStart(string label)
    {
        if (Console.IsOutputRedirected) return;
        SpinStop();
        _spinner = new Spinner(label);
    }

    private void SpinStop()
    {
        var s = Interlocked.Exchange(ref _spinner, null);
        s?.Dispose();
    }

    private sealed class Spinner : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private readonly string _label;
        private const string Frames = @"-\|/";

        public Spinner(string label) { _label = label; _task = Task.Run(RunAsync); }

        private async Task RunAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int i = 0;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    lock (ConsoleGate)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"\r  {Frames[i++ % Frames.Length]} {_label} {sw.Elapsed.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}s ");
                        Console.ResetColor();
                    }
                    await Task.Delay(150, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _task.Wait(500); } catch { }
            lock (ConsoleGate) { Console.Write("\r" + new string(' ', _label.Length + 16) + "\r"); }
        }
    }

    private static async Task<string?> ReadSecretAsync(string prompt, CancellationToken ct)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return await Task.Run(() => Console.ReadLine(), ct);
        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); } continue; }
                if (k.KeyChar == '\0') continue;
                sb.Append(k.KeyChar); Console.Write('*');
            }
            return sb.ToString();
        }, ct);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            while (line.Length > width)
            {
                var cut = line.LastIndexOf(' ', width);
                if (cut < width / 2) cut = width;
                yield return line[..cut];
                line = line[cut..].TrimStart();
            }
            yield return line;
        }
    }

    private static string FirstLine(string s, int max = 100)
    {
        var line = (s ?? "").Replace("\r", "").Split('\n')[0].Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }

    private static void Dim(string s) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine(s); Console.ResetColor(); }
    private static void Cyan(string s) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(s); Console.ResetColor(); }
    private static void Green(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(s); Console.ResetColor(); }
    private static void Red(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(s); Console.ResetColor(); }
}
