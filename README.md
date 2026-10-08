# vanity-agent: open-source AI coding agent for your terminal

**One CLI agent for every model: ChatGPT, Claude, Gemini, Grok, DeepSeek, Ollama and any OpenAI-compatible API.**
Sign in with the subscriptions you already pay for or paste an API key, and get a terminal agent that
reads and edits files, runs commands, searches the web, remembers your project and delegates to sub-agents.
Single .NET 8 binary for Windows, Linux and macOS. No cloud account of ours, no telemetry, no vendor lock-in.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download)
[![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20Linux%20%7C%20macOS-informational)](#install)
[![Providers](https://img.shields.io/badge/providers-OpenAI%20%7C%20Anthropic%20%7C%20Gemini%20%7C%20Grok%20%7C%20Ollama-success)](#providers-and-sign-in)

```
$ vanity-agent
  vanity-agent 1.0.0 · C:\work\my-project
  profile: chatgpt (OpenAI/gpt-5.5) · signed in as you@example.com

You: the build fails with CS0246 in Program.cs, fix it and run the tests
  · The error names a missing namespace; I'll read the file, add the using, rebuild and run the tests.
  → bash  dotnet build -nologo
  ← bash  (4.2s · 1,207 chars)
  → edit_file  Program.cs
  ← edit_file  (0.0s · 48 chars)
  → bash  dotnet test -nologo
  ← bash  (11.8s · 640 chars)

Agent: The missing `using System.Text.Json;` was the cause. Build and 34 tests pass.
  [gpt-5.5 · 24.1s · tokens: 9,812 in / 402 out / 7,900 cached]
```

## Why another coding agent?

Every vendor ships a terminal agent that talks to its own models only: Codex CLI to OpenAI, Gemini CLI to Gemini,
Anthropic's to Claude. Each wants its own login, its own config, its own instructions file, and you learn three
tools to use three subscriptions.

vanity-agent is the one terminal agent that uses all of them, with **automatic failover**: when your ChatGPT plan
hits its limit, the next profile (Gemini, a Claude key, a local Ollama model) answers the same conversation. Same
tools, same skills, same project memory, whichever model is behind it.

| | vanityagent | Codex CLI | Gemini CLI | OpenCode | Aider |
|---|---|---|---|---|---|
| Models | OpenAI, Anthropic, Gemini, Grok, DeepSeek, Mistral, Groq, OpenRouter, Ollama, any OpenAI-compatible | OpenAI | Gemini | many | many (API keys) |
| Use a ChatGPT subscription | yes (device-code login) | yes | no | yes | no |
| Use a Gemini / Antigravity subscription | yes (Google login) | no | yes | partly | no |
| Failover across providers in one chat | yes | no | no | no | no |
| Local models (Ollama) | yes | no | no | yes | yes |
| Skills / playbooks | markdown, per project or global | AGENTS.md | yes | yes | no |
| Personas (roles with their own tools) | yes | no | no | agents | no |
| Image generation, browser screenshots, desktop control | yes | no | no | no | no |
| Smart project memory between sessions | yes, automatic | no | yes | partly | no |
| Live view of the model's reasoning | yes | no | no | yes | no |
| Runtime | one .NET 8 binary | Rust/Node | Node.js | Go/TS | Python |

The comparison describes the tools as published in 2026; check each project for its current features.

## Providers and sign-in

| Provider | API key | Subscription login |
|---|---|---|
| OpenAI (GPT-5.x) | `/key openai` | `/login openai`: ChatGPT device code, Codex backend |
| Anthropic (Claude) | `/key anthropic` | paste a bearer token with `/login anthropic` |
| Google Gemini | `/key gemini` | |
| Google Antigravity (Gemini 3.x, Claude via Cloud Code) | | `/login antigravity`: Google sign-in in the browser |
| Grok (xAI) | `/key grok` | `/login grok`: device code |
| DeepSeek, Mistral, Groq, OpenRouter, Perplexity, Together | `/key <provider>` | |
| Ollama (local, free) | `/key ollama` | |
| Any OpenAI-compatible endpoint | `/key custom` | |

Subscription logins use the public OAuth client ids of the providers' own CLIs; **no client secret ships in this
repository**. Whether a provider accepts a subscription token outside its own tools is the provider's decision and
subject to its terms; an API key is always the documented path. Tokens refresh themselves and are stored only on
your machine.

One exception needs a value from you: Google refuses to refresh the Antigravity login without that client's
installed-app secret (the one the Antigravity app itself carries). Store it once with `/set GoogleClientSecret`
(kept in `~/.vanity-agent/config.json`) or put it in `VANITY_AGENT_GOOGLE_SECRET`; without it the login works for
about an hour and then fails with "client_secret is missing".

Model settings per profile (`/tune`): temperature, top_p, max_tokens, thinking on/off, request timeout, Ollama
context size. On Gemini and Antigravity the thinking depth is part of the model name (`-low`, `-medium`, `-high`).

Every profile is a fallback for the others. If the first one fails (quota, outage, rate limit, retired model) the
router moves to the next profile and model, parks the failing pair for a while and comes back to it later.

## Install

Requirements: the [.NET 8 SDK](https://dotnet.microsoft.com/download) to build (no runtime needed by the people you
give the published binary to); on Windows, [Git for Windows](https://git-scm.com/download/win) for the `bash` tool.

```bash
git clone https://github.com/x5qubits/vanityagent.git
cd vanityagent
dotnet build -c Release
dotnet run -c Release          # first run opens the setup menu
```

Self-contained single-file builds:

```bash
dotnet publish -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o dist/win
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist/linux
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist/mac
```

Put `vanity-agent` (or `vanity-agent.exe`) on your PATH and run it in any project folder.

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
```

Or skip the menu: `vanity-agent --login openai`, or inside the chat `/key anthropic sk-ant-api03-... claude-sonnet-5-5`.
Add a second provider any time with `/login` or `/key`; `/use <name>` picks which one answers first.

## Use it

```bash
vanity-agent                                    # chat in the current directory
vanity-agent "explain the build setup"          # one answer, then exit
git diff | vanity-agent "review this diff"      # text on stdin is the prompt
vanity-agent -C ~/src/app -m gpt-5.5 "add a --json flag to the export command"
vanity-agent --persona reviewer "review the last commit"
vanity-agent --sandbox --max-turns 20 "list the TODO comments"
```

| Option | Meaning |
|---|---|
| `-C, --cwd <dir>` | work in that directory |
| `-P, --profile <name>` | use this profile first |
| `-m, --model <model>` | use this model on the active profile for this run |
| `--persona <name>` | run as a persona |
| `--skill <name>` | pin a skill for the session (repeatable) |
| `--max-turns <n>` | tool-call turns one request may take (default 60) |
| `--sandbox` | confine the file tools to the working directory |
| `--deny <path>` | a folder the agent must not touch (repeatable) |
| `--no-memory` | neither load nor save project notes (also turns the automatic analysis off) |
| `--login <provider>` | sign in and exit |
| `-v, --verbose` | echo the diagnostic log |

### Chat commands

| Command | What it does |
|---|---|
| `/login [openai\|grok\|antigravity\|anthropic] [name]` | sign in with a subscription, or paste an Anthropic key/token |
| `/key [provider] [key] [model]` | add a profile with an API key (interactive when arguments are missing) |
| `/profiles`, `/use <name>` | list profiles; make one the active one (the rest are fallbacks) |
| `/model <model>`, `/models` | set the model; list the models your credentials can use (asked from the provider) |
| `/tune [setting value]` | temperature, top_p, max_tokens, thinking on/off, timeout, ctx of the active profile |
| `/set <name> [value]` | a machine-local setting in config.json (e.g. `GoogleClientSecret`) |
| `/remove <name>` | delete a profile and its stored tokens |
| `/init`, `/project` | create the `.vanity-agent/` project directory; show where things live |
| `/personas`, `/persona <name\|off>` | list personas; switch persona (the conversation is kept) |
| `/skills`, `/skill <name>` | list skills; pin or unpin one for the session |
| `/memory [list\|add <text>\|delete <n>]` | the project's notes |
| `/usage` | what is left on each login (the provider's own weekly and five-hour limits, plan, balance) and what this project spent, by month; also `vanity-agent --usage` |
| `/reset`, `/cwd [dir]`, `/tools`, `/config` | housekeeping |
| `/sandbox on\|off`, `/verbose on\|off` | toggles |
| `/quit` | exit |

Ctrl+C stops the running task; at the prompt it exits.

## What the agent can do

| Area | Capability |
|---|---|
| Files | read any file (text with line numbers, images, PDFs, Jupyter notebooks), create files, exact-string edits in batches, several files in one call |
| Search | `grep` with ripgrep semantics (regex, globs, file types, context, counts) and `glob`, both newest-first and capped so results never flood the context |
| Shell | a persistent `bash` session (Git Bash on Windows): state, exports and the working directory persist; timeouts, output capping, child-process cleanup |
| Web | `web_fetch` turns a page into readable text with links and headings, downloads images and PDFs; `web_search` via a local headless Edge/Chrome, DuckDuckGo fallback; `page_view` screenshots any page or local HTML file and checks its layout |
| Images and desktop | `image_gen` makes pictures with your OpenAI, ChatGPT or Antigravity account; `computer` takes screenshots and drives the mouse and keyboard for GUI-only tasks (Windows) |
| Smart memory | after every turn that used tools, a background model call records what the steps proved (where things live, build and run commands and their results, what exists, the operator's stated preferences); each fact must cite a step, instructions and guesses are refused, duplicates skipped, contradicted automatic notes replaced, your own notes never touched. Notes are shown to the model on every turn, whole when few, filtered by relevance to the request when many |
| Sub-agents | `agent` runs independent sub-tasks in parallel in fresh contexts, optionally as a persona, and reports back |
| Skills and personas | markdown playbooks loaded on demand or pinned; roles with their own tools and turn budget |
| Planning | `task_scratchpad` keeps the working plan of a long task |
| Context control | tool outputs capped, superseded file reads dropped, old write arguments slimmed, long turns compacted, history kept within a token budget |
| Resilience | failover across profiles and models, rate-limit jails, silent-stall detection, context-overflow retry, OAuth refresh with persistence |
| Live view | the model's reasoning streams to the console as it thinks (providers that stream it); tool calls and results as they happen; time and tokens per turn |

### Tools the model gets

`bash`, `read_file`, `write_file`, `write_files`, `edit_file`, `grep`, `glob`, `web_fetch`, `web_search`,
`image_gen`, `page_view`, `computer`, `memory`, `agent`, `skill_view`, `task_scratchpad`, `date_time`, `list_tools`.

- `image_gen` generates a picture to a `.webp` at exact pixel size, or regenerates an existing one from a
  reference, through an OpenAI key or ChatGPT login, an Antigravity login, or an Alibaba key.
- `page_view` (Windows) renders a URL or a local HTML file in headless Edge/Chrome and returns the screenshot
  plus layout probes (horizontal overflow, clipped text, console errors), with JS injection, scrolling and clicks.
- `computer` (Windows) sees and controls the desktop: screenshots with the cursor marked, zoom, click, type,
  scroll, drag, find a control by its accessible name. For anything with no command-line path.

## Project directory, skills and personas

`/init` creates a `.vanity-agent/` folder in the working directory:

```
.vanity-agent/
  instructions.md      conventions of this project, in the system prompt on every turn
  personas/*.md        roles the agent can take (/persona <name>, --persona, or a sub-agent job's persona)
  skills/*.md          playbooks the model loads on demand with skill_view, or that a persona pins
  memory.json          the project's notes (kept here when the folder exists, so they travel with the project)
```

An `AGENTS.md` (or `VANITY.md`) in the working directory works as instructions too, with or without the folder,
so a project set up for Codex CLI is understood as is. The same `personas/` and `skills/` folders
under `~/.vanity-agent/` apply to every workspace; a project file with the same name overrides the global one.

A persona: frontmatter plus a body that replaces the agent's identity. The working rules and tool mechanics stay.

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

A skill: a playbook.

```markdown
---
name: code-review
description: How to review a diff or a pull request and how to report the findings.
always: false        # true = inlined on every turn for everyone
---
1. Get the change with `git diff` ...
```

Skills that are not pinned are listed to the model by name and description; it calls `skill_view` when a task
matches. The `agent` tool takes a `persona` per job, so the main agent can hand "review this" to the reviewer
persona in parallel with other work.

## FAQ

**Is vanity-agent free?** The agent is free and open source. You pay your model provider: a ChatGPT, Claude,
Gemini or Grok plan you already have, API usage, or nothing at all with Ollama on your own machine.

**Can I use my ChatGPT Plus/Pro subscription in the terminal?** Yes: `/login openai` signs in with a device
code, and the agent talks to the same Codex backend that Codex CLI uses.

**Does it work with Claude?** Yes, with an Anthropic API key (native Messages API with prompt caching) or a pasted
token. It also reaches Claude models through Google Antigravity's Cloud Code backend after `/login antigravity`.

**How is it different from the vendors' own terminal agents?** It does the same job (an agent in your terminal that
edits code, runs commands and tests, follows project instructions and skills) with any model and any provider,
switching between them automatically when one is out of quota or down.

**Can I run it fully offline / local?** With Ollama, yes: `/key ollama`, pick a model, no key, no network.

**Does it read my `AGENTS.md`?** `AGENTS.md`, `VANITY.md` and `.vanity-agent/instructions.md` are read
automatically; copy any other agent's instructions file to one of those names.

**Where are my keys stored?** In `~/.vanity-agent/config.json` on your machine only, created with user-only
permissions on Unix. Nothing leaves your machine except the requests to the provider you chose.

**Why is a turn slow?** Usually the provider's queue, not the agent: "pro"/"high" models can wait tens of seconds
before the first token. The status line shows the turn's wall time; `/models` lists faster models, `/model` picks one.

## Configuration

Everything lives in `%USERPROFILE%\.vanity-agent` (`$HOME/.vanity-agent` on Linux/macOS; `VANITY_AGENT_HOME`
overrides it):

```
config.json            the AI profiles: keys, OAuth tokens, models, base URLs
personas/, skills/     global personas and skills
projects/<name>-<id>/  memory.json and usage.json per workspace (when the workspace has no .vanity-agent/)
logs/                  vanity-agent.log and _shared/prompts/<date>.jsonl, one line per model call (pruned after 3 days)
scratch/               oversized tool outputs (pruned after 2 days)
```

`config.json` can be written by hand; see [config.example.json](config.example.json). Each profile:

| Field | Meaning |
|---|---|
| `Name`, `Provider` | the profile name and the provider id (`openai`, `anthropic`, `gemini`, `antigravity`, `grok`, `deepseek`, `mistral`, `groq`, `openrouter`, `perplexity`, `together`, `ollama`, `custom`) |
| `ApiKeys` | one or more keys, tried in turn; `["oauth"]` or empty means "use the stored login" |
| `Models` | models tried in order on this profile |
| `BaseUrl` | override the provider endpoint (Ollama, proxies, `custom`) |
| `Enabled` | `false` keeps the profile without using it |
| `MaxTokens`, `Temperature`, `TopP`, `NumCtx`, `RequestTimeoutMs`, `DisableThinking` | tuning |
| `OAuthProvider`, `OAuthAccessToken`, `OAuthRefreshToken`, `OAuthExpiresAt`, `OAuthAccountId` | written by `/login`; refreshed tokens are written back automatically |

Environment variables: `VANITY_AGENT_HOME` (state folder), `VANITY_AGENT_PROMPT_LOG=0` (no per-call prompt log),
`VANITY_BASH` (path to a bash executable), `VANITY_AGENT_GOOGLE_SECRET` / `_SECRET_ALT` / `_APP_SECRET` (only if
Google refuses the public PKCE exchange for your account).

## Safety

- No permission prompts: the agent runs the commands and edits the files it decides on, in the directory you start
  it in, as your user. Review its work as you would a colleague's. `--sandbox` confines the file tools to the
  working directory; `--deny` fences folders off.
- The system prompt tells the model to treat destructive actions (deleting, force-pushing, dropping data) as
  needing an explicit request, and to report failures rather than claim success. That is guidance, not a guard.
- Every model call is logged with its prompt and reply under `logs/_shared/prompts/`; the files can contain your
  project's content and are pruned after three days. `VANITY_AGENT_PROMPT_LOG=0` turns that log off.

## Visual Studio and VS Code

No extension needed: run the agent in the editor's terminal (View > Terminal in Visual Studio 2022, the integrated
terminal in VS Code) from the solution folder, with `vanity-agent` on your PATH. In Visual Studio you can also
register it under Tools > External Tools with `$(SolutionDir)` as the initial directory for a menu entry.

## How it is built

```
Program.cs                 entry
src/Host/ConsoleHost.cs    arguments, setup wizard, REPL, slash commands, live rendering
src/Host/ModelCatalog.cs   /models: ask the provider for its model list
src/Auth/OAuthFlows.cs     OpenAI device code, Grok device code, Antigravity (Google) PKCE loopback, Anthropic paste
src/Agent/AgentLoop.cs     the tool-calling loop: model call, parallel tool execution, budgets, history hygiene
src/Agent/SystemPrompt.cs  identity, rules, environment, project instructions, skills catalog, memory block
src/Agent/Personas.cs      persona and skill files, frontmatter, the project directory
src/Agent/HistoryLifecycleManager.cs   capping, compaction and token budget of the conversation
src/Llm/LlmRouter.cs       profile/model failover, cooldown jails, usage meter
src/Llm/OpenAiLlmClient.cs transports: OpenAI-compatible chat completions, Codex Responses, Google Cloud Code
                           (Antigravity), Gemini native, Anthropic Messages
src/Llm/OAuthTokenRefresher.cs  keeps subscription tokens fresh
src/Tools/                 the tools, the persistent shell and the process helpers
src/Memory/                the per-workspace note store
src/Infra/                 config and state folders, logging, path rules, usage tracking
```

The runtime (router, transports, tools, history management) comes from the VanityAgent project of
TheOrchestrator, trimmed to a single general agent; the console host, the terminal login flows, the Anthropic
transport, personas/skills and the configuration store are new.

## Troubleshooting

- **`bash` cannot start**: install Git for Windows, or set `VANITY_BASH` to a bash executable.
- **"All N LLM profile(s) failed"**: run `/profiles` and `/models`; the last error of each attempt is in
  `logs/vanity-agent.log`, the full exchange in `logs/_shared/prompts/`.
- **A login expired**: `/login <provider>` again; the profile keeps its models.
- **Google sign-in cannot reach this machine** (remote shell): paste the redirected URL from the browser's address
  bar into the prompt the login shows.

## Contributing

Issues and pull requests are welcome: new providers go in `ProviderBaseUrl`/`ProviderChatPath`
([src/Llm/OpenAiLlmClient.cs](src/Llm/OpenAiLlmClient.cs)) and `KeyProviders`
([src/Host/ConsoleHost.cs](src/Host/ConsoleHost.cs)); new tools implement `ITool` in [src/Tools](src/Tools).

## License

No license file is included yet; add one before relying on this project.
