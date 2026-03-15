---
name: pdf
description: Use when tasks involve reading, creating, or reviewing PDF files where rendering and layout matter; prefer visual checks by rendering pages and use Python tools such as reportlab, pdfplumber, and pypdf for generation and extraction.
requires_bins: [uv]
---

# PDF Skill ({{os}})

## When to Use
- Read or review PDF content where layout and visuals matter
- Create PDFs programmatically with reliable formatting
- Validate final rendering before delivery

## Setup

```bash
uv venv {{paths.base}}/pdf-venv
uv pip install reportlab pdfplumber pypdf pypdf2
```

Install Poppler for PDF rendering (required for `pdftoppm`):

**Windows:**
```powershell
# Install via winget
winget install poppler
```

**Mac:**
```bash
brew install poppler
```

**Linux:**
```bash
apt-get install -y poppler-utils
```

## Workflow

1. Prefer visual review — render PDF pages to PNGs and inspect them
2. Use `reportlab` to generate new PDFs
3. Use `pdfplumber` or `pypdf` for text extraction and quick checks — do not rely on these for layout fidelity
4. After each meaningful update, re-render pages and verify alignment, spacing, and legibility

## Example: Generate a PDF

```python
# generate.py — save to {{paths.sandbox}}/generate.py
from reportlab.lib.pagesizes import letter
from reportlab.pdfgen import canvas

c = canvas.Canvas('{{paths.sandbox}}/document.pdf', pagesize=letter)
c.setFont("Helvetica", 16)
c.drawString(72, 700, "Generated PDF Document")
c.save()
print("Saved document.pdf")
```

Run:
```bash
{{shell}} {{shell.flag}} "{{paths.base}}/pdf-venv/bin/python {{paths.sandbox}}/generate.py"
```

## Example: Extract Text from PDF

```python
# extract_text.py
import pdfplumber

with pdfplumber.open('{{paths.sandbox}}/input.pdf') as pdf:
    for page in pdf.pages:
        text = page.extract_text()
        print(text)
```

## Example: Render PDF to PNG

```bash
pdftoppm -png {{paths.sandbox}}/input.pdf {{paths.sandbox}}/page
# Outputs: page-01.png, page-02.png, etc.
```

## Path Conventions

| Purpose | Path |
|---------|------|
| Input files | `{{paths.sandbox}}/` |
| Output files | `{{paths.sandbox}}/` |
| Intermediate/temp | `{{paths.sandbox}}/tmp/` — delete when done |

## Quality Expectations

- Maintain polished visual design: consistent typography, spacing, margins, and section hierarchy
- Avoid rendering issues: clipped text, overlapping elements, broken tables, or unreadable glyphs
- Charts, tables, and images must be sharp, aligned, and clearly labeled
- Use ASCII hyphens only — avoid Unicode dashes (U+2011 etc.)
- Citations and references must be human-readable — never leave placeholder strings

## Final Checks

- Do not deliver until the latest PNG inspection shows zero visual or formatting defects
- Confirm headers/footers, page numbering, and section transitions look polished
- Remove intermediate files after final approval

## Dependencies

- **poppler-utils** — `pdftoppm` for rendering (install via system package manager)
- **reportlab** — PDF generation
- **pdfplumber** — text extraction
- **pypdf / pypdf2** — PDF manipulation
