# CLI and Command Reference

Complete reference for mux-swarm CLI flags and interactive slash commands (v0.12.1).

## CLI Flags

Launch flags accepted by the `mux-swarm` binary. Any of these can be persisted across launches with `/startargs <args>` (stored in `config.startupArgs`; clear with `/startargs clear`).

| Flag | Description |
|---|---|
| `--help` / `-h` | Print help and exit |
| `--goal <text\|file>` | Explicit goal (also accepted as a bare positional argument) |
| `--goal-id <id>` | Attach a persistent goal/session identifier |
| `--continuous` | Continuous autonomous mode |
| `--parallel` | Parallel swarm (concurrent batch dispatch) |
| `--max-parallelism <n>` | Max concurrent agent tasks (default 4) |
| `--prod` | Prod mode (orchestrator `[[MARKER]]` output) |
| `--stdio` | Machine-readable NDJSON output, no ANSI; suppresses hook prompts |
| `--acp` | Zed Agent Client Protocol server over stdio (JSON-RPC) |
| `--delimiter <str>` | Set the multi-line input delimiter |
| `--model <id>` | CLI model override |
| `--min-delay <secs>` | Minimum delay between continuous loops (default 300) |
| `--persist-interval <s>` | Persist session state every N seconds |
| `--session-retention <n>` | Keep the last N sessions (default 10) |
| `--watchdog` | External watchdog (auto-restart on crash) |
| `--mcp-strict <bool>` | Require all MCP servers to connect (default true) |
| `--docker-exec <bool>` | Route execution through Docker skills |
| `--sandbox [backend]` | Startup sandbox backend override (default argument `docker`); validated and synced to config |
| `--agent <name>` | Pick an agent and boot into a single-agent session (or the agent for a goal/machine run) |
| `--agent-mode` | Boot straight into a single-agent session (pair with `--agent`) |
| `--plan` | Plan mode (approve before executing) |
| `--ultra` / `--ultraplan` | Max-reasoning mode (plan + auto sub-agents per config) |
| `--giga` | Dynamic team/workflow orchestration (parity with `/giga`) |
| `--sub` / `--subagents` | Enable sub-agent delegation |
| `--psub` / `--parasubagents` | Enable parallel sub-agent delegation |
| `--verbose` | Verbose MCP/init logging |
| `--swarm` / `--pswarm` / `--stateless` / `--teams` | Boot straight into that mode |
| `--classic` / `--tui` | Force render mode (classic line renderer vs live TUI) |
| `--clear` | Clear the console at startup |
| `--report [session-id]` | Generate audit report(s) and exit (no id = all sessions) |
| `--provider <name>` | Set the active LLM provider on launch |
| `--cfg <path>` | Override the Config.json path (scoped instance) |
| `--swarmcfg <path>` | Override the Swarm.json path (scoped instance) |
| `--workspace <path>` / `--ws` | Set the @-file workspace root |
| `--workflow <file>` / `--wf` | Load and run a workflow file |
| `--serve [port]` | Embedded web UI (default 6723) |
| `--daemon` | Daemon mode (file watch, cron, status, and webhook triggers from config.json) |
| `--update` | Self-update from the latest GitHub release, then exit |
| `--register` / `--remove` | Register/unregister mux-swarm as an OS service |
| `--relaunch-after` | Internal: post-update re-exec handshake (not user-facing) |

### Goal-Driven Execution

```bash
mux-swarm "<goal>"
mux-swarm <goal.txt>
mux-swarm --goal "<goal>"
mux-swarm --goal <goal.txt>
```

### Single-Agent via CLI

```bash
mux-swarm --agent CodeAgent --goal "<goal>"
mux-swarm --agent WebAgent --goal task.txt --continuous --goal-id overnight --min-delay 600
```

### Continuous Mode

```bash
mux-swarm --continuous --goal "<goal>" --goal-id my-run
mux-swarm --continuous --goal task.txt --goal-id overnight --min-delay 600
```

### Parallel Mode

```bash
mux-swarm --parallel --goal "<goal>"
mux-swarm --parallel --continuous --goal "<goal>" --goal-id batch-run
mux-swarm --parallel --max-parallelism 6 --goal task.txt
```

