---
name: csharp
description: C# and .NET 8+ coding conventions, naming standards, idiomatic patterns, CLI commands, dependency management, testing with xUnit, and XML documentation. Use when writing, reviewing, or refactoring C# code.
---

# C# Coding Guidelines

## Code Style and Naming Conventions

- **Classes, Records, Structs:** `PascalCase`
- **Interfaces:** `IPascalCase` (prefixed with 'I')
- **Methods, Properties, Events:** `PascalCase`
- **Local Variables and Parameters:** `camelCase`
- **Private/Protected Fields:** `_camelCase` (leading underscore)
- **Constants / Readonly variables:** `PascalCase` (avoid `SCREAMING_SNAKE_CASE`)
- **Braces:** Allman style (opening and closing braces on their own lines)
- **Implicit Typing (`var`):** Use `var` when type is obvious from the assignment. Use explicit type for primitives or when unclear.

## CLI Commands (.NET 8+)

```bash
# Build the project/solution (implicitly restores)
dotnet build

# Format code (uses editorconfig if present)
dotnet format

# Run tests
dotnet test

# Run the application
dotnet run

# Add a NuGet package
dotnet add package <PackageName>

# Add a project reference
dotnet add reference <ProjectPath>
```

## Dependency Management

- Rely on built-in **Dependency Injection** (`Microsoft.Extensions.DependencyInjection`).
- Register services via `AddSingleton`, `AddScoped`, or `AddTransient`.
- `HttpClient` should be injected via `IHttpClientFactory` or registered as a singleton to avoid socket exhaustion.

## Idiomatic C# Patterns

```csharp
// ✅ Records for immutable data models
public record CustomerDto(string Name, string Email);

// ✅ File-Scoped Namespaces (saves indentation)
namespace MyProject.Services;

// ✅ Global Using Directives (in GlobalUsings.cs)
global using System.Linq;
global using Microsoft.Extensions.Logging;

// ✅ Pattern Matching
if (obj is Customer { IsPremium: true } premiumCustomer)
{
    // ...
}

var discount = customer switch 
{
    { IsPremium: true } => 0.2m,
    _ => 0m
};

// ✅ LINQ for queries and transformations
var activeUsers = users.Where(u => u.IsActive).ToList();

// ✅ Async/Await patterns
public async Task<string> GetDataAsync(CancellationToken cancellationToken)
{
    // Pass CancellationToken down
    return await _httpClient.GetStringAsync(url, cancellationToken);
}
```

## Anti-patterns to Avoid

- ❌ **Primitive Obsession:** Prefer domain specific wrappers or records over raw strings/ints (e.g., `EmailAddress` record vs `string`).
- ❌ **Catching Exception blindly:** Never use `catch (Exception ex)` without logging or `throw;`. Never use `throw ex;` as it destroys stack trace.
- ❌ **Using blocks for HttpClient:** Do not wrap `HttpClient` in `using`. Register it properly or use `IHttpClientFactory`.
- ❌ **Magic numbers/strings:** Use named `const` variables or `enum`s.
- ❌ **Classic Singleton Pattern:** Don't write manual `public static MyService Instance`. Use DI.

## Testing Frameworks

- Primarily use **xUnit** (`xunit.v3` for modern testing).
- `[Fact]` for parameterless tests.
- `[Theory]` and `[InlineData]` for parameterized tests.

## XML Documentation Comments

Use triple-slash `///` XML comments for public APIs. This powers tools like Swagger/OpenAPI.

```csharp
/// <summary>
/// Calculates the total cost of an order including tax.
/// </summary>
/// <param name="orderAmount">The original base amount of the order.</param>
/// <param name="taxRate">The applicable tax rate as a decimal (e.g., 0.05 for 5%).</param>
/// <returns>The final calculated total price.</returns>
/// <exception cref="ArgumentException">Thrown when orderAmount is negative.</exception>
public decimal CalculateTotal(decimal orderAmount, decimal taxRate)
```

## Quick Checklist

- [ ] Target framework is .NET 8+
- [ ] Uses `file-scoped` namespaces
- [ ] No `using` statements for `HttpClient`
- [ ] Uses `var` appropriately
- [ ] Async methods end with `Async` suffix and accept `CancellationToken`
- [ ] Uses DI instead of static Singletons
- [ ] Code formatted with `dotnet format`
- [ ] Tests use `xUnit`
