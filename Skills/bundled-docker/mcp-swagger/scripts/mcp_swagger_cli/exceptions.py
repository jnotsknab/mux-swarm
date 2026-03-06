"""Custom exceptions for MCP Swagger CLI."""


class MCPSwaggerError(Exception):
    """Base exception for MCP Swagger CLI."""
    pass


class SpecNotFoundError(MCPSwaggerError):
    """Raised when a spec file or URL cannot be found."""
    pass


class SpecParseError(MCPSwaggerError):
    """Raised when a spec cannot be parsed."""
    pass


class SpecValidationError(MCPSwaggerError):
    """Raised when a spec fails validation."""
    pass


class GeneratorError(MCPSwaggerError):
    """Raised when server generation fails."""
    pass


class TemplateError(MCPSwaggerError):
    """Raised when template rendering fails."""
    pass
