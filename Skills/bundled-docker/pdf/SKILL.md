---
name: pdf
description: Use when tasks involve reading, creating, or reviewing PDF files where rendering and layout matter. Runs inside Docker with reportlab, pdfplumber, pypdf, and poppler pre-installed.
requires_bins: [docker]
---

# PDF Skill — Docker ({{os}})

## When to Use
- Read or review PDF content where layout and visuals matter
- Create PDFs programmatically with reliable formatting
- Validate final rendering before delivery

## Setup

Build the Docker image if not already built:

```bash
{{shell}} {{shell.flag}} "docker build -t pdf-skill {{paths.skills}}/bundled-docker/pdf"
```

## Workflow

1. Prefer visual review — render PDF pages to PNGs and inspect them
2. Use `reportlab` to generate new PDFs
3. Use `pdfplumber` or `pypdf` for text extraction — do not rely on these for layout fidelity
4. After each meaningful update, re-render pages and verify alignment, spacing, and legibility

## Execution Pattern

```bash
{{shell}} {{shell.flag}} "docker run --rm -v {{paths.sandbox}}:/output -v {{paths.base}}:/workspace pdf-skill python /workspace/script.py"
```

## Path Conventions

| Context | Path |
|---------|------|
| Container input | `/workspace/` |
| Container output | `/output/` |
| Host sandbox | `{{paths.sandbox}}` |

## Example: Generate a PDF

```python
# script.py — save to {{paths.base}}/script.py
from reportlab.lib.pagesizes import letter
from reportlab.pdfgen import canvas

c = canvas.Canvas('/output/document.pdf', pagesize=letter)
c.setFont("Helvetica", 16)
c.drawString(72, 700, "Generated PDF Document")
c.save()
print("Saved document.pdf")
```

Run:
```bash
{{shell}} {{shell.flag}} "docker run --rm -v {{paths.sandbox}}:/output -v {{paths.base}}:/workspace pdf-skill python /workspace/script.py"
```

## Example: Extract Text from PDF

```python
# extract_text.py
import pdfplumber

with pdfplumber.open('/workspace/input.pdf') as pdf:
    for page in pdf.pages:
        text = page.extract_text()
        print(text)
```

## Example: Render PDF to PNG

```bash
{{shell}} {{shell.flag}} "docker run --rm -v {{paths.sandbox}}:/output -v {{paths.base}}:/workspace pdf-skill pdftoppm -png /workspace/input.pdf /output/page"
# Outputs: page-01.png, page-02.png, etc.
```

## Temp and Output Conventions

- Use `/tmp/pdfs/` for intermediate files inside container — delete when done
- Write final artifacts to `/output/` inside container
- Access output from host via `{{paths.sandbox}}`

## Dependencies (Included in pdf-skill Image)

- **System**: `poppler-utils` (includes `pdftoppm`)
- **Python**: `reportlab`, `pdfplumber`, `pypdf`, `pypdf2`

## Fallback: Base Image with Inline Install

```bash
{{shell}} {{shell.flag}} "docker run --rm -v {{paths.sandbox}}:/output -v {{paths.base}}:/workspace "appropriate container" bash -c 'apt-get update && apt-get install -y poppler-utils && pip install reportlab pdfplumber pypdf && python /workspace/script.py'"
```

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
