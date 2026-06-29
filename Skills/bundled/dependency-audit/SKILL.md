---
name: dependency-audit
description: Find vulnerable and outdated dependencies across ecosystems (Python, Node, .NET, Rust, Go). Use when auditing a project for CVEs, preparing a release, or investigating a security report.
requires_bins: []
---

## When to use
- Pre-release security audit
- Investigating a reported CVE in a dependency
- Routine hygiene: find outdated/unpinned packages
- Multi-language monorepo sweeps

---

## Quick commands by ecosystem

### Python — pip-audit
```bash
# Install (uv preferred)
uv tool install pip-audit

# Audit current environment
pip-audit

# Audit a requirements file without installing
pip-audit -r requirements.txt

# JSON output for scripting
pip-audit --format json -r requirements.txt
```

### Node — npm audit + outdated
```bash
# Audit for known CVEs
npm audit

# Fix automatically (minor/patch)
npm audit fix

# Show outdated versions (current / wanted / latest)
npm outdated

# pnpm / yarn equivalents
pnpm audit
yarn audit
```

### Multi-ecosystem — osv-scanner (recommended cross-language)
```bash
# Install
go install github.com/google/osv-scanner/cmd/osv-scanner@latest
# or: brew install osv-scanner

# Scan a directory (auto-detects lockfiles)
osv-scanner --recursive .

# Scan a specific lockfile
osv-scanner --lockfile package-lock.json
osv-scanner --lockfile requirements.txt

# JSON output
osv-scanner --format json --recursive .
```
osv-scanner covers: npm, PyPI, Go, Cargo, Maven, NuGet, RubyGems, Pub.

### .NET — dotnet list package
```bash
# Vulnerable packages (including transitive)
dotnet list package --vulnerable --include-transitive

# Outdated packages
dotnet list package --outdated

# Per-project in a solution
dotnet list MyApp.csproj package --vulnerable --include-transitive
```

### Rust — cargo audit
```bash
cargo install cargo-audit
cargo audit

# With JSON output
cargo audit --json
```

### Go — govulncheck
```bash
go install golang.org/x/vuln/cmd/govulncheck@latest

# Scan current module
govulncheck ./...

# Scan a binary
govulncheck -mode binary ./myapp
```

---

## Reading severity

| Field | Meaning |
|-------|---------|
| CRITICAL / HIGH | Patch immediately; block release if exploitable |
| MODERATE | Schedule fix within sprint |
| LOW | Informational; fix in batch |
| GHSA-xxxx / CVE-xxxx | Cross-reference at https://osv.dev for full detail |

Focus on **directly imported** packages first. Transitive CVEs only matter if the vulnerable code path is reachable.

---

## Remediation

**Pin to a safe version** (fastest):
```bash
# Python
pip-audit --fix          # auto-applies safe upgrades to requirements.txt
uv pip install "package>=safe_version"

# Node
npm install package@safe_version

# .NET
dotnet add package PackageName --version safe_version

# Rust
# Edit Cargo.toml, then:
cargo update -p package_name --precise safe_version

# Go
go get package@safe_version
go mod tidy
```

**Check transitive exposure**: if the vulnerability is in a transitive dep and the direct dep hasn't released a fix yet, check whether your code actually calls the vulnerable symbol — if not, document and defer.
