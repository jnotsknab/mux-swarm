---
name: mcp-swagger
description: Generate runnable MCP servers from Swagger/OpenAPI specifications using the mcp-swagger-cli tool inside Docker. Use when the user wants to turn any REST API into an MCP server, integrate a third-party API into an agent workflow, or generate MCP tools from an OpenAPI/Swagger spec (JSON or YAML, local file or URL).
requires_bins: [docker]
---

# MCP Swagger — Docker ({{os}})

Generate runnable MCP servers from OpenAPI/Swagger specifications using the `mcp-swagger-cli` tool inside Docker.

## Setup

Write the full install + generate flow as a single Python script via Filesystem MCP, then run it in one container execution:

```python
# setup_and_generate.py
import subprocess, sys

# Install CLI from skill bundle
subprocess.run([sys.executable, "-m", "pip", "install", "-e", "/workspace/skills/bundled/mcp-swagger/scripts"], check=True)

# Verify
subprocess.run(["mcp-swagger", "--help"], check=True)
```

Run in Docker using the OS shell tool:

```bash
# Unix/Mac
docker run --rm \
  -v "{{paths.sandbox}}:/output" \
  -v "{{paths.base}}:/workspace" \
  "appropriate python container" python /workspace/setup_and_generate.py

# Windows
docker run --rm -v "{{paths.sandbox}}:/output" -v "{{paths.base}}:/workspace" "appropriate python container"-python python /workspace/setup_and_generate.py
```

**Never use `python -m mcp_swagger_cli` — the package has no `__main__.py`. Always use the `mcp-swagger` entry point.**

## Finding a Spec

**Priority order:**

1. **Official provider repo** — search GitHub for `<provider> openapi` or `<provider> swagger`.
2. **Provider developer docs** — look for `openapi.json` or `swagger.yaml`. Common paths: `/openapi.json`, `/api-docs`, `/swagger.json`.
3. **apis.guru** — last resort. Specs here are often outdated. Always validate base URL before using.

**Never use a spec without validating the base URL first.**

## Validation

Before generating, verify the spec's base URL resolves correctly:

```bash
mcp-swagger info "<spec-path-or-url>"
curl "<base-url><a-known-path>?<required-params>"
```

If curl returns a DNS error or 404, override with `--base-url` at generation time.

## Generating a Server

Write the full install + generate flow as a single Python script, then execute in Docker. All output paths inside the script must use `/output/`.

### Basic

```python
subprocess.run([
    "mcp-swagger", "create", "<spec>",
    "-o", "/output/<server-name>",
    "--name", "<ServerName>"
], check=True)
```

### With Base URL Override

```python
subprocess.run([
    "mcp-swagger", "create", "<spec>",
    "-o", "/output/<server-name>",
    "--name", "<ServerName>",
    "--base-url", "https://correct-host.com"
], check=True)
```

### With Header Auth

```python
subprocess.run([
    "mcp-swagger", "create", "<spec>",
    "-o", "/output/<server-name>",
    "--name", "<ServerName>",
    "--api-key-env", "MY_API_KEY",
    "--api-key-header", "Authorization",
    "--api-key-prefix", "Bearer"
], check=True)
```

### With Path Filtering (for large specs)

```python
subprocess.run([
    "mcp-swagger", "create", "<spec>",
    "-o", "/output/<server-name>",
    "--name", "<ServerName>",
    "--path-filter", "/v1/charges",
    "--path-filter", "/v1/customers",
    "--path-filter", "/v1/payment_intents"
], check=True)
```

### With Static Custom Headers

```python
subprocess.run([
    "mcp-swagger", "create", "<spec>",
    "-o", "/output/<server-name>",
    "--name", "<ServerName>",
    "-H", "X-App-Id: abc123",
    "-H", "X-Version: 2"
], check=True)
```

## Auth Patterns

| API auth style | How to handle |
|---|---|
| `Authorization: Bearer <token>` | `--api-key-env VAR --api-key-header Authorization --api-key-prefix Bearer` |
| `Authorization: Token <token>` | `--api-key-env VAR --api-key-header Authorization --api-key-prefix Token` |
| Custom header (e.g. `X-API-Key`) | `--api-key-env VAR --api-key-header X-API-Key --api-key-prefix ""` |
| Query param (e.g. `?api_key=`) | Pass key as tool argument, or manually wire env var in generated `main.py` |
| No auth | Omit all auth flags |

## After Generation

```bash
cd {{paths.sandbox}}/<server-name>
pip install -e .
<server-name>
```

Or with SSE:

```bash
<server-name> --sse 8000
```

## Verifying the Generated Server

After generation, check `BASE_URL` matches the actual working host:

```bash
{{shell}} {{shell.flag}} "grep -n BASE_URL {{paths.sandbox}}/<server-name>/main.py"
```

If it doesn't match, regenerate with `--base-url`.

## Connecting to Mux

Add to your `config.json` mcpServers:

```json
{
  "<server-name>": {
    "type": "stdio",
    "command": "<server-name>",
    "args": [],
    "env": {
      "MY_API_KEY": "MY_API_KEY"
    },
    "enabled": true
  }
}
```

## Known Pitfalls

- **`python -m mcp_swagger_cli` fails** — no `__main__.py`. Always use `mcp-swagger` entry point.
- **DNS error (`getaddrinfo failed`)** — base URL in spec is wrong. Override with `--base-url`.
- **401 Unauthorized** — API key invalid or wrong env var name. Test key directly with curl first.
- **404 on all endpoints** — spec paths missing a prefix. Override base URL to include the prefix.
- **Large specs (Stripe, GitHub, etc.)** — use `--path-filter`. Stripe uses `default` tag — `--tag` won't work, use `--path-filter`.
- **Optional params causing validation errors** — omit optional fields rather than passing `null`.
- **Query param auth exposed as tool argument** — manually edit `main.py` to read from `os.environ`.
- **Spec from apis.guru** — treat base URL as suspect. Always validate with curl before generating.

