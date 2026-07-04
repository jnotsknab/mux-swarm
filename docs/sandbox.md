# Execution Sandbox & Security Posture

Mux-Swarm can route all native shell and REPL execution through a per-session sandbox. The
sandbox is pluggable: OCI container engines, microVMs, lightweight process wrappers, or a
custom command all slot behind the same config block. This page covers configuring the
sandbox, swapping backends at runtime, and the broader filesystem/shell security posture for
production deployments.

## What gets sandboxed

The native Shell/REPL tools (`repl_shell_exec` and the async shell-job tools). When a sandbox
backend is active, each session gets its own isolated execution environment (a per-session
container for OCI backends). Filesystem MCP tools and other integrations are governed
separately by the filesystem allowlist (below), not the sandbox.

`SandboxRuntime.IsActive` is authoritative: the agent preamble reflects the live sandbox
state, so agents know whether their shell runs on the host or inside isolation.

## Configuration (`config.json` `sandbox`)

```json
"sandbox": {
  "backend": "docker",
  "image": "python:3.12-slim",
  "network": false,
  "allowedDomains": []
}
```

| Key | Default | Description |
|-----|---------|-------------|
| `backend` | `host` | `host` (no sandbox), `docker`, `podman`, `nerdctl`, `gvisor`, `kata` (microVM), `bwrap` / `firejail` / `sandbox-exec` (process wrappers), or `custom`. |
| `image` | `python:3.12-slim` | Container image for OCI backends. |
| `network` | `false` | Allow network egress from the sandbox. |
| `allowedDomains` | `[]` | With `network` on and a non-empty list, a deny-by-default CONNECT allowlist is enforced (OCI backends only). |
| `command` | `""` | Command template for the `custom` backend. |
| `runtime` | `""` | Optional `--runtime` passthrough for OCI backends (e.g. `kata-runtime`). |

### Backend classes

- **OCI engines** (`docker`, `podman`, `nerdctl`): per-session container, image-based,
  network allowlist support. The most common production choice.
- **Hardened OCI** (`gvisor`, `kata`): gVisor user-space kernel or Kata microVM for stronger
  isolation than a plain container. `kata` typically pairs with `runtime: "kata-runtime"`.
- **Process wrappers** (`bwrap`, `firejail` on Linux; `sandbox-exec` on macOS): lightweight
  namespace/profile isolation without a container engine.
- **`custom`**: bring your own wrapper via `command`.
- **`host`**: no sandbox; execution runs directly on the machine (default).

## Selecting a backend

- **Config**: set `sandbox.backend` in `config.json` (persists).
- **Startup flag**: `--sandbox [backend]` overrides at launch (bare `--sandbox` defaults to
  `docker`); the choice is validated and synced back to config.
- **Runtime**: `/sandbox` hot-swaps the backend inside a live session.

## Filesystem & shell security (`config.json`)

The native in-process Filesystem and Shell/REPL tools enforce configurable security postures
independent of any MCP server:

```json
"filesystem": { "securityMode": "standard" },
"shell": { "securityMode": "off", "allowedCommands": [] }
```

| Key | Values | Description |
|-----|--------|-------------|
| `filesystem.securityMode` | `standard` (default), `secure`, `lax`, `none` | Enforcement level for native filesystem tools. `standard` honors `allowedPaths`; `secure` is strictest; `lax`/`none` relax checks. |
| `shell.securityMode` | `off` (default), `prompt`, `allowlist` | Gate on native Shell/REPL execution. `off` runs commands ungated (default, run-anything); `prompt` asks for confirmation on every command; `allowlist` runs commands whose first token is in `allowedCommands` and prompts for anything else. Non-interactive sessions auto-deny a prompt. |
| `shell.allowedCommands` | string[] | Commands permitted when `securityMode` is `allowlist`. |

The filesystem allowlist itself lives in `filesystem.allowedPaths`, alongside the sandbox,
skills, sessions, and prompts paths.

## Security model

Mux-Swarm is designed around scoped execution, explicit boundaries, and inspectable outputs:
filesystem allowlist enforcement, least-privilege per-agent MCP scoping, prompt- and
config-level role separation, deterministic completion signaling, session-based provenance
and artifact trails, configurable sandboxed execution, environment-variable-based secret
handling, hook execution gating, and daemon trigger isolation.

## Recommended production stance

- Use `--cfg` and `--swarmcfg` to isolate per-user or per-environment instances.
- Keep `--mcp-strict` enabled (the default) so startup fails if required integrations are
  unavailable.
- Keep filesystem allowed paths minimal and purpose-specific.
- Route execution-heavy tasks through an OCI sandbox backend when possible; step up to
  `gvisor` or `kata` when running untrusted or generated code.
- Keep `sandbox.network` off unless the workload needs egress; when it does, prefer a tight
  `allowedDomains` allowlist over open network.
- Scope MCP servers narrowly by role.
- Use environment variables for all credentials.
- Prefer file-path-based deliverables so outputs remain inspectable.
- Use `/report` or `--report` to review session artifacts regularly.
- Review hook commands before confirming on startup - hooks run with your user permissions.
- Scope daemon watch paths narrowly; avoid watching broad directories like home or root.
- Use `--register` from an elevated terminal only after validating your daemon and hook
  config.
- Pair `--daemon` with status checks (`failThreshold` > 1) to avoid restart loops on
  transient failures.
- Use the three-layer resilience stack (`--register` + `--watchdog` + status triggers) for
  production always-on deployments rather than relying on any single layer.
- For maximum isolation, run mux-swarm itself inside a container with only the necessary
  volumes mounted - this constrains all agent execution, hook commands, and daemon triggers
  to the container's filesystem and network boundaries regardless of configuration.

---
[Back to docs index](README.md) | [Main README](../README.md)