Parallel mode decomposes a goal into independent subtasks and dispatches them concurrently across agents. Use `--max-parallelism` to cap simultaneous agent tasks (default 4). Combines with `--continuous` for recurring parallel batch runs.

## Interactive Commands

Type `/help` at any time for the built-in reference, or `/` in the live TUI for a fuzzy command palette. Commands are scoped: some only work inside a live session, some only at the top-level REPL, and a few work in both.

### Session-native commands

Available inside a live single-agent session.

| Command | Description |
|---|---|
| `/compact [steering]` | Compact live session context; optional steering text guides the summary |
| `/handoff [steering\|path.md]` | Write a cold-resume handoff doc via the active model |
| `/heal [deep] [steering]` / `/reflect` | Review the session for lessons; propose BRAIN/MEMORY (and SKILL) self-heal write-backs |
| `/fix [what is wrong]` | Diagnose and propose repairs for a misbehaving Mux subsystem |
| `/diff` | Working-tree git diff (collapsible) |
| `/doctor` | Health check: providers, MCP, sandbox, proxy (no model call) |
| `/cost` (+ `/cost all`) | Token usage + estimated cost; `all` = per-model matrixed breakdown |
| `/tokens` / `/context` (+ `/tokens all`) | Context/token usage; `all` is an alias of `/cost all` |
| `/init` | Analyze the workspace and scaffold AGENTS.md |
| `/review` | AI review of the working-tree diff (read-only) |
| `/wipe` | Clear session history, keep the session |
| `/undo` | Drop the last exchange |
| `/retry` / `/redo` | Re-run the last turn |
| `/effort` | Cycle `low → med → high → xhigh → max → low` (also Shift+Tab) |
| `/effort <tier>` | Select `low`, `med`/`medium`, `high`, `xhigh`, or `max`; `xhigh` and `max` are distinct |
| `/max` | Shortcut for `/effort max`; sends the literal provider `max` value |
| `/effort custom <raw-value>` | Send a provider-specific effort string; preserve casing/interior spaces, trim surrounding whitespace, reject control characters |
| `/tag <text>` | Tag the live session for resume/search |
| `/kanban` (+ add/assign/block/ready/move/remove/peer) | Editable team task board |
| `/background` / `/bg` (+ jobs/cancel) | Run an agent goal in the background; watch via `\` |
| `/detach` | Park the session in the background; re-enter with `/attach` |
| `/voice [auto\|off\|vol <1-10>]` | Local speech-to-text dictation into the compose field (TUI only) |
| `/unhide <agent>` | Restore a hidden sub-agent to the viewport (hide via the `\` Agent View `h` key; `/background` lanes start hidden) |
| `/qc` / `/qm` | Quit the session loop |
| `!<command>` | Run a shell command and add its output to context |

### Both scopes (process-level)

Work inside a session and at the top-level REPL.

| Command | Description |
|---|---|
| `/daemon` / `/da` (on\|off\|jobs\|cron\|watch\|cancel) | Runtime daemon control; bare `cron`/`watch` opens an interactive builder (plain-English cron accepted) |
| `/update` | Self-update from the latest GitHub release (hash-verified; restarts if the binary changed) |

### REPL-only commands

Available at the top-level REPL.

**Mode launch**

| Command | Description |
|---|---|
| `/swarm` | Launch the multi-agent orchestrated swarm loop |
| `/pswarm` | Launch the parallel swarm (concurrent batch dispatch) |
| `/agent` | Launch an interactive single-agent loop |
| `/stateless` | Stateless single-agent loop for one-off tasks |
| `/subagents` (`/sub`) | Enable ephemeral sub-agent delegation inside a single-agent loop |
| `/parasubagents` (`/psub`) | Enable parallel ephemeral sub-agent delegation |
| `/workflow <file>` | Run a deterministic workflow from a JSON file |
| `/teams [name]` | List and launch named teams from swarm.json |
| `/createteam` | Guided wizard to define a team (lead, members, coordination, parallelism) |
| `/createhook [id]` | Guided wizard: scaffold a hook, an outbound webhook, or an inbound webhook |
| `/hooks (on\|off\|create)` | Hooks status / toggle / create |
| `/onboard` | Create or update your operator profile (BRAIN.md + MEMORY.md) |

**Toggles and configuration**

| Command | Description |
|---|---|
| `/plan` | Toggle plan mode (agents present a plan and ask for approval before executing) |
| `/ultra` (`/ultraplan`) | Interactive deep-reasoning mode inside the single-agent loop: plan + maximum reasoning budget + heavy sub-agent delegation |
| `/giga` | Interactive Giga mode: ultra plus the agent can spawn named teams and author/run workflows on the fly |
| `/continuous` (`/cont`) | Toggle continued autonomous execution |
| `/addcontext` | Configure what context each agent is injected with |
| `/maxp` | Max agents running in parallel (default 4) |
| `/setmodel` | Browse active-provider models and save a slot’s model and reasoning effort |
| `/set <key> <value>` | Edit any config.json or swarm.json key by dotted path (bare `/set` opens a picker) |
| `/showreasoning full\|summary\|none` | Show or hide streamed reasoning text |
| `/config` | Show all configuration settings; every key is `/set`-editable |
| `/newagent` | Guided wizard to create a swarm agent |
| `/editagent` | Edit a swarm agent (model, description, MCP servers, delegation) |
| `/delagent` | Remove a swarm agent from swarm.json (and optionally its prompt file) |
| `/swap` | Fuzzy-search and choose the agent for subsequent single-agent runs |
| `/verbose` | Toggle TUI tool output between compact and full panels |
| `/subagentview` (`/sav`) | Toggle collapsed/expanded delegated sub-agent output |
| `/daemonview` (`/dv`) | Toggle the daemon output view |
| `/dockerexec` | Toggle Docker execution mode |
| `/sandbox` | View or switch the execution sandbox backend (host/docker/podman/gvisor/kata/...) |
| `/login` | Sign in to a subscription provider via the CLIProxy sidecar OAuth flow |
| `/ping` | Check sidecar + provider login readiness |
| `/proxy status\|update\|restart` | Manage the bundled CLIProxyAPI sidecar |
| `/delimiter` | Toggle the multi-line input delimiter |

**Utilities**

| Command | Description |
|---|---|
| `/classic` | Switch to the classic line-by-line renderer |
| `/tui` | Switch to the live full-screen TUI renderer |
| `/resume` | Resume a previous single-agent session (shows #tags) |
| `/attach [id]` | Re-attach a detached session |
| `/model` | View current model assignments |
| `/provider` | View or switch the active LLM provider |
| `/workspace [path]` | Set or view the @-file workspace root |
| `/limits` | Display current execution limits for orchestration and agents |
| `/tools [query]` | Fuzzy-find and inspect tools in the current scope, grouped by native/MCP/runtime source |
| `/skills` / `/skill` | List available local skills / inspect one |
| `/memory [deep\|standard\|show\|set <k> <v>]` | Toggle deep memory + status and tuning |
| `/deep [off]` | Shortcut to enable (or disable) deep memory mode |
| `/taskgraph on\|off\|status` | Auto task decomposition onto the task board (config block is `decompose`) |
| `/theme [default\|dark\|light\|mono\|solarized\|dracula\|gruvbox]` | Switch the TUI theme |
| `/sessions` | List all saved sessions with type and agent count |
| `/setup` | Run initial setup / reconfigure |
| `/reloadskills` | Refresh the skills directory for mid-process changes |
| `/installskill <name\|owner/repo\|owner/repo/path\|URL>` | Install a skill from the curated registry or GitHub |
| `/refresh` | Full system refresh: config, MCP servers, and skills |
| `/report [id]` | Generate full session audit report(s) |
| `/clear` | Clear the terminal |
| `/status` | View current system status: provider, models, tools, skills, and sessions |
| `/help` | Full command reference |
| `/shortcuts` (`/keys`) | Show keyboard shortcuts |
| `/exit` | Exit the runtime |
| `/startargs` | Persist CLI flags across launches (`/startargs clear` to reset) |
| `/dbg` / `/nodbg` | Enable/disable tool-call output (stdio mode only) |
| `/disabletools` | Disable tool availability |

---
[Back to docs index](README.md) | [Main README](../README.md)


### Explicit reasoning effort

`/effort xhigh` uses the existing extra-high tier. `/effort max` (or `/max`) requests the
separate provider `max` tier; it is not an alias for `xhigh`. Max and custom selections use
OpenAI-compatible native request options and surface provider rejections without silently
retrying at a lower effort. The footer shows `max` or `custom <raw-value>` as selected.
The next Shift+Tab or bare `/effort` after a custom selection returns to `low`.

Selections affect the next model request in the active single-agent session; commands do not
rewrite Swarm.json. To configure a startup selection, set `modelOpts.reasoning.effort` to
`"max"` or `"custom <raw-value>"`. Ultra/giga retain their existing xhigh + numeric-budget
default, but do not overwrite an explicit max/custom setting.


## Clipboard screenshots and paste cards (TUI)

- Paste normally for text. In the draft, **Ctrl+V** (when forwarded by the terminal), **Alt+V**, or **`/paste` + Enter** reads clipboard text or a screenshot. Pasting does not send a model request.
- Images are saved with unique names under the configured sandbox's **`captures/`** directory. The draft shows an image card; submitting includes its absolute file path, not an automatic vision payload. Detaching a card does not delete its saved image. Captures are not automatically cleaned up.
- Recognized PNG/JPEG/GIF/WebP/BMP image paths inside allowed directories can also be pasted. Images are limited to 16 MiB; extension follows detected format. An unavailable or disallowed file path remains ordinary text.
- Text pastes with **at least 10 newline separators or more than 1,000 characters** collapse to compact numbered `[≡ N]` chips; images use `[▧ N]`. Full text remains in the draft for submission and history; code indentation is not rewritten. Short text pastes remain ordinary editable text.
- **F2** focuses cards. **Up/Down or Left/Right** selects; **Enter** opens/closes a preview; **Up/Down, Home/End, or mouse wheel** scrolls its contents. **PageUp/PageDown** stays with the outer viewport (Mux transcript in frame mode; host-terminal bindings in inline mode), without dismissing the preview. **Left/Right** changes cards while previewing. **Delete/Backspace** detaches the selected item; **Esc** closes the preview, then returns to editing. Ordinary typing leaves the preview and edits the draft. Deleting a folded item from the edit line removes its whole range.
- **Ctrl+Z** restores the last attachment insertion/removal if no subsequent text edit intervened; this is attachment undo, not a full text-editor undo stack.
- Pending paste is serialized before later typing/Enter. **Esc** cancels that pending paste without clearing the existing draft. An unsuccessful save never inserts a nonexistent capture path.

**Portability:** native Windows registered-PNG clipboard reads have a bounded PowerShell STA bitmap fallback. macOS uses built-in osascript/AppKit; Linux uses available `wl-paste` (Wayland) or `xclip` (X11), and WSL can use the Windows PowerShell bridge. No clipboard utilities are installed automatically. Direct clipboard access needs a local desktop session.

Both docked TUI renderers (frame and inline) use the same input pump and paste transaction path. On Windows that input owner requests **VT input** as well as bracketed-paste output negotiation. CSI/SS3 navigation, function keys, and mouse reports are decoded before editor dispatch; older consoles can retain the native-record fallback. Recognized paste frames preserve embedded newlines and tabs as payload, and async clipboard work is bound to its initiating draft.

For unframed compose input in either docked renderer, a bounded capture-layer compatibility classifier stages likely batched text rather than immediately executing embedded Enter keys. **An inferred paste shows “F4 sends (or Ctrl+Enter)”: plain Enter adds a newline; F4 or a distinguishable Ctrl+Enter explicitly sends.** This guard is used only for uncertain capture; normal framed/clipboard paste retains ordinary Enter submission. Fast typing can be misclassified and sufficiently slow unframed paste can evade inference—this is mitigation, not perfect provenance. Prefer framed or explicit clipboard paste when available.

The classifier uses a short candidate window, a separate paste-idle window, and bounded chunks; none of these timers submits a draft. Application Ctrl+V/Alt+V works only when the terminal forwards the chord. Mux does not globally intercept keys or monitor clipboard contents to guess completion. Incomplete recognized paste bodies recover after a separate two-second inactivity watchdog as guarded text, never replayed commands; abandoned prefixes are discarded. Native Ctrl+C can cancel pending capture, but raw control bytes inside framed payload remain literal. Bare Escape retains its separate ambiguity window. Inline keeps primary-screen/native scrollback presentation and does not enable terminal mouse reporting; its Windows acquisition preserves the saved QuickEdit setting. The non-docked/non-TUI fallback remains separate.

Both docked TUI renderers negotiate **OSC 5522** with capable terminals; supported terminal-originated image pastes can work over SSH. Plain bracketed paste is text-only. If the terminal consumes a paste gesture and sends no event, use the alternate key or `/paste`. In SSH sessions without enhanced paste, upload the image and paste a path readable by Mux; Mux does not inspect an unrelated remote desktop clipboard. Clipboard images are not accepted by ask-user modals or transcript viewers.


## Submitted input and agent headers

Submitted user text keeps the same two-column body alignment on explicit multiline and
soft-wrapped continuation rows. Literal markup and indentation stay readable; compact pasted
text/image labels remain display-only and do not change the submitted payload.

User echoes and agent-name rules are retained as width-aware layouts. Frame history, inline
resize/Ctrl+L redraw, and opening NAV rebuild them at the current width rather than wrapping an
old full-width rule. Long agent names are clipped for display so the name and rule stay on one
row. Inline history outside the repainted viewport remains terminal-owned.

## Frame transcript scrollbar

The frame renderer reserves the rightmost terminal column for a passive scrollbar. A muted
track and solid proportional thumb show the viewport's position in retained transcript history;
the thumb has a two-cell minimum where space permits. The rail appears whenever history
exceeds the transcript pane, including at the live tail, and stops above the pinned live/footer area.

The footer divider stays a plain horizontal rule; scroll position is conveyed by the rail alone.
**PgUp/PgDn** scroll the transcript; after user-initiated scrolling, **End/Esc** returns to
latest when no higher-priority editor or attachment action consumes the key. The scrollbar does
not add click/drag targets or change mouse bindings. Inline rendering keeps host-terminal scrollback.

The startup title card uses shared bounded column sizing in both render paths. Frame-mode resize
rebuilds its retained layout at the current width rather than wrapping old panel borders.


## Swarm completion display (TUI)

Swarm and pswarm show one compact completion row instead of a completion summary followed by
another generic success acknowledgment. Redundant end-of-turn spacing and the per-goal separator
are omitted because the docked footer already provides the boundary. Short summaries remain readable inline; long or multiline
summaries show an expansion hint and retain their complete text for the existing **Ctrl+E** / **Ctrl+G**
views. A TUI without a docked driver prints the full summary because it has no retained expansion view.

Completion status does not change the current agent's lane color. Failure/partial messages,
artifact paths, unique summaries, and the per-goal token report are preserved. Classic and stdio
completion output/events are unchanged.

Both modes populate the existing footer with the orchestrator model and goal duration. At goal end,
the footer shows cumulative goal token usage, **not a context-window percentage**. Token accounting
itself is unchanged, and no context threshold or aggregate sys/tool breakdown is inferred.


## Local context pruning (`/prune`)

`/prune` is the quick, **deterministic/no-model** alternative to `/compact`: it elides selected
text without generating a summary and immediately returns to the idle single-agent prompt.
It applies to the active single-agent/lead session only—not swarm orchestrators, workers, an
in-flight turn, or a brand-new session before its first turn. Unsupported contexts explain this
instead of sending the command to a model. Run a normal turn after `/compact`, `/undo`, or a
reseed before pruning that pending context.

| Command | Action |
|---|---|
| `/prune` | Apply dupes, stale, and tools together; count each elision once |
| `/prune dupes` | Replace older exact duplicate text-content blocks of at least 1,000 characters; retain a complete copy |
| `/prune tools` | Replace older completed tool-result text blocks of at least 4,000 characters |
| `/prune stale` | Replace older successful observations superseded by a newer identical read-only call |

All passes protect system/developer messages, the original user goal, the two latest user turns
and at least approximately 4,000 recent context tokens, skill/instruction-file reads, errors,
images and other non-text data. The initial stale allowlist is exactly `Filesystem_read_text_file`
and `Filesystem_list_directory`; **all arguments**, including head/tail selectors, must match.
Unknown tools/structured result shapes are not guessed or flattened. Age alone does not establish
that prose is stale. Exact duplicate means ordinal text equality, not similar meaning; this does
not deduplicate individual lines or parse arbitrary Markdown documents.

Messages, tool calls, call IDs and result envelopes remain in place. Removed text becomes a short
`[pruned:…]` notice. Copies retained as evidence for duplicate/stale notices remain protected on
subsequent pruning. No eligible savings means no context mutation and no recovery-file write.
Counts/characters and estimated tokens saved are shown; estimates are not provider billing usage.

**Trade-off:** useful details may be removed, and changing historical prompt text can reduce prompt-cache
reuse on the next request. Pruning is explicit, so it does not wait for cache expiry. No compaction
model is called and no model-generated replacement facts are introduced. System/tool schema overhead
and surviving protected context are not removed.

Before a change, the original SDK session is serialized to a unique
`<sandbox>/prune-recovery/pre-prune-*.muxprune` file, published without overwriting. The sandbox must
be allowed and existing symlink/junction ancestors are rejected. If recovery storage fails, pruning
is not applied. These snapshots contain the **full pre-prune conversation**: treat them as sensitive
session records and retain/delete them deliberately; there is no automatic cleanup.

The active context and auxiliary compaction/undo history are then updated consistently. The displayed
transcript and existing artifacts are not deleted. Ordinary later session checkpoints serialize the
reduced context; pruning is not an immediate forced session save. The `.muxprune` snapshot retains the
original serialized session for manual recovery (copy it to `agent_session.json` in a separate session
folder before resuming there; do not overwrite a session you want to keep). `/undo` still undoes an
exchange, not the prune operation. Recovery publication is atomic on supported filesystems, not a
universal power-loss or adversarial filesystem-race guarantee.

## Agent picker (`/swap`)

In docked TUI (frame or inline), `/swap` opens a dedicated searchable agent list. The current
agent is marked and selected initially; the highlighted agent’s description appears below the list.
Type a name, abbreviation (ordered-character fuzzy match), or description terms. Every search term
must match; exact names rank first. The existing configured single-agent/default plus agent roster
and duplicate handling are unchanged. Filtering or navigating does not switch anything.

| Control | Action |
|---|---|
| Type / Backspace | Filter by fuzzy agent name or description terms |
| Up/Down, PgUp/PgDn, Home/End | Move the highlight; the selection stays visible |
| Ctrl+U | Clear the search |
| Enter | Choose the highlighted agent and close |
| Esc / Ctrl+Q / Ctrl+C | Cancel without changing the current agent |

The change is an **in-memory override for subsequent single-agent runs**, not a saved configuration
edit, model change, or hot-swap of a running agent. Invoking `/swap` inside a session retains the
existing confirmation to end the session and return to the menu. Classic, stdio, and non-docked
paths retain the existing number-or-exact-name prompt. Paste only changes the search; it never
confirms a choice. Tiny windows show resize/cancel guidance and cannot apply an unseen selection
(minimum 20 usable columns and 10 rows). Long names/descriptions are clipped only for display.

## Model and effort picker (`/setmodel`)

With a docked TUI (frame or inline), `/setmodel` opens a dedicated full-screen view rather than
printing model-selection cards into the transcript. It edits the same slots as before: the configured
single agent, orchestrator, compaction agent, and entries in `agents`. Duplicate display names are
disambiguated by the displayed slot ID. Each Apply updates one slot; switching slots reloads its saved
values, so Apply before moving to another slot if you want to keep an edit.

| Control | Action |
|---|---|
| Tab / Shift+Tab | Switch Slots, Models, and Effort panes |
| Up/Down, PgUp/PgDn, Home/End | Navigate the focused list |
| Type / Backspace in Models | Filter the active provider’s advertised IDs |
| Enter | Choose the slot or highlighted model and advance; does not save |
| Left/Right in Effort | Cycle default (no override), none, low, med, high, xhigh, max |
| F2 in Models | Toggle manual model-ID entry, including when discovery fails |
| F2 in Effort | Toggle a custom effort value; type the provider-specific value |
| F5 | Refresh the active provider catalog, cancelling the previous request |
| F4 | Explicitly Apply model + effort to the selected slot and close |
| Esc / Ctrl+Q | Cancel without writing configuration |

Discovery queries only the active provider’s normalized OpenAI-compatible `{endpoint}/models`
endpoint, with its configured bearer key and custom headers. A running but unrelated CLIProxy does
not replace that provider’s catalog. Errors/timeouts remain visible; manual model entry is always
available. This requests the catalog, not test completions: an advertised ID does not guarantee
account access, and `/models` generally does not describe supported effort values.

Apply writes `.model` and `.modelOpts.reasoning.effort` in the selected slot of the resolved
`Swarm.json`. “Default (no override)” removes only the effort override; existing runtime inheritance and ultra/giga
policies still apply. Other model options, reasoning
output, and unknown JSON properties remain intact. The file is replaced via a same-directory
staged write; a detected concurrent edit rejects the save rather than overwriting it. Saving refreshes
`App.SwarmConfig` for subsequent runs; it does **not** rebind an already-created agent or override a
CLI `--model` selection. Existing `/setmodel` session-to-main-menu routing is unchanged. Classic,
stdio, and non-docked TUI retain the prior manual model prompt; `/effort` remains session-local.

Use at least 12 terminal rows to browse. Smaller windows show a resize/cancel message. Long IDs
are clipped for display, never changed in the catalog; the header shows the staged model.


## Native tools in `/doctor` and `/fix`

The health snapshot distinguishes configured **external MCP connections** from native in-process
**Filesystem** and **Shell** tool groups. Entries intentionally replaced by native tools are not
reported as disconnected MCP servers. Startup and diagnostics share the same command/argument
recognition: the `native-runtime-tools` marker, legacy `server-filesystem`, or legacy
`mcp-async-repl` (case-insensitive substring matching).

The native-groups section reports global enablement from the canonical `Filesystem` / `Shell`
config entries; absent entries retain the runtime's default-on behavior. Disabled entries stay
explicitly disabled. This is not an execution probe or a grant of agent access: per-agent
`mcpServers` / `toolPatterns` and filesystem/shell security gates still apply.

Entry names alone do not suppress external-server checks. An ordinary external MCP definition,
even if named `Shell` or `Filesystem`, still requires its own connection. A legacy definition under
a different name is skipped under the existing replacement rule but does not create a new native
alias; native tool identities remain `Filesystem` and `Shell`.


## Scoped tool browser (`/tools [query]`)

`/tools` is read-only and works **in place** at the menu and in single-agent, swarm, and pswarm sessions;
it no longer ends a session to display global tools. The submitted listing and live TUI preview use
the same scope and fuzzy ranking. Matching searches tool name, native/MCP group, and description;
multiple words must all match, and names also support in-order subsequences (for example `fsrtf`).

At the main menu, **Global availability** contains enabled native Filesystem/Shell tools and connected
external MCP tools, not a permission grant to every agent. While MCP startup is loading, the preview
says so and updates when the catalog arrives. During a session it shows only the supplied agent or
orchestrator toolset, including local runtime/delegation tools. Before a single agent's first goal,
only its already-filtered native/MCP tools exist; the browser labels this bootstrap scope and adds
session-local tools once construction completes. Reattach restores the session's own catalog.

- Type `/tools <query>` to filter. **Up/Down** navigates; **Tab** or **Enter on a highlighted row**
  inserts `/tools <exact-name>`. Press Enter again to inspect it. This never calls the tool.
- Preview rows show source groups alongside names when space permits; narrow layouts prioritize
  the name and show the selected item's group/description below. Rows and height stay bounded.
- Submitted output has separate **MCP · server**, **Native · Filesystem/Shell**, and **Local · Runtime**
  headings with counts and concise descriptions. An exact name shows its full description.
- MCP grouping records the exact connected server at registration, so names containing underscores
  and external/native tools sharing a name are not conflated. A filtered scope never expands to a
  global list merely because it is empty. Unknown provenance is labeled rather than guessed.

Browsing changes no config, tool arguments, agent permissions, or tool-execution behavior. Existing
`/disabletools` removals and MCP refresh republish the global catalog so the menu does not list removed tools.
