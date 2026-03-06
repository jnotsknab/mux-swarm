# MCP Swagger CLI

> Generate MCP (Model Context Protocol) servers from Swagger/OpenAPI specifications

[![Python Version](https://img.shields.io/pypi/pyversions/mcp-swagger-cli)](https://pypi.org/project/mcp-swagger-cli/)
[![License](https://img.shields.io/pypi/l/mcp-swagger-cli)](LICENSE)

MCP Swagger CLI is a command-line tool that generates runnable MCP servers from Swagger/OpenAPI specifications. It maps API operations to MCP Tools, schemas to MCP Resources, and provides a complete, ready-to-use MCP server implementation.

## Features

- **Parse OpenAPI 3.x and Swagger 2.0** specifications
- **Generate MCP Tools** from API operations (GET, POST, PUT, DELETE, etc.)
- **Generate MCP Resources** from API schemas
- **Multiple transport support**: stdio and SSE/HTTP
- **Automatic type conversion** from JSON Schema to Python types
- **CLI with progress feedback** and validation

## Installation

### Using pip

```bash
pip install mcp-swagger-cli
```

### Using uv (recommended for speed)

```bash
uv pip install mcp-swagger-cli
```

### Development Installation

```bash
git clone https://github.com/mcp-swagger/mcp-swagger-cli.git
cd mcp-swagger-cli
pip install -e .
```

## Usage

### Basic Usage

Generate an MCP server from a Swagger/OpenAPI specification:

```bash
mcp-swagger create https://petstore.swagger.io/v2/swagger.json -o ./my_mcp_server
```

### With Custom Options

```bash
mcp-swagger create ./api_spec.yaml \
    --output ./my_server \
    --name my_api \
    --transport stdio \
    --base-url https://api.example.com
```

### Validate a Specification

Before generating, validate your OpenAPI spec:

```bash
mcp-swagger validate-spec https://petstore.swagger.io/v2/swagger.json
```

### View Spec Information

Display information about a specification without generating:

```bash
mcp-swagger info https://petstore.swagger.io/v2/swagger.json
```

## Command Reference

### `mcp-swagger create`

Create an MCP server from a Swagger/OpenAPI specification.

```
Usage: mcp-swagger create <spec> [OPTIONS]

Arguments:
  spec                  URL or file path to Swagger/OpenAPI specification

Options:
  -o, --output PATH     Output directory for generated MCP server
  -n, --name TEXT      Name for the generated MCP server
  -t, --transport TEXT Transport type (stdio or sse)
  -b, --base-url TEXT  Base URL for API requests
  --validate / --no-validate  Validate specification before generating
  -f, --force          Overwrite output directory if it exists
  -v, --verbose        Enable verbose output
  --help               Show this message and exit.
```

### `mcp-swagger validate-spec`

Validate a Swagger/OpenAPI specification.

```
Usage: mcp-swagger validate-spec <spec> [OPTIONS]

Arguments:
  spec    URL or file path to Swagger/OpenAPI specification

Options:
  -v, --verbose    Show detailed validation results
  --help           Show this message and exit.
```

### `mcp-swagger info`

Show information about a Swagger/OpenAPI specification.

```
Usage: mcp-swagger info <spec> [OPTIONS]

Arguments:
  spec    URL or file path to Swagger/OpenAPI specification

Options:
  --help    Show this message and exit.
```

## Generated Server Usage

After generating an MCP server, follow these steps to use it:

### 1. Install the Server

```bash
cd my_mcp_server
pip install -e .
```

### 2. Run the Server

**Stdio Transport** (recommended for Claude Desktop):
```bash
my_mcp_server
```

**SSE Transport** (for remote access):
```bash
my_mcp_server --sse 8000
```

### 3. Configure with Claude Desktop

Add to your Claude Desktop config:

```json
{
  "mcpServers": {
    "my_mcp_server": {
      "command": "my_mcp_server",
      "args": []
    }
  }
}
```

## How It Works

MCP Swagger CLI parses your OpenAPI/Swagger specification and generates:

1. **MCP Tools** - Each API operation becomes an MCP tool with:
   - Tool name from `operationId` (or generated)
   - Description from `summary`/`description`
   - Parameters from path/query/header parameters
   - Request body support

2. **MCP Resources** - Each schema becomes an MCP resource:
   - `schema://api/schemas` - List of all schemas
   - `schema://api/{schema_name}` - Individual schema definitions
   - `api://operations` - List of all operations

3. **Transport** - Supports:
   - `stdio` - Standard I/O (local, default)
   - `sse` - Server-Sent Events over HTTP

## Examples

See the [examples](examples/) directory for sample specifications and usage.

## Development

### Running Tests

```bash
# Install dev dependencies
pip install -e ".[dev]"

# Run tests
pytest tests/ -v
```

### Project Structure

```
mcp-swagger-cli/
├── mcp_swagger_cli/
│   ├── __init__.py
│   ├── cli.py           # CLI commands
│   ├── generator.py     # Server generation logic
│   ├── parser.py        # OpenAPI parsing
│   ├── exceptions.py    # Custom exceptions
│   └── templates/       # Jinja2 templates
├── tests/               # Test suite
├── pyproject.toml       # Project configuration
└── README.md            # This file
```

## Requirements

- Python 3.10+
- typer (CLI framework)
- httpx (HTTP client)
- prance (OpenAPI parser)
- jinja2 (template engine)

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome! Please read our [contributing guidelines](CONTRIBUTING.md) first.

## Related

- [MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk) - Official MCP Python implementation
- [FastMCP](https://github.com/jlowin/fastmcp) - High-level MCP framework
- [OpenAPI Specification](https://spec.openapis.org/) - OpenAPI standard
