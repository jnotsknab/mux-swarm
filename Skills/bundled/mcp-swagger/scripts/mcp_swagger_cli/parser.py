"""Parser module for Swagger/OpenAPI specifications."""

import json
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import httpx
import yaml
from prance import BaseParser, ResolvingParser
from prance.util.resolver import RESOLVE_HTTP, RESOLVE_FILES

from mcp_swagger_cli.exceptions import (
    SpecNotFoundError,
    SpecParseError,
    SpecValidationError,
)


class OpenAPIParser:
    """Parser for Swagger/OpenAPI specifications."""
    
    def __init__(
        self,
        spec_path: str,
        validate: bool = True,
        resolve_refs: bool = True,
    ) -> None:
        """Initialize the parser with a spec path.
        
        Args:
            spec_path: URL or file path to the spec
            validate: Whether to validate the spec
            resolve_refs: Whether to resolve JSON references
        """
        self.spec_path = spec_path
        self.validate = validate
        self.resolve_refs = resolve_refs
        self._spec: dict[str, Any] = {}
        self._parser: BaseParser | ResolvingParser | None = None
        self._load_spec()
    
    def _load_spec(self) -> None:
        """Load and parse the specification."""
        # Determine if it's a URL or file
        parsed = urlparse(self.spec_path)
        
        if parsed.scheme in ("http", "https"):
            self._load_from_url(self.spec_path)
        else:
            self._load_from_file(self.spec_path)
    
    def _load_from_url(self, url: str) -> None:
        """Load spec from a URL."""
        try:
            response = httpx.get(url, timeout=30.0)
            response.raise_for_status()
            
            # Determine format from content-type or URL
            content_type = response.headers.get("content-type", "")
            if "yaml" in content_type or url.endswith((".yaml", ".yml")):
                self._spec = yaml.safe_load(response.text)
            else:
                self._spec = response.json()
                
        except httpx.HTTPError as e:
            raise SpecParseError(f"Failed to fetch spec from URL: {e}")
        except json.JSONDecodeError as e:
            raise SpecParseError(f"Invalid JSON in spec: {e}")
        except yaml.YAMLError as e:
            raise SpecParseError(f"Invalid YAML in spec: {e}")
    
    def _load_from_file(self, path: str) -> None:
        """Load spec from a file."""
        file_path = Path(path)
        
        if not file_path.exists():
            raise SpecNotFoundError(f"Spec file not found: {path}")
        
        try:
            with open(file_path, "r", encoding="utf-8") as f:
                if file_path.suffix in (".yaml", ".yml"):
                    self._spec = yaml.safe_load(f)
                else:
                    self._spec = json.load(f)
        except json.JSONDecodeError as e:
            raise SpecParseError(f"Invalid JSON in spec file: {e}")
        except yaml.YAMLError as e:
            raise SpecParseError(f"Invalid YAML in spec file: {e}")
        except OSError as e:
            raise SpecParseError(f"Failed to read spec file: {e}")
    
    @property
    def spec(self) -> dict[str, Any]:
        """Get the parsed specification."""
        return self._spec
    
    def get_spec_info(self) -> dict[str, Any]:
        """Extract useful information from the spec."""
        info = self._spec.get("info", {})
        
        # Determine OpenAPI version
        openapi_version = self._spec.get("openapi") or self._spec.get("swagger", "")
        
        # Get paths
        paths = self._spec.get("paths", {})
        
        # Count operations
        operations = []
        for path, path_item in paths.items():
            if isinstance(path_item, dict):
                for method in ["get", "post", "put", "delete", "patch", "options", "head"]:
                    if method in path_item:
                        operations.append((path, method, path_item[method]))
        
        # Get schemas - handle both OpenAPI 3.0 (components/schemas) and 2.0 (definitions)
        components = self._spec.get("components", {}) or self._spec.get("definitions", {})
        schemas = components.get("schemas", components) if isinstance(components, dict) else {}
        
        # Convert schema keys to list if it's a dict
        if isinstance(schemas, dict):
            schema_list = list(schemas.keys())
        else:
            schema_list = []
        
        # Get servers - handle both OpenAPI 3.x and Swagger 2.0
        servers = self._spec.get("servers", [])
        server_urls = []
        if servers:
            for server in servers:
                if isinstance(server, dict):
                    server_urls.append(server.get("url", ""))
                else:
                    server_urls.append(str(server))
        else:
            # Swagger 2.0 uses host and basePath
            host = self._spec.get("host", "")
            base_path = self._spec.get("basePath", "/")
            schemes = self._spec.get("schemes", ["https"])
            if host:
                scheme = schemes[0] if schemes else "https"
                server_urls.append(f"{scheme}://{host}{base_path}")
        
        # Group paths by tag
        paths_by_tag: dict[str, list[tuple[str, list[str]]]] = {}
        for path, method, operation in operations:
            tags = operation.get("tags", ["default"])
            for tag in tags:
                if tag not in paths_by_tag:
                    paths_by_tag[tag] = []
                # Check if this path+method combo already exists
                found = False
                for i, (p, methods) in enumerate(paths_by_tag[tag]):
                    if p == path:
                        methods.append(method)
                        found = True
                        break
                if not found:
                    paths_by_tag[tag].append((path, [method]))
        
        return {
            "title": info.get("title", "Untitled API"),
            "version": info.get("version", "1.0.0"),
            "description": info.get("description", ""),
            "openapi_version": openapi_version,
            "path_count": len(paths),
            "operation_count": len(operations),
            "schema_count": len(schema_list),
            "paths": list(paths.keys()),
            "schemas": schema_list,
            "servers": server_urls,
            "paths_by_tag": paths_by_tag,
        }
    
    def get_operations(self) -> list[dict[str, Any]]:
        """Get all operations from the spec with metadata."""
        operations = []
        paths = self._spec.get("paths", {})
        
        for path, path_item in paths.items():
            if not isinstance(path_item, dict):
                continue
            
            for method in ["get", "post", "put", "delete", "patch", "options", "head"]:
                if method not in path_item:
                    continue
                
                operation = path_item[method]
                
                # Get path-level parameters (for both 2.0 and 3.0)
                path_params = path_item.get("parameters", [])
                operation_id = operation.get("operationId")
                
                # Generate operationId if not present
                if not operation_id:
                    method_part = method.upper()
                    path_part = path.strip("/").replace("/", "_").replace("-", "_").replace("{", "").replace("}", "")
                    operation_id = f"{method_part}_{path_part}"
                
                # Merge path-level and operation-level parameters
                parameters = path_params + operation.get("parameters", [])
                params_list = []
                
                # FIX Issue 2: Filter body params from params_list (only add non-body params)
                for param in parameters:
                    # Skip body parameters - they will be handled separately as request_body
                    if param.get("in") == "body":
                        continue
                    
                    # Handle $ref in parameters (OpenAPI 3.0 components/parameters)
                    if "$ref" in param:
                        param = self._resolve_parameter_ref(param) or param
                    
                    # Handle $ref in schema within parameter - resolve BEFORE reading type
                    schema = param.get("schema", {})
                    if "$ref" in schema:
                        schema = self._resolve_schema_ref(schema)
                        param = {**param, "schema": schema}
                    
                    # Handle oneOf/anyOf in schema (OpenAPI 3.0)
                    if "oneOf" in schema or "anyOf" in schema:
                        # Flatten to first option for now (basic support)
                        if "oneOf" in schema and schema["oneOf"]:
                            first = schema["oneOf"][0]
                            if "$ref" in first:
                                first = self._resolve_schema_ref(first)
                            schema = {**schema, **first}
                            del schema["oneOf"]
                        elif "anyOf" in schema and schema["anyOf"]:
                            first = schema["anyOf"][0]
                            if "$ref" in first:
                                first = self._resolve_schema_ref(first)
                            schema = {**schema, **first}
                            del schema["anyOf"]
                        param = {**param, "schema": schema}
                    
                    # Now read type AFTER resolution
                    type_val = schema.get("type", param.get("type", "string")) if schema else param.get("type", "string")
                    
                    params_list.append({
                        "name": param.get("name"),
                        "in": param.get("in"),
                        "required": param.get("required", False),
                        "type": type_val,
                        "description": param.get("description", ""),
                        "default": schema.get("default", param.get("default")) if schema else param.get("default"),
                        "enum": schema.get("enum", param.get("enum")) if schema else param.get("enum"),
                    })
                
                # FIX Issue 3: Resolve requestBody $ref in OpenAPI 3.x
                request_body = None
                if "requestBody" in operation:
                    rb = operation["requestBody"]
                    content = rb.get("content", {})
                    if "application/json" in content:
                        json_content = content["application/json"]
                        schema = json_content.get("schema", {})
                        # Resolve $ref if present in requestBody schema
                        if "$ref" in schema:
                            schema = self._resolve_schema_ref(schema)
                        request_body = {
                            "required": rb.get("required", False),
                            "description": rb.get("description", ""),
                            "schema": schema,
                        }
                
                # Handle body parameters (OpenAPI 2.0 style: in: body)
                for param in parameters:
                    if param.get("in") == "body":
                        # Also resolve $ref in body param schema
                        schema = param.get("schema", {})
                        if "$ref" in schema:
                            schema = self._resolve_schema_ref(schema)
                        request_body = {
                            "required": param.get("required", False),
                            "description": param.get("description", ""),
                            "schema": schema,
                        }
                        break
                
                # Get responses
                responses = {}
                for status, response in operation.get("responses", {}).items():
                    responses[status] = {
                        "description": response.get("description", ""),
                        "schema": response.get("content", {}).get("application/json", {}).get("schema"),
                    }
                
                # Get tags
                tags = operation.get("tags", ["default"])
                
                operations.append({
                    "path": path,
                    "method": method,
                    "operation_id": operation_id,
                    "summary": operation.get("summary", ""),
                    "description": operation.get("description", ""),
                    "tags": tags,
                    "deprecated": operation.get("deprecated", False),
                    "parameters": params_list,
                    "request_body": request_body,
                    "responses": responses,
                    "security": operation.get("security", []),
                })
        
        return operations
    
    def _resolve_parameter_ref(self, param: dict[str, Any]) -> dict[str, Any] | None:
        """Resolve a $ref to a parameter definition."""
        ref = param.get("$ref", "")
        if not ref:
            return param
        
        # Parse the reference path
        if ref.startswith("#/components/parameters/"):
            param_name = ref.split("/")[-1]
            components = self._spec.get("components", {})
            params = components.get("parameters", {})
            return params.get(param_name, param)
        elif ref.startswith("#/parameters/"):
            # Swagger 2.0 style
            param_name = ref.split("/")[-1]
            params = self._spec.get("parameters", {})
            return params.get(param_name, param)
        
        return param
    
    def _resolve_schema_ref(self, schema: dict[str, Any]) -> dict[str, Any]:
        """Resolve a $ref to a schema definition."""
        ref = schema.get("$ref", "")
        if not ref:
            return schema
        
        # Parse the reference path
        if ref.startswith("#/components/schemas/"):
            schema_name = ref.split("/")[-1]
            components = self._spec.get("components", {})
            schemas = components.get("schemas", {})
            return schemas.get(schema_name, schema)
        elif ref.startswith("#/definitions/"):
            # Swagger 2.0 style
            schema_name = ref.split("/")[-1]
            definitions = self._spec.get("definitions", {})
            return definitions.get(schema_name, schema)
        
        return schema
    
    def get_schemas(self) -> dict[str, dict[str, Any]]:
        """Get all schemas from the spec."""
        # Handle both OpenAPI 3.0 (components/schemas) and 2.0 (definitions)
        components = self._spec.get("components", {}) or self._spec.get("definitions", {})
        
        if isinstance(components, dict):
            return components.get("schemas", components)
        
        return {}
    
    def get_servers(self) -> list[dict[str, Any]]:
        """Get server definitions from the spec."""
        return self._spec.get("servers", [{"url": self._spec.get("host", "")}])
    
    def get_security_schemes(self) -> dict[str, dict[str, Any]]:
        """Get security schemes from the spec."""
        components = self._spec.get("components", {})
        if isinstance(components, dict):
            return components.get("securitySchemes", {})
        return {}


def parse_spec(spec_path: str, validate: bool = True) -> OpenAPIParser:
    """Parse a Swagger/OpenAPI specification.
    
    Args:
        spec_path: URL or file path to the spec
        validate: Whether to validate the spec
        
    Returns:
        OpenAPIParser instance
        
    Raises:
        SpecNotFoundError: If spec file not found
        SpecParseError: If spec cannot be parsed
        SpecValidationError: If spec validation fails
    """
    return OpenAPIParser(spec_path=spec_path, validate=validate)
