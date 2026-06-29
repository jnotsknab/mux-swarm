---
name: security-scan
description: Run real SAST and secret-scanning tools locally (semgrep, bandit, gitleaks, trivy). Use when auditing a codebase for vulnerabilities, exposed secrets, or vulnerable dependencies before shipping or on demand.
requires_bins: []
---

## When to use

- Pre-merge security gate on a feature branch
- Auditing a new dependency or third-party codebase
- Checking for secrets accidentally committed
- Scanning container images or IaC for CVEs

## Tools at a glance

| Tool | Best for | Language |
|---|---|---|
| **semgrep** | SAST patterns, custom rules | Any (Python, JS, Go, Java, …) |
| **bandit** | Python-specific SAST | Python only |
| **gitleaks** | Secret/credential scanning | Any (git history + working tree) |
| **trivy** | Dep CVEs, IaC misconfig, container images | Any |

---

## semgrep — Multi-language SAST

### Install
```bash
# macOS / Linux
pip install semgrep
# or
brew install semgrep
```

### Run
```bash
# Auto-select rules for detected languages (recommended starting point)
semgrep --config auto .

# OWASP Top 10 focused
semgrep --config "p/owasp-top-ten" .

# Secrets only
semgrep --config "p/secrets" .

# Python security rules
semgrep --config "p/python" .

# Specific file or directory
semgrep --config auto src/

# JSON output for programmatic triage
semgrep --config auto --json . > semgrep_results.json
```

### Interpret findings
```
severity: ERROR   → P0 — likely exploitable; fix before merge
severity: WARNING → P1 — review carefully; context-dependent
severity: INFO    → P2 — style/hygiene; triage manually
```

### Ignore a false positive (inline)
```python
return query + user_input  # nosemgrep: python.lang.security.audit.formatted-sql-query
```

---

## bandit — Python SAST

### Install
```bash
pip install bandit
```

### Run
```bash
# Scan a package tree
bandit -r src/

# Include severity + confidence filters (HIGH only)
bandit -r src/ -l -i          # -l = high severity, -i = high confidence

# JSON report
bandit -r src/ -f json -o bandit_report.json

# Skip a test ID
bandit -r src/ --skip B101   # B101 = assert_used (common false positive in tests)
```

### Key finding IDs to prioritize

| ID | Issue |
|---|---|
| B102 | `exec` / `eval` with user input |
| B106 | Hardcoded password argument |
| B201 | Flask debug=True |
| B301/B302 | `pickle` deserialization |
| B501–B509 | Weak SSL/TLS config |
| B601–B608 | Shell injection / SQL injection |

---

## gitleaks — Secret scanning

### Install
```bash
# macOS
brew install gitleaks

# Linux / Windows (download release binary)
# https://github.com/gitleaks/gitleaks/releases
```

### Run
```bash
# Scan entire git history (catches secrets committed then "deleted")
gitleaks detect --source . --report-format json --report-path gitleaks.json

# Scan working tree only (no git history)
gitleaks detect --no-git --source . --report-format json --report-path gitleaks.json

# Pre-commit gate (exits 1 if leak found)
gitleaks protect --staged
```

### Baseline an existing repo (suppress old findings)
```bash
# Create baseline on current state
gitleaks detect --source . --baseline-path gitleaks-baseline.json --report-path gitleaks-baseline.json

# Future scans only report NEW leaks
gitleaks detect --source . --baseline-path gitleaks-baseline.json
```

> **Gotcha:** gitleaks scans git-tracked content. Untracked files need `--no-git`.
> Rotate any real credential found — assume it was exfiltrated the moment it hit version control.

---

## trivy — Dependency CVEs + IaC + containers

### Install
```bash
# macOS
brew install aquasecurity/trivy/trivy

# Linux
curl -sfL https://raw.githubusercontent.com/aquasecurity/trivy/main/contrib/install.sh | sh -s -- -b /usr/local/bin
```

### Run

```bash
# Scan filesystem for dependency vulns + IaC misconfig
trivy fs .

# Python requirements / lock files only
trivy fs --scanners vuln requirements.txt

# Severity filter (CRITICAL + HIGH only)
trivy fs --severity CRITICAL,HIGH .

# Container image
trivy image python:3.11-slim

# JSON report
trivy fs --format json --output trivy_report.json .

# Exit 1 on CRITICAL (CI gate)
trivy fs --exit-code 1 --severity CRITICAL .
```

### Reading trivy output
```
CRITICAL → P0: known exploitable CVE; patch immediately
HIGH     → P1: serious; patch before shipping
MEDIUM   → P2: assess exploitability in your context
LOW/UNKNOWN → P3: informational; track but don't block
```

---

## Triage workflow

1. **Run all four** on the target repo/dir.
2. **Deduplicate**: semgrep + bandit often flag the same Python issue; keep one finding.
3. **Classify each finding**:
   - Reachable from untrusted input? → P0/P1
   - Dependency CVE with available fix? → P1
   - Secret in git history? → P0, rotate NOW
   - False positive / test code? → suppress with inline comment or `.semgrepignore`/`.gitleaksignore`
4. **Output a prioritized list** (P0 → P1 → P2) with file:line, tool, and remediation note.
5. **Re-run after fixes** to confirm findings resolved.

## Suppress false positives

```bash
# semgrep: .semgrepignore (same syntax as .gitignore)
tests/
vendor/

# bandit: inline
subprocess.call(cmd, shell=True)  # nosec B603

# gitleaks: .gitleaksignore (list of fingerprints from report)
echo "abc123fingerprint" >> .gitleaksignore

# trivy: .trivyignore
CVE-2023-12345
```
