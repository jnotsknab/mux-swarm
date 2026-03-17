# Contributing to Mux-Swarm

Thank you for your interest in contributing to Mux-Swarm. This document covers everything you need to get started - from setting up a local build to getting your changes merged.

## Prerequisites

- [Git](https://git-scm.com/)
- [.NET SDK](https://dotnet.microsoft.com/download) compatible with `net10.0`
- [Node.js / npm](https://nodejs.org/) (`npx` required for MCP servers)
- [uv / uvx](https://docs.astral.sh/uv/) for Python-based MCP servers

## Getting Started

1. **Fork the repository** on GitHub.
2. **Clone your fork** locally:

   ```bash
   git clone https://github.com/jnotsknab/mux-swarm.git
   cd mux-swarm
   ```

3. **Create a branch** for your work. Always branch off `development` - never commit directly to `main` or `development`:

   ```bash
   git checkout development
   git pull origin development
   git checkout -b your-branch-name
   ```

   Use a descriptive branch name, for example: `fix-memory-persistence`, `feature-docker-healthcheck`, `docs-setup-clarification`.

4. **Build and verify** the project compiles:

   ```bash
   dotnet build
   ```

5. **Run from source** to confirm everything works:

   ```bash
   dotnet run --project Mux-Swarm.csproj
   ```

## Branch Model

| Branch | Purpose | Who can push |
|--------|---------|--------------|
| `main` | Stable releases only. All changes require a PR with passing CI. | No one directly - PR only. |
| `development` | Active development branch. Integration target for all contributions. | Maintainers only. |
| `your-branch-name` | Your feature or fix. Branched from `development`. | You. |

**Important:** Contributor branches are deleted after their PR is merged. Keep your branches focused on a single change - this makes review faster and keeps the history clean.

## Making Changes

- Keep changes focused. One PR per feature or fix.
- Follow the existing code style and conventions in the project.
- If you're adding a new agent role, MCP server integration, or skill, include the relevant config examples and documentation.
- If your change affects the CLI interface (new commands, new flags), update the README accordingly.

## Cross-Platform Builds

Mux-Swarm targets Windows, Linux, and macOS. CI runs builds across all three platforms automatically. If you want to verify locally before pushing, the publish commands are:

**Windows:**
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

**Linux:**
```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

**macOS:**
```bash
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

You don't need to test all three locally - CI handles that. But if your change involves platform-specific behavior (filesystem paths, process spawning, Docker integration), try to verify on at least the platforms you have access to.

## Submitting a Pull Request

1. **Push your branch** to your fork:

   ```bash
   git push origin your-branch-name
   ```

2. **Open a Pull Request** on GitHub targeting the `development` branch.

3. **CI runs automatically.** Your PR must pass all status checks (build across Windows, Linux, and macOS) before it can be merged. If a build fails, check the Actions tab for logs.

4. **Respond to review feedback.** A maintainer will review your PR. Be open to suggestions - the goal is to ship quality changes together.

5. **After merge**, your branch will be deleted. If you want to contribute again, create a new branch from an up-to-date `development`.

## What Makes a Good Contribution

Contributions don't have to be large to be valuable. Here are some areas where help is welcome:

- **Bug fixes** - especially around cross-platform edge cases.
- **Documentation** - clarifying setup steps, adding examples, fixing typos.
- **Swarm templates** - community-contributed `swarm.json` configurations for specific use cases.
- **Skills** - reusable skill modules that agents can load at runtime.
- **MCP server integrations** - new tool integrations or improvements to existing ones.
- **Test coverage** - unit tests and integration tests are actively being expanded.

## Reporting Issues

If you find a bug or have a feature request, open an issue on GitHub. Include:

- What you expected to happen.
- What actually happened.
- Steps to reproduce (if applicable).
- Your OS, .NET SDK version, and any relevant config snippets.

## Code of Conduct

Be respectful, constructive, and collaborative. We're building something useful together - treat fellow contributors the way you'd want to be treated.

## License

By contributing to Mux-Swarm, you agree that your contributions will be licensed under the [Apache License 2.0](LICENSE).