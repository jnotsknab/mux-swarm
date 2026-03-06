---
name: mcp-swagger
description: Generate runnable MCP servers from Swagger/OpenAPI specifications using the mcp-swagger-cli tool. Use when the user wants to turn any REST API into an MCP server, integrate a third-party API into an agent workflow, or generate MCP tools from an OpenAPI/Swagger spec (JSON or YAML, local file or URL).
requires_bins: [uv, python]
---

# MCP Swagger ({{os}})

Generate runnable MCP servers from OpenAPI/Swagger specifications using the `mcp-swagger-cli` tool.

## Setup

Install the CLI into a virtual environment before use:

```bash
uv venv {{paths.base}}/mcp-swagger-venv
uv pip install -e {{paths.base}}/skills/bundled/mcp-swagger/scripts
```

Verify install:
```bash
{{paths.base}}/mcp-swagger-venv/bin/mcp-swagger --help
```

**Never use `python -m mcp_swagger_cli` — the package has no `__main__.py`. Always use the `mcp-swagger` entry point.**

## Finding a Spec

**Priority order:**

1. **Official provider repo** — search GitHub for `<provider> openapi` or `<provider> swagger`. Most major APIs (Stripe, GitHub, Twilio) maintain an official spec under `/openapi` or `/swagger`.
2. **Provider developer docs** — look for a link to `openapi.json` or `swagger.yaml` on their API docs page. Common paths: `/openapi.json`, `/api-docs`, `/swagger.json`.
3. **apis.guru** — use as a last resort. Specs here are often outdated. Always validate the base URL before using.

**Never use a spec without validating the base URL first.**

## Validation

Before generating, verify the spec's base URL resolves correctly:

```bash
# Check what base URL the spec declares
mcp-swagger info "<spec-path-or-url>"

# Test a real endpoint from the spec manually
curl "<base-url><a-known-path>?<required-params>"
```

If curl returns a DNS error or 404, the spec's base URL is wrong. Override it with `--base-url` at generation time.

## Generating a Server

### Basic

```bash
mcp-swagger create <spec> -o {{paths.sandbox}}/<server-name> --name <ServerName>
```

### With Base URL Override (recommended when spec is from an aggregator)

```bash
mcp-swagger create <spec> -o {{paths.sandbox}}/<server-name> --name <ServerName> --base-url https://correct-host.com
```

### With Header Auth

```bash
mcp-swagger create <spec> -o {{paths.sandbox}}/<server-name> --name <ServerName> \
  --api-key-env MY_API_KEY \
  --api-key-header Authorization \
  --api-key-prefix Bearer
```

### With Path Filtering (for large specs)

```bash
mcp-swagger create <spec> -o {{paths.sandbox}}/<server-name> --name <ServerName> \
  --path-filter /v1/charges \
  --path-filter /v1/customers \
  --path-filter /v1/payment_intents
```

### With Static Custom Headers

```bash
mcp-swagger create <spec> -o {{paths.sandbox}}/<server-name> --name <ServerName> \
  -H "X-App-Id: abc123" \
  -H "X-Version: 2"
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
uv pip install -e .
<server-name>          # starts with stdio transport
```

Or with SSE:

```bash
<server-name> --sse 8000
```

## Verifying the Generated Server

After generation, check that `BASE_URL` matches the actual working host:

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

- **`python -m mcp_swagger_cli` fails** — no `__main__.py`. Always use `mcp-swagger` entry point after install.
- **DNS error (`getaddrinfo failed`)** — base URL in spec is wrong. Override with `--base-url`.
- **401 Unauthorized** — API key invalid or wrong env var name. Test key directly with curl first.
- **404 on all endpoints** — spec paths missing a prefix. Override base URL to include the prefix.
- **Large specs (Stripe, GitHub, etc.)** — generated `main.py` can exceed 40k+ lines. Use `--path-filter` to scope. Note: Stripe uses a single `default` tag — use `--path-filter` not `--tag`.
- **Optional params causing validation errors** — omit optional fields entirely rather than passing `null`.
- **Query param auth exposed as tool argument** — manually edit `main.py` to read from `os.environ` if needed.
- **Spec from apis.guru** — treat base URL as suspect. Always validate with curl before generating.

