# vanity-agent

A general-purpose command-line AI agent. It reads and edits files, runs shell commands, searches code, fetches
and searches the web, keeps notes between sessions and delegates sub-tasks to parallel sub-agents. It talks to
the model provider you choose, signed in with a subscription (OAuth) or an API key, with automatic failover
between the profiles you configure.

Single .NET 8 console application, no plugins, no PHP. One executable, one config file, optional markdown personas and skills.

```
$ vanity-agent
  vanity-agent 1.0.0 · C:\work\my-project
  profile: chatgpt (OpenAI/gpt-5.5) · signed in as user@example.com
  /help for commands · Ctrl+C stops the current task · /quit to exit

You: the build fails with CS0246 in Program.cs, fix it and run the tests
  → bash  dotnet build -nologo
  ← bash  (4.2s · 1,207 chars)
  → read_file  Program.cs
  ← read_file  (0.0s · 2,310 chars)
  → edit_file  Program.cs
  ← edit_file  (0.0s · 48 chars)
  → bash  dotnet test -nologo
  ← bash  (11.8s · 640 chars)

Agent: The missing `using System.Text.Json;` was the cause ...
```

## Capabilities

| Area | What the agent can do |
|---|---|
| Files | read any file (text with line numbers, images, PDFs, Jupyter notebooks), create files, exact-string edits with batch support, write several files in one call |
| Search | `grep` with ripgrep semantics (regex, globs, file types, context, counts) and `glob` file matching, both ordered newest first and capped so long results never flood the context |
| Shell | a persistent `bash` session (Git Bash on Windows): state, exports and the working directory persist between commands; timeouts, output capping, child-process cleanup |
| Web | `web_fetch` turns a page into readable text with links and headings, downloads images and PDFs; `web_search` uses a local headless Edge/Chrome on Google, falling back to DuckDuckGo |
| Memory | project notes saved per workspace between sessions: the agent searches, saves and deletes them; you edit them with `/memory` |
| Sub-agents | `agent` runs independent sub-tasks in parallel in fresh contexts with the same tools and reports back |
| Planning | `task_scratchpad` holds the working plan of a long task; `date_time`, `list_tools` |
| Context control | tool outputs are capped, superseded file reads are dropped, old write arguments are slimmed, long turns are compacted, the history stays within a token budget |
| Resilience | provider failover across profiles and models, rate-limit jails, silent-stall detection, context-overflow retry, OAuth token refresh with persistence |
| Project rules | an `AGENTS.md` or `.vanity-agent/instructions.md` in the working directory is loaded into the system prompt |
| Personas and skills | markdown roles and playbooks per project or global; switch roles, pin playbooks, or let the model load them on demand |
| Live view | the model's reasoning streams to the console as it thinks (providers that stream it), tool calls and results as they happen |

## Providers and authentication

| Provider | API key | Subscription login | Notes |
|---|---|---|---|
| OpenAI | `/key openai` | `/login openai` (ChatGPT device code, Codex backend) | |
| Anthropic | `/key anthropic` or `/login anthropic` | paste a bearer token (`sk-ant-oat...`) | native Messages API with prompt caching |
| Google Gemini | `/key gemini` | | OpenAI-compatible endpoint |
| Google Antigravity | | `/login antigravity` (Google sign-in in the browser) | Cloud Code backend, Gemini 3.x and the Claude models it serves |
| Grok (xAI) | `/key grok` | `/login grok` (device code) | |
| DeepSeek, Mistral, Groq, OpenRouter, Perplexity, Together | `/key <provider>` | | OpenAI-compatible |
| Ollama | `/key ollama` | | local, no key |
| Anything OpenAI-compatible | `/key custom` | | base URL + key |

