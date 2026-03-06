"""Generator module for creating MCP servers from OpenAPI specs."""

import re
import shutil
from pathlib import Path
from typing import Any

import jinja2

from mcp_swagger_cli.exceptions import GeneratorError, TemplateError
from mcp_swagger_cli.parser import OpenAPIParser


class MCPServerGenerator:
    """Generate MCP servers from OpenAPI specifications."""
    
    def __init__(
        self,
        spec_path: str,
        server_name: str = "mcp_server",
        transport: str = "stdio",
        base_url: str | None = None,
        validate: bool = True,
        verbose: bool = False,
        api_key_env: str | None = None,
        api_key_header: str = "Authorization",
        api_key_prefix: str = "Bearer",
        extra_headers: dict[str, str] | None = None,
        tags: list[str] | None = None,
        path_filters: list[str] | None = None,
        max_operations: int | None = None,
    ) -> None:
        """Initialize the generator.
        
        Args:
            spec_path: Path or URL to the OpenAPI spec
            server_name: Name for the generated server
            transport: Transport type (stdio or sse)
            base_url: Base URL for API requests
            validate: Whether to validate the spec
            verbose: Enable verbose output
            api_key_env: Environment variable name to read API key from
            api_key_header: HTTP header name for API key
            api_key_prefix: Prefix for API key in header (e.g., 'Bearer')
            extra_headers: Additional custom headers as dict
            tags: Filter operations by tags (only include operations with matching tags)
            path_filters: Filter operations by path (only include operations with paths containing the filter)
            max_operations: Maximum number of operations to include (requires tags or path_filters if exceeded)
        """
        self.spec_path = spec_path
        self.server_name = self._sanitize_name(server_name)
        self.transport = transport
        self.base_url = base_url
        self.verbose = verbose
        self.api_key_env = api_key_env
        self.api_key_header = api_key_header
        self.api_key_prefix = api_key_prefix
        self.extra_headers = extra_headers or {}
        self.tags = tags or []
        self.path_filters = path_filters or []
        self.max_operations = max_operations
        
        # Parse the spec
        self.parser = OpenAPIParser(spec_path=spec_path, validate=validate)
        self.spec = self.parser.spec
        self.spec_info = self.parser.get_spec_info()
        
        # Set default base_url from spec if not provided
        if not self.base_url:
            servers = self.spec_info.get("servers", [])
            if servers:
                self.base_url = servers[0]
            else:
                # FIX: Use empty string instead of leaving as None (becomes "None" in template)
                self.base_url = ""
        
        # Setup Jinja2 environment
        self.jinja_env = jinja2.Environment(
            loader=jinja2.PackageLoader("mcp_swagger_cli", "templates"),
            trim_blocks=True,
            lstrip_blocks=True,
            keep_trailing_newline=True,
        )
        
        # Add custom filters
        self.jinja_env.filters["sanitize_name"] = self._sanitize_name
        self.jinja_env.filters["to_python_type"] = self._to_python_type
        self.jinja_env.filters["to_json_type"] = self._to_json_type
        self.jinja_env.filters["escape_docstring"] = self._escape_docstring
        self.jinja_env.filters["sort_params"] = self._sort_params
        self.jinja_env.filters["sanitize_toml_string"] = self._sanitize_toml_string
    
    @staticmethod
    def _sanitize_name(name: str) -> str:
        """Sanitize a name to be a valid Python identifier."""
        # Replace spaces and hyphens with underscores
        name = re.sub(r"[\s\-]+", "_", name)
        # Remove invalid identifier characters
        name = re.sub(r"[^a-zA-Z0-9_]", "", name)
        # Ensure it doesn't start with a digit
        if name and name[0].isdigit():
            name = "_" + name
        return name or "mcp_server"
    
    @staticmethod
    def _to_python_type(json_type: str | None) -> str:
        """Convert JSON schema type to Python type."""
        type_mapping = {
            "string": "str",
            "integer": "int",
            "number": "float",
            "boolean": "bool",
            "array": "list",
            "object": "dict",
            "file": "bytes",
            "date": "datetime.date",
            "date-time": "datetime.datetime",
        }
        return type_mapping.get(str(json_type), "Any")
    
    @staticmethod
    def _to_json_type(python_type: str) -> str:
        """Convert Python type to JSON schema type."""
        type_mapping = {
            "str": "string",
            "int": "integer",
            "float": "number",
            "bool": "boolean",
            "list": "array",
            "dict": "object",
        }
        return type_mapping.get(python_type, "string")
    
    @staticmethod
    def _escape_docstring(text: str | None) -> str:
        """Escape text for use in docstrings."""
        if not text:
            return ""
        # Escape triple quotes and backslashes
        text = text.replace("\\", "\\\\")
        text = text.replace('"""', '\\"\\"\\"')
        return text.strip()

    @staticmethod
    def _sanitize_toml_string(text: str | None, max_length: int = 200) -> str:
        """Sanitize a string for use in TOML single-line values.
        
        Strips newlines and whitespace, truncates to max_length chars,
        and escapes TOML-invalid characters (quotes, backslashes).
        
        Args:
            text: Input string to sanitize
            max_length: Maximum length (default 200)
            
        Returns:
            Sanitized string safe for TOML single-line strings
        """
        if not text:
            return ""
        
        # Replace newlines, tabs, and other whitespace with spaces
        text = re.sub(r'[\n\r\t]+', ' ', text)
        # Collapse multiple spaces into one
        text = re.sub(r' +', ' ', text)
        # Strip leading/trailing whitespace
        text = text.strip()
        
        # Escape backslashes first (before adding more escapes)
        text = text.replace('\\', '\\\\')
        # Escape double quotes
        text = text.replace('"', '\\"')
        
        # Truncate to max_length (accounting for escape chars added)
        if len(text) > max_length:
            text = text[:max_length]
        
        return text

    @staticmethod
    def _sort_params(params: list[dict[str, Any]]) -> list[dict[str, Any]]:
        """Sort parameters: required first, then optional.
        
        Preserves original order within each group (required or optional).
        This ensures valid Python function signatures where parameters
        without defaults (required) come before parameters with defaults (optional).
        """
        required = [p for p in params if p.get("required", False)]
        optional = [p for p in params if not p.get("required", False)]
        return required + optional
    
    def generate(self, output_dir: Path, force: bool = False) -> None:
        """Generate the MCP server files.
        
        Args:
            output_dir: Directory to write generated files
            force: Whether to overwrite existing files
        """
        output_dir = Path(output_dir)
        
        # Create output directory
        if output_dir.exists() and force:
            shutil.rmtree(output_dir)
        output_dir.mkdir(parents=True, exist_ok=True)
        
        # Create server package subdirectory
        server_package_dir = output_dir / self.server_name
        server_package_dir.mkdir(parents=True, exist_ok=True)
        
        # Generate all files
        self._generate_init_py(output_dir)
        self._generate_main_py(output_dir)
        self._generate_pyproject_toml(output_dir)
        self._generate_requirements_txt(output_dir)
        self._generate_readme(output_dir)
        self._generate_example_spec(output_dir)
        
        if self.verbose:
            print(f"Generated MCP server at {output_dir}")
    
    def _render_template(self, template_name: str, context: dict[str, Any]) -> str:
        """Render a template with the given context."""
        try:
            template = self.jinja_env.get_template(template_name)
            return template.render(**context)
        except jinja2.TemplateError as e:
            raise TemplateError(f"Failed to render template {template_name}: {e}")
    
    def _generate_init_py(self, output_dir: Path) -> None:
        """Generate the __init__.py file in the server package directory."""
        # Create the package subdirectory and __init__.py
        server_package_dir = output_dir / self.server_name
        server_package_dir.mkdir(parents=True, exist_ok=True)
        
        init_py = server_package_dir / "__init__.py"
        # Write empty __init__.py
        init_py.write_text("", encoding="utf-8")
        
        if self.verbose:
            print(f"  Generated {init_py}")
    
    def _generate_main_py(self, output_dir: Path) -> None:
        """Generate the main.py file in the server package subdirectory."""
        operations = self.parser.get_operations()
        schemas = self.parser.get_schemas()
        
        # Filter operations based on tags and path_filters
        filtered_operations = self._filter_operations(operations)
        
        # Prepare context
        context = {
            "server_name": self.server_name,
            "title": self.spec_info.get("title", "MCP Server"),
            "version": self.spec_info.get("version", "1.0.0"),
            "description": self.spec_info.get("description", ""),
            "base_url": self.base_url,
            "transport": self.transport,
            "operations": filtered_operations,
            "schemas": schemas,
            "schema_names": list(schemas.keys()),
            "path_count": self.spec_info.get("path_count", 0),
            "operation_count": len(filtered_operations),
            # Authentication parameters
            "api_key_env": self.api_key_env,
            "api_key_header": self.api_key_header,
            "api_key_prefix": self.api_key_prefix,
            "extra_headers": self.extra_headers,
        }
        
        # Render template
        content = self._render_template("main.py.j2", context)
        
        # Write file to server package subdirectory
        main_py = output_dir / self.server_name / "main.py"
        main_py.write_text(content, encoding="utf-8")
        
        if self.verbose:
            print(f"  Generated {main_py}")
    
    def _filter_operations(self, operations: list[dict[str, Any]]) -> list[dict[str, Any]]:
        """Filter operations based on tags and path_filters.
        
        Args:
            operations: List of operation dictionaries from the parser
            
        Returns:
            Filtered list of operations
            
        Raises:
            GeneratorError: If max_operations is exceeded without sufficient filtering
        """
        if not self.tags and not self.path_filters:
            # No filtering requested, but check max_operations
            if self.max_operations is not None and len(operations) > self.max_operations:
                raise GeneratorError(
                    f"This specification contains {len(operations)} operations, which exceeds "
                    f"the maximum of {self.max_operations}. Use --tag or --path-filter to reduce "
                    "the number of operations."
                )
            return operations
        
        # Apply filtering
        filtered = []
        for op in operations:
            op_tags = op.get("tags", [])
            op_path = op.get("path", "")
            
            # Check tag filter: keep if any op.tags intersects with self.tags
            tag_match = False
            if self.tags:
                # If tags specified, operation must have at least one matching tag
                if op_tags and any(tag in op_tags for tag in self.tags):
                    tag_match = True
            
            # Check path filter: keep if operation path matches filter as a path segment
            path_match = False
            if self.path_filters:
                # If path_filters specified, operation path must match filter as a proper path prefix
                # This means: exact match OR filter is a parent path (followed by /)
                # E.g., filter "/users" matches "/users" and "/users/{id}" but NOT "/usersabc"
                for pf in self.path_filters:
                    # Normalize filter to ensure it starts with /
                    if not pf.startswith('/'):
                        pf = '/' + pf
                    # Match if path equals filter OR path starts with filter + "/"
                    if op_path == pf or op_path.startswith(pf + '/'):
                        path_match = True
                        break
            
            # Include operation if either filter matches (OR logic)
            # If only one filter type is specified, that filter must match
            # If both are specified, operation matches if either matches
            if self.tags and self.path_filters:
                # Both filters present: OR logic
                if tag_match or path_match:
                    filtered.append(op)
            elif self.tags:
                # Only tags filter: must match tags
                if tag_match:
                    filtered.append(op)
            elif self.path_filters:
                # Only path filter: must match path
                if path_match:
                    filtered.append(op)
        
        # Check if filtered operations exceed max_operations
        if self.max_operations is not None and len(filtered) > self.max_operations:
            raise GeneratorError(
                f"After filtering, {len(filtered)} operations remain, which exceeds "
                f"the maximum of {self.max_operations}. Use --tag or --path-filter to further "
                "reduce the number of operations."
            )
        
        return filtered
    
    def _generate_pyproject_toml(self, output_dir: Path) -> None:
        """Generate the pyproject.toml file."""
        # Sanitize description for TOML (strip newlines, escape quotes, truncate)
        raw_description = self.spec_info.get("description", "Generated MCP server")
        sanitized_description = self._sanitize_toml_string(raw_description)
        
        context = {
            "server_name": self.server_name,
            "title": self.spec_info.get("title", "MCP Server"),
            "version": self.spec_info.get("version", "1.0.0"),
            "description": sanitized_description,
        }
        
        content = self._render_template("pyproject.toml.j2", context)
        
        pyproject_path = output_dir / "pyproject.toml"
        pyproject_path.write_text(content, encoding="utf-8")
        
        if self.verbose:
            print(f"  Generated {pyproject_path}")
    
    def _generate_requirements_txt(self, output_dir: Path) -> None:
        """Generate requirements.txt file."""
        content = """# MCP Server Dependencies
mcp>=1.0.0
httpx>=0.27.0
"""
        
        requirements_path = output_dir / "requirements.txt"
        requirements_path.write_text(content, encoding="utf-8")
        
        if self.verbose:
            print(f"  Generated {requirements_path}")
    
    def _generate_readme(self, output_dir: Path) -> None:
        """Generate README.md file."""
        context = {
            "server_name": self.server_name,
            "title": self.spec_info.get("title", "MCP Server"),
            "version": self.spec_info.get("version", "1.0.0"),
            "description": self.spec_info.get("description", ""),
            "base_url": self.base_url,
            "transport": self.transport,
            "operation_count": self.spec_info.get("operation_count", 0),
            "path_count": self.spec_info.get("path_count", 0),
            # Authentication parameters
            "api_key_env": self.api_key_env,
            "api_key_header": self.api_key_header,
            "api_key_prefix": self.api_key_prefix,
            "extra_headers": self.extra_headers,
        }
        
        content = self._render_template("README.md.j2", context)
        
        readme_path = output_dir / "README.md"
        readme_path.write_text(content, encoding="utf-8")
        
        if self.verbose:
            print(f"  Generated {readme_path}")
    
    def _generate_example_spec(self, output_dir: Path) -> None:
        """Generate an example spec file (copy of input or minimal example)."""
        # Just create a placeholder - user can use their own spec
        context = {
            "spec_source": self.spec_path,
        }
        
        content = self._render_template("example_spec_info.md.j2", context)
        
        spec_info_path = output_dir / "SPEC_INFO.md"
        spec_info_path.write_text(content, encoding="utf-8")
        
        if self.verbose:
            print(f"  Generated {spec_info_path}")
