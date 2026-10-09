# Changelog

All notable changes to Mux-Swarm are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **A note on versions.** Every release so far has been tagged `-alpha`. Git tag names and the
> version string the binary reports (`App.Version`) drifted apart during the 0.12 era — see
> [Version/tag drift](#versiontag-drift) at the bottom. Entries below are grouped by **git tag**,
> with the reported version noted where the two disagree. The `<Version>` field in `MuxSwarm.csproj`
> has never been maintained and is not a version source.

## [Unreleased]

## [v0.15.2-alpha] — Unreleased

### Security
- **Python REPL ran on the host under wrapper sandboxes.** With `sandbox.backend` set to `bwrap`, `firejail`,
  `sandbox-exec` or `custom`, shell commands were confined but the Python REPL tool (`repl_shell_exec`,
  `install_package_async`) ran unconfined on the host in v0.15.1 and earlier. The REPL worker now runs through
  the same wrapper. `custom` backends must declare `sandbox.replStdio: true` (template keeps stdin/stdout open)
  to get a REPL; otherwise REPL calls are refused. There is never a host fallback. (#100)

### Fixed
- Wrapper backends (bwrap, firejail, sandbox-exec, custom on Unix) passed commands as one re-split string,
  breaking any multi-word or quoted command; they now pass exact argv. bwrap jobs no longer die when the
  spawning thread exits. firejail is refused where it cannot confine (e.g. inside WSL/containers). (#99)
- podman `allowedDomains` requires podman 4+ (clear error on 3.x); the allowlist proxy now joins podman's own
  network for egress. (#101)
- `--version` / `-V` and `--help` exit before any setup (no `Configs/` written, no hang without a TTY). Relative
  `--cfg` / `--swarmcfg` resolve against the directory you launched from, also through the install shims. (#102)
- **A turn could vanish from the saved session.** A tool wait longer than `activityTimeoutSeconds` (e.g. a slow
  `ask_user` answer) tripped the watchdog, and the failed turn was saved as the pre-turn history. The watchdog
  now resets on tool calls/results and pauses during `ask_user`; a timed-out or failed turn is recorded as your
  message + `[turn ended: <reason>]` with the tools that ran. (#105)

### Added
- `/api/health` reports daemon trigger readiness (`daemon: off | starting | ready`). (#103)
- `/doctor` checks the configured sandbox backend for real; docs: podman + gVisor recipe, snap Docker FAQ. (#104)
- Compaction first saves the full session to `<sandbox>/prune-recovery/pre-compact-<session>-*.muxprune` plus a
  searchable `.txt` (tool manifest + transcript), appends a deterministic tool manifest to the summary, and
  points the agent at the snapshots. `/prune` snapshots get the `.txt` too. Re-compaction strips engine blocks
  and a non-summary reply falls back to a marked extractive summary. (#105)

## [v0.15.1-alpha] — Unreleased

### Changed
- **`docker` sandbox is now a microVM.** `sandbox.backend: docker` (and `/sandbox docker`, bare `--sandbox`)
  runs each session in a [Docker Sandboxes](https://docs.docker.com/ai/sandboxes/) microVM through the `sbx`
  CLI: its own kernel and Docker engine, on Windows 11, macOS (Apple silicon) and Ubuntu 24.04+. It needs
  `sbx login`; a missing or signed-out `sbx` is an error, never a fall back to a container or the host.
  `/work` and `/host/<leaf>` mounts work as before; `network: false` denies all egress.
- **Upgrade note:** configs with `backend: docker` (and `/dockerexec`) get the microVM. The previous container path is
  `docker-container` (alias `docker-legacy`), unchanged. `allowedDomains` only works on container backends
  and is rejected on `docker` (sbx cannot express a strict allowlist).

### Fixed
- Sandbox shell commands (`docker-container`, podman, nerdctl, gvisor, kata, and `docker`) received `$` as
  `\$` and doubled backslashes, so `$var`/`${x}` never expanded. Commands now reach `sh -c` unchanged.
- The primary session's sandbox container/microVM is now removed when Mux exits (it outlived every run).
- `/sandbox` and `--sandbox` no longer clear a configured `sandbox.runtime` (e.g. `kata-runtime` on podman)
  when switching backends.
- **`/share` guest typing:** an invisible format character (e.g. a zero-width space) before `!` or a
  blocked `/command` hid it from the guest-input policy while the orchestrator still dispatched it. Command
  prefixes (`!`, `/`) are now matched ordinally everywhere, and guest lines are classified on their visible
  text. Guest text itself is not modified.

## [v0.15.0-alpha] — Unreleased

Live session sharing, webhooks as request → run → result, and a working sandbox network allowlist.

### Added
- **Live session sharing.** `/share` streams your TUI to same-network guests over an end-to-end encrypted
  link (`/join <link>` or `mux-swarm --join "<link>"`). You approve every join (Deny / Watch / Watch + type);
  guests with typing can only edit at your idle prompt, never a modal or permission prompt, and `!shell`,
  lifecycle and config commands are refused. **Ctrl+]** revokes typing from everyone. See `docs/share.md`.
- **Webhook results.** Every accepted delivery returns a `deliveryId`; with `callbackUrl` set, the run's
  outcome (`status`, `result`, `error`) is POSTed back, signed with the trigger's `secret`.

### Changed
- Webhook runs are **stateless** (no session directory) in every mode, and an `agent` override applies to
  that run only. An unknown agent now fails the delivery instead of running the default agent.
- Daemon-fired swarm/pswarm runs are serialized with each other; agent-mode triggers stay concurrent.
- `/createhook` defaults `payloadLimit` to 65536 characters.
- Webhook default `cooldown` is now 0; a delivery inside a configured cooldown gets `429` + `Retry-After`
  instead of being accepted and dropped. `payloadLimit` default is 65536 characters, truncated with a marker.
- Webhook triggers added at runtime (web UI / `POST /api/daemon/trigger`) are live immediately.
- `/proxy update` only accepts plain `X.Y.Z` release tags, and a newer compiled-in pin overrides an older
  `/proxy update`.

### Fixed
- **Sandbox `allowedDomains` never worked:** the generated proxy script was a Python syntax error and ran
  from the sandbox image. The proxy now runs from `python:3.12-alpine` (pulled as its own step), and a dead
  proxy triggers a rebuild instead of a silent no-network sandbox.
- **macOS:** the CLIProxy sidecar stayed in Mux's process group when started without a terminal (launchd,
  `nohup`); it now detaches via `setsid` (Linux) or perl `POSIX::setsid` (macOS).
- Webhook failures (missing prompt, failed agent init) no longer report `status: ok`; cancelled and
  shutdown deliveries get an error callback; concurrent POSTs can no longer both pass a cooldown.
- The CI self-test uploads only its log on failure (the scratch proxy's keys were in the artifact).

### Breaking
- `App` and share types are `internal`; nothing outside the engine should reference the assembly.
- Webhook defaults changed (`cooldown` 0, `payloadLimit` 65536, stateless runs); see Changed.

## [v0.14.2-alpha] — 2026-09-23

Hotfixes: stateless sessions are never persisted by `/tag` or `/detach`, `/proxy update` installs the latest
upstream CLIProxyAPI (checksum-verified) and restarts the sidecar onto it, the macOS sidecar starts without
`setsid`, a `/tag` confirm freeze is mitigated (root cause unconfirmed), and `--selftest` runs end-to-end checks in CI.

## [v0.14.1-alpha] — 2026-09-16

Session durability, telemetry as a first-class surface, and TUI polish.

### Added
- **Telemetry dashboard is now a full four-tab UI** (Overview / Metrics / Traces / Logs) with an
  in-process OpenTelemetry stack — no external collector required. Runs independently of
  `telemetry.enabled`.
- **Durable trace storage.** Spans persist to daily `traces-yyyyMMdd.jsonl` files under `Telemetry/`
  with cumulative retention, surviving restarts. The Traces tab renders a parent/child waterfall
  plus a chronological step-through (turns, tool calls, messages, logs); Logs entries deep-link
  into their trace.
- **`check_delegations` can now wait.** A `waitSeconds` argument blocks server-side until a
  sub-agent makes discrete progress or finishes, replacing sleep-and-poll loops. Reads are
  delta-only per job, and finished results are reported once. Available to both the single-agent
  lead and the swarm orchestrator; `delegate_to_agent` gains `background=true`.
- **14 new themes** (21 total): Catppuccin (Mocha/Macchiato/Latte), Nord, Tokyo Night, Tokyo Storm,
  Rosé Pine, Rosé Dawn, Kanagawa, Everforest, One Dark, Monokai, Ayu Mirage, Synthwave.
- **Interactive theme picker.** Bare `/theme` opens a full-screen fuzzy-searchable list with a live
  preview pane rendering sample chrome in the highlighted theme.
- Session **tags are now the primary label** in every resume surface, with timestamps demoted to
  detail; tag search matches subsequences.
- New theme presets mirrored into the web app (17) and telemetry dashboard (12).

### Changed
- **MCP servers are non-strict by default.** A server that fails to start is skipped with a single
  badge-style notice and the run continues; startup aborts only if *every* enabled server fails.
  See Breaking for the environment-variable semantics change.
- The telemetry dashboard now ships as an on-disk file (`Runtime/mux-telemetry/index.html`) instead
  of an embedded string, so it can be edited without rebuilding.
- All web surfaces use thin themed scrollbars instead of default browser chrome.

### Fixed
- **Sessions are now written when you submit, not only after a reply arrives.** A new session
  previously had nothing on disk — not even a directory — until its first assistant response
  completed, so a crash during the first turn lost the exchange entirely.
- **Interrupted turns survive every cancellation path.** Session state was persisted *after* an
  outer-cancellation rethrow, so `/qc`, `/qm`, and Ctrl+C discarded the reconstructed user goal and
  partial reply that an Esc interrupt already preserved.
- Session writes are **atomic** (temp file + replace), closing a torn-read window that widened with
  session size.
- Session retention now counts real sessions rather than bare directories. Unrelated folders left
  in `Sessions/` (for example browser profile directories) were consuming retention slots and
  causing real sessions to be pruned early.
- Resuming no longer crashes on a session directory that has no saved file yet.
- An in-progress session no longer displaces a real one from the rolling-context window.
- **Agent output alignment.** Wrapped continuation lines of markdown bullets, numbered lists,
  checkboxes, and blockquotes fell back to column 0 instead of aligning under their marker text; a
  row breaking on a styled-text boundary could pick up a stray leading space; and rows could break
  well short of the right margin. Wrapping is now a single column-aware pass.
- Streaming no longer stalls on the first word when a provider delivers tokens in bursts.
- Sub-agent tool-call counts no longer report `0` for MCP-only sub-agents.
- Delegation waits wake on discrete events (a tool call landing, a status change, completion)
  rather than on streamed prose, eliminating constant ~2s wake churn.
- Metric panels share one column grid, so columns line up across panels.
- The settings picker no longer masks what you are typing.
- The thinking indicator survives streaming and coexists with the sub-agent activity strip.

### Breaking
- **`MUXSWARM_MCP_STRICT` semantics inverted.** Previously any value other than `0` meant strict;
  now *only* the exact value `1` enables strict mode. `MUXSWARM_MCP_STRICT=true` no longer has any
  effect. Use `--mcp-strict` or `MUXSWARM_MCP_STRICT=1` to restore the old behavior.

## [v0.14.0-alpha] — 2026-09-14

Agent efficiency, token usage, and user experience.

### Changed
- **Default renderer is now the frame engine** (`console.renderEngine` default `inline` → `frame`),
  an alt-screen full-frame renderer with mouse support.
- **Full mouse control by default** (`console.mouseTracking` default `wheel` → `buttons`).
- Tool output collapses more aggressively (`console.collapseToolLines` 6 → 3).
- Esc / Ctrl+Q now cancel the entire turn and every worker it owns, not just the current step.
- The default agent persona no longer carries a blanket no-delegation rule.
- Reasoning `max`/`custom` levels no longer silently fall back to a lower effort.

### Removed
- **Vim Normal mode removed from the prompt editor.** The line editor is now a single consistent
  input mode.

### Breaking
- Namespace and directory refactor: `MuxSwarm.Utils` → `MuxSwarm.Engine` (`Utils/` → `Engine/`).
  Affects anything referencing internal types by namespace.
- Configurations that explicitly set `renderEngine`, `mouseTracking`, or `collapseToolLines` keep
  their existing values; only unset keys pick up the new defaults.

## [v0.13.2-alpha] — 2026-09-04

Maintenance release.

## [v0.13.1-alpha] — 2026-08-30

Security hardening plus TUI quality-of-life.

### Security
- **Fixed a path-traversal vulnerability in the `--serve` file API**
  ([GHSA-vxmx-253h-48pr](https://github.com/jnotsknab/mux-swarm/security/advisories/GHSA-vxmx-253h-48pr),
  CWE-22, CVSS 8.6). The containment check used a bare string-prefix test, so a *sibling* directory
  whose name began with the sandbox root's path (e.g. `<root>_escape`) passed validation. All seven
  call sites were affected, including recursive delete. The check now canonicalizes the root and
  requires a true path-component boundary, with case-insensitive comparison on Windows to close a
  second case-confusion escape. Reported privately by SK Shieldus EQSTLab.
- **The web UI now binds to loopback by default** (`serveAddress` `0.0.0.0` → `127.0.0.1`).
  Exposing the server beyond the local machine is now an explicit opt-in.

### Added
- Nine TUI quality-of-life improvements and mid-turn steering with Ctrl+N.

### Breaking
- `serveAddress` default change (above): deployments that relied on the server listening on all
  interfaces must now set `serveAddress` explicitly.

## [v0.13.0-alpha] — 2026-08-24

Web UI and TUI improvements.

## [v0.12.4-alpha] — 2026-07-19

*Reports version `0.12.3`.* Frame renderer and a unified input plane.

### Added
- **Frame renderer** with a real viewport over retained history, scrollbar chrome, and mouse
  support, as an alternative to the inline live-region renderer.
- Single consolidated input plane, eliminating a class of mouse-report leakage into the prompt.

### Removed
- Vim Normal mode in the prompt editor (later finalized in v0.14.0).

## [v0.12.3-alpha] — 2026-07-16

Benchmark and harness tuning.

## [v0.12.2-alpha] — 2026-07-13

*Reports version `0.12.1`.* Maintenance release.

## [v0.12.1-alpha] — 2026-07-05

The large v0.12.0 feature set landed here — see [Version/tag drift](#versiontag-drift).

### Added
- **Zed Agent Client Protocol (`--acp`)** support, driving ACP-compatible editors with streaming
  tool-call diffs and locations.
- **Execution sandboxing** with multiple backends.
- **Native in-process tools** for filesystem and shell work.
- **CLI proxy integration** for subscription-backed model routing.

### Breaking
- The `mcp-async-repl` MCP server was replaced by native in-process tools. Configurations
  referencing the old server must be updated.

## [v0.12.0-alpha] — 2026-06-23

*Reports version `0.11.1`.* TUI rendering and input fixes — despite the tag, this is the 0.11.1
follow-up to the live-TUI release, not the 0.12 feature set.

## [v0.11.0-alpha] — 2026-06-22

The live TUI.

### Added
- **Live-region renderer**: a pinned bottom region (streaming output, input box, status footer)
  repainted in place, with finished output committed into native terminal scrollback.
- Markdown rendering for assistant output — headings, bold/italic, inline code, lists, tables, and
  fenced code blocks render as styled terminal text.
- Collapsible tool-result cards and diff cards.
- Live token metering in the footer.

### Changed
- Default render mode changed from `auto` to the TUI renderer.
- Context auto-compaction threshold raised (80k → 200k tokens).
- Sub-agent output collapses by default.
- Activity timeout raised (1200s → 3600s).

## [v0.10.3-alpha] — 2026-06-07

Maintenance release.

## [v0.10.2-alpha] — 2026-06-01

Maintenance release.

## [v0.10.1-alpha] — 2026-05-24

Maintenance release.

## [v0.10.0-alpha] — 2026-05-23

Daemon, automation, and memory work.

## [v0.9.6-alpha] — 2026-04-09

Maintenance release.

## [v0.9.5-alpha] — 2026-04-05

Maintenance release.

## [v0.9.4-alpha] — 2026-04-03

Maintenance release.

## [v0.9.3-alpha] — 2026-04-03

Plan mode and CI migration.

### Changed
- CI moved from GitHub Actions to Depot.

### Breaking
- The `ask_user` options delimiter changed from `,` to `|`, so options may now contain commas.

## [v0.9.2-alpha] — 2026-03-29

Plan mode groundwork.

## [v0.9.1-alpha] — 2026-03-27

Maintenance release.

## [v0.9.0-alpha] — 2026-03-26

Feature release.

## [v0.8.2-alpha] — 2026-03-26

Maintenance release.

## [v0.8.1-alpha] — 2026-03-24

### Breaking
- Added `serveAddress`; the server no longer unconditionally binds all interfaces.

## [v0.8.0-alpha] — 2026-03-20

### Breaking
- `--watchdog` became boolean-only and now applies to all execution modes.

## [v0.7.0-alpha] — 2026-03-19

Feature release.

## [v0.6.2-alpha] — 2026-03-16

Maintenance release.

## [v0.6.1-alpha] — 2026-03-15

The workflow engine (despite the version number — see [Version/tag drift](#versiontag-drift)).

### Changed
- **Token counting switched from local estimation to provider-reported usage**, so reported totals
  and costs changed for existing users.

## [v0.6.0-alpha] — 2026-03-15

A one-line configuration fix.

### Breaking
- Configuration defaults are no longer re-applied on load, so a key explicitly set to a
  falsy/empty value is now respected instead of being silently replaced by its default.

## [v0.5.1-alpha] — 2026-03-15

Maintenance release.

## [v0.5.0-alpha] — 2026-03-12

Feature release.

## [v0.4.0-alpha] — 2026-03-09

Feature release.

## [v0.3.0-alpha] — 2026-03-09

### Breaking
- `/multiagent` renamed to `/swarm`.

## [v0.2.0-alpha] — 2026-03-08

Feature release.

## [v0.1.1-alpha] — 2026-03-06

Maintenance release.

## [v0.1.0-alpha] — 2026-03-06

Initial public open-source release of Mux-Swarm: a .NET multi-agent orchestration engine with a
single-agent mode, multi-agent swarm coordination, MCP client support, a configurable agent/skill
system, session persistence, and a console interface.

---

## Version/tag drift

Git tag names and the version reported by the binary (`App.Version`) diverged during the 0.12 era.
When reading history, trust the tag for *what shipped* and this table for *what the binary said*:

| Git tag | Reported `App.Version` | Note |
|---|---|---|
| `v0.6.0-alpha` | — | A one-line config fix; the "workflow engine" work is under `v0.6.1-alpha`. |
| `v0.9.2-alpha` | — | Part of the plan-mode chain landed under `v0.9.3-alpha`. |
| `v0.12.0-alpha` | `0.11.1` | Contains the 0.11.1 TUI follow-up, not the 0.12 feature set. |
| `v0.12.1-alpha` | `0.12.1` | Carries **both** the 0.12.0 and 0.12.1 work (ACP, sandboxing, native tools, CLI proxy). |
| `v0.12.2-alpha` | `0.12.1` | Version not bumped. |
| `v0.12.4-alpha` | `0.12.3` | Version not bumped. |

`MuxSwarm.csproj`'s `<Version>` field has never tracked releases (it has read `0.9.5` since the 0.9
era) and is not a version source. The surfaced string is `App.Version` in `App.cs`.
