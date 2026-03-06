"""
MCP Swagger CLI - Generate MCP servers from Swagger/OpenAPI specifications.

This module provides a CLI tool to convert Swagger/OpenAPI specs into
runnable MCP (Model Context Protocol) servers.
"""

__version__ = "0.1.0"
__author__ = "MCP Swagger CLI Team"
__license__ = "MIT"

from mcp_swagger_cli.cli import app

__all__ = ["app"]