Subscription logins use the public OAuth client ids of the providers' own command-line tools; no client secret
is shipped in this repository. The Google (Antigravity) exchange and refresh use the public PKCE form; if Google
refuses it for your account, set `VANITY_AGENT_GOOGLE_SECRET` (and `VANITY_AGENT_GOOGLE_SECRET_ALT`, or
`VANITY_AGENT_GOOGLE_APP_SECRET` for the desktop-app client) in your environment with the installed-app secret.
Whether a provider accepts a subscription token outside its own tools is the provider's decision and subject to
its terms; an API key is always the documented path. Anthropic has no browser login here: paste a key or a token.

Every profile is a fallback for the others: if the first one fails (quota, outage, rate limit, dead model) the
router moves to the next profile and model, parks the failing pair for a while, and comes back to it later.

## Install

Requirements: the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer) to build; on Windows,
[Git for Windows](https://git-scm.com/download/win) for the `bash` tool.

```bash
git clone <this repo>
cd VanityAgent
dotnet build -c Release
# run it
dotnet run -c Release -- --help
```

A self-contained single file, no runtime needed on the target machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist
```

Put `dist/vanity-agent` (or `vanity-agent.exe`) on your PATH.

## First run

```
$ vanity-agent
  Welcome to vanity-agent. No AI provider is configured yet.
  How do you want to connect?
    1  OpenAI       - sign in with ChatGPT (subscription, device code)
    2  OpenAI       - API key
    3  Anthropic    - API key or token
    4  Google       - Antigravity sign in (subscription, browser)
    5  Gemini       - API key
    6  Grok (xAI)   - sign in (subscription, device code)
    7  Grok (xAI)   - API key
    8  DeepSeek / Mistral / Groq / OpenRouter / other OpenAI-compatible - API key
    9  Ollama       - local, no key
  Choice:
```

Or without the menu: `vanity-agent --login openai`, or inside the chat `/key anthropic sk-ant-api03-... claude-sonnet-5-5`.

## Use

```bash
vanity-agent                          # chat in the current directory
vanity-agent "explain the build setup"   # one answer, then exit
git diff | vanity-agent "review this diff"   # text on stdin is the prompt
vanity-agent -C ~/src/app --model gpt-5.5 "add a --json flag to the export command"
vanity-agent --sandbox --max-turns 20 "list the TODO comments"
```

| Option | Meaning |
|---|---|
| `-C, --cwd <dir>` | work in that directory |
| `-P, --profile <name>` | use this profile first |
| `-m, --model <model>` | use this model on the active profile for this run |
| `--max-turns <n>` | tool-call turns one request may take (default 60) |
| `--sandbox` | confine the file tools to the working directory |
| `--deny <path>` | a folder the agent must not touch (repeatable) |
| `--no-memory` | neither load nor save project notes |
| `--login <provider>` | sign in and exit |
| `--persona <name>` | run as a persona |
| `--skill <name>` | pin a skill for the session (repeatable) |
| `-v, --verbose` | echo the diagnostic log |

### Chat commands

| Command | What it does |
|---|---|
| `/login [openai\|grok\|antigravity\|anthropic] [name]` | sign in with a subscription, or paste an Anthropic key/token |
| `/key [provider] [key] [model]` | add a profile with an API key (interactive when arguments are missing) |
| `/profiles` | list profiles; the first usable one answers, the rest are fallbacks |
| `/use <name>` | make a profile the active one |
| `/model <model>` | set the model of the active profile |
| `/models` | list the models the active credentials can use, asked from the provider |
| `/remove <name>` | delete a profile and its stored tokens |
| `/reset` | clear the conversation |
| `/usage` | token usage of this process |
| `/cwd [dir]` | show or change the working directory |
| `/tools` | list the tools |
| `/memory [list\|add <text>\|delete <n>]` | the project's notes |
| `/init`, `/project` | create the `.vanity-agent/` project directory; show where it and the global folders are |
| `/personas`, `/persona <name\|off>` | list personas; switch persona |
| `/skills`, `/skill <name>` | list skills; pin or unpin one for the session |
| `/sandbox on\|off`, `/verbose on\|off` | toggles |
| `/config` | where the config and the project state live |
| `/quit` | exit |

Ctrl+C stops the running task; at the prompt it exits.

## Tools the model gets

| Tool | Purpose |
|---|---|
| `bash` | run a command in the persistent shell (`timeout_ms` up to 10 minutes; long output is saved to a file and previewed) |
| `read_file` | a file with line numbers, a range, several files in one call; images are shown to the model; PDFs by page range; notebooks cell by cell |
| `write_file`, `write_files` | create or overwrite one or several files |
| `edit_file` | exact-string replacement, `replace_all`, several edits in one call, with a precise hint when the text is not found |
| `grep` | regex content search with glob/type filters, context lines, three output modes |
| `glob` | find files by pattern, newest first |
| `web_fetch` | a page as readable text, links only, image/PDF download, full text to a file |
| `web_search` | titles, URLs and snippets |
| `memory` | search / list / save / delete project notes |
| `agent` | run sub-tasks in parallel in fresh sub-agents, optionally as a persona |
| `skill_view` | load a skill's playbook by name |
| `task_scratchpad` | in-task working notes |
| `date_time`, `list_tools` | |

## Project directory, instructions, personas and skills

`/init` creates a `.vanity-agent/` folder in the working directory:

```
.vanity-agent/
  instructions.md      conventions of this project, in the system prompt on every turn
  personas/*.md        roles the agent can take (/persona <name>, --persona, or a sub-agent job's persona)
  skills/*.md          playbooks the model loads on demand with skill_view, or that a persona pins
  memory.json          the project's notes (kept here when the folder exists, so they travel with the project)
```

An `AGENTS.md` (or `VANITY.md`) in the working directory works as instructions too, with or without the folder.
The same `personas/` and `skills/` folders under `~/.vanity-agent/` apply to every workspace; a project file with
the same name overrides the global one. Files are re-read on every turn, so edits apply at once.

A persona is a markdown file with YAML frontmatter; the body replaces the agent's identity, the working rules and
tool mechanics stay:

```markdown
---
name: reviewer
description: Reviews changes for correctness and risk, never edits files.
tools: [read_file, grep, glob, bash, skill_view]   # optional: the only tools it may use
skills: [code-review]                              # optional: playbooks inlined on every turn
max_turns: 40                                      # optional
---
You are a meticulous code reviewer. You read the change and the code around it ...
```

A skill is a playbook:

```markdown
---
name: code-review
description: How to review a diff or a pull request and how to report the findings.
always: false        # true = inlined on every turn for everyone
---
1. Get the change with `git diff` ...
```

Skills that are not pinned are listed to the model by name and description under "Skills you may load"; it calls
`skill_view` with the name when a task matches. `/skills` shows which are active, `/skill <name>` pins or unpins
one for the session, `/personas` and `/persona <name>` switch roles (the conversation is kept), `/persona off`
returns to the default agent. The `agent` tool takes a `persona` per job, so the main agent can delegate "review
this" to the reviewer persona in parallel with other work.

## Visual Studio and VS Code

No extension or marketplace listing is needed: run the agent in the editor's terminal (View > Terminal in Visual
Studio 2022, the integrated terminal in VS Code) from the solution folder, with `vanity-agent` on your PATH. In
Visual Studio you can also register it under Tools > External Tools with `$(SolutionDir)` as the initial
directory to get a menu entry. To share the agent with others, publish the self-contained builds from the Install
section as a GitHub release.

## Memory

Facts the agent learns (paths, commands that work, decisions) are saved as notes per workspace under the agent
home and shown to the model at the start of every turn. A note on a subject that is already stored replaces the
earlier note. `/memory` lists, adds and deletes them; `--no-memory` turns the feature off.

## Configuration

Everything lives in `%USERPROFILE%\.vanity-agent` (`$HOME/.vanity-agent` on Linux/macOS; `VANITY_AGENT_HOME`
overrides it):

```
config.json            the AI profiles: keys, OAuth tokens, models, base URLs
projects/<name>-<id>/  memory.json and usage.json per workspace
logs/                  vanity-agent.log and _shared/prompts/<date>.jsonl, one line per model call (pruned after 3 days)
scratch/               oversized tool outputs (pruned after 2 days)
```

`config.json` can also be written by hand; see [config.example.json](config.example.json). Each profile:

| Field | Meaning |
|---|---|
| `Name`, `Provider` | the profile name and the provider id (`openai`, `anthropic`, `gemini`, `antigravity`, `grok`, `deepseek`, `mistral`, `groq`, `openrouter`, `perplexity`, `together`, `ollama`, `custom`) |
| `ApiKeys` | one or more keys, tried in turn; `["oauth"]` or empty means "use the stored login" |
| `Models` | models tried in order on this profile |
| `BaseUrl` | override the provider endpoint (Ollama, proxies, `custom`) |
| `Enabled` | `false` keeps the profile without using it |
| `MaxTokens`, `Temperature`, `TopP`, `NumCtx`, `RequestTimeoutMs`, `DisableThinking` | tuning |
| `OAuthProvider`, `OAuthAccessToken`, `OAuthRefreshToken`, `OAuthExpiresAt`, `OAuthAccountId` | written by `/login`; refreshed tokens are written back automatically |

Keys and tokens are stored in plain text in that file; it is created with user-only permissions on Unix. Do not
commit it.

## Safety

- No permission prompts: the agent runs the commands and edits the files it decides on, in the directory you
  start it in, as your user. Review its work as you would a colleague's. Use `--sandbox` to confine the file
  tools to the working directory and `--deny` for folders it must not touch.
- The system prompt tells the model to treat destructive actions (deleting, force-pushing, dropping data) as
  needing an explicit request, and to report failures rather than claim success. That is guidance, not a guard.
- Every model call is logged with its prompt and reply under `logs/_shared/prompts/`; the files can contain your
  project's content and are pruned after three days. Set `VANITY_AGENT_PROMPT_LOG=0` to turn that log off.

## How it is built

```
Program.cs                 entry
src/Host/ConsoleHost.cs    arguments, setup wizard, REPL, slash commands, rendering
src/Host/ModelCatalog.cs   /models: ask the provider for its model list
src/Auth/OAuthFlows.cs     OpenAI device code, Grok device code, Antigravity (Google) PKCE loopback, Anthropic paste
src/Agent/AgentLoop.cs     the tool-calling loop: model call, parallel tool execution, budgets, history hygiene
src/Agent/SystemPrompt.cs  identity, rules, environment block, project instructions, memory block
src/Agent/HistoryLifecycleManager.cs   capping, compaction and token budget of the conversation
src/Llm/LlmRouter.cs       profile/model failover, cooldown jails, usage meter
src/Llm/OpenAiLlmClient.cs transports: OpenAI-compatible chat completions, Codex Responses, Google Cloud Code
                           (Antigravity), Gemini native, Anthropic Messages
src/Llm/OAuthTokenRefresher.cs  keeps subscription tokens fresh
src/Tools/                 the tools listed above, the persistent shell and the process helpers
src/Memory/                the per-workspace note store
src/Infra/                 config and state folders, logging, path rules, usage tracking
```

The runtime (router, transports, tools, history management) is lifted from the VanityAgent project of
TheOrchestrator and trimmed to a single general agent; the console host, the login flows in the terminal, the
Anthropic transport and the configuration store are new.

## Troubleshooting

- **`bash` says it cannot start**: install Git for Windows, or set `VANITY_BASH` to the path of a bash executable.
- **"All N LLM profile(s) failed"**: run `/profiles` and `/models`; the last error of each attempt is in
  `logs/vanity-agent.log`, and the full exchange in `logs/_shared/prompts/`.
- **OAuth login expired**: `/login <provider>` again; the profile keeps its models.
- **The model keeps reading the same files**: give it a smaller task, or raise `--max-turns`; the history manager
  drops superseded reads automatically, the per-turn limit is what stops a run.
- **Google sign-in cannot reach this machine** (remote shell): paste the redirected URL from the browser's address
  bar into the prompt the login shows.

## License

No license file is included yet; add one before publishing.
