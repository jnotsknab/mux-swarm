---
name: docx
description: Best practices for creating, editing, and manipulating Word documents (.docx files) using Python. Use when creating reports, memos, letters, templates, or any .docx output. Also use when reading, editing, or extracting content from existing Word documents.
requires_bins: [uv]
---

# Word Document Skill (Python)

## Quick Reference

| Task | Approach |
|------|----------|
| Create new document | Use `python-docx` package |
| Edit existing document | Open with `python-docx`, modify paragraphs/tables, and save |
| Read/extract content | Iterate through `document.paragraphs` and `document.tables` |

## Installation

```bash
pip install python-docx
# or with uv
uv pip install python-docx
```

---

## Creating New Documents

### Setup

```python
from docx import Document
from docx.shared import Inches, Pt, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH

# Create a new document
doc = Document()

# Save the document
doc.save('output.docx')
```

### Page Size and Margins

```python
# python-docx defaults to 8.5 x 11 inches (US Letter)
section = doc.sections[0]

# Change to A4 size
section.page_width = Inches(8.27)
section.page_height = Inches(11.69)

# Set margins
section.top_margin = Inches(1.0)
section.bottom_margin = Inches(1.0)
section.left_margin = Inches(1.0)
section.right_margin = Inches(1.0)
```

### Headings and Paragraphs

```python
# Add headings
doc.add_heading('Document Title', level=0) # Title
doc.add_heading('Heading 1', level=1)
doc.add_heading('Heading 2', level=2)

# Add a normal paragraph
p = doc.add_paragraph('This is a normal paragraph. ')

# Add formatted text to the same paragraph using "runs"
run = p.add_run('This text is bold. ')
run.bold = True

run2 = p.add_run('This text is italicized.')
run2.italic = True

# Paragraph alignment
p.alignment = WD_ALIGN_PARAGRAPH.CENTER
```

### Lists

```python
# Bulleted list
doc.add_paragraph('First item', style='List Bullet')
doc.add_paragraph('Second item', style='List Bullet')

# Numbered list
doc.add_paragraph('Step one', style='List Number')
doc.add_paragraph('Step two', style='List Number')
```

### Tables

```python
# Create a table with 3 rows and 3 columns
table = doc.add_table(rows=3, cols=3)
table.style = 'Table Grid' # Adds borders

# Populate header row
hdr_cells = table.rows[0].cells
hdr_cells[0].text = 'Qty'
hdr_cells[1].text = 'Id'
hdr_cells[2].text = 'Desc'

# Populate data rows
data = [
    (1, '101', 'Spam'),
    (2, '102', 'Eggs'),
    (3, '103', 'Bacon')
]

for qty, id, desc in data:
    row_cells = table.add_row().cells
    row_cells[0].text = str(qty)
    row_cells[1].text = id
    row_cells[2].text = desc
```

### Images

```python
# Add an image with a specified width (height scales automatically)
doc.add_picture('image.png', width=Inches(1.25))
```

### Page Breaks

```python
doc.add_page_break()
```

### Custom Styles

```python
from docx.enum.style import WD_STYLE_TYPE

# Create a custom paragraph style
styles = doc.styles
new_style = styles.add_style('CustomStyle', WD_STYLE_TYPE.PARAGRAPH)
new_style.base_style = styles['Normal']

# Set font properties
font = new_style.font
font.name = 'Arial'
font.size = Pt(12)
font.color.rgb = RGBColor(0, 51, 102) # Dark Blue

# Apply the style
doc.add_paragraph('This paragraph uses the custom style.', style='CustomStyle')
```

---

## Editing Existing Documents

```python
# Open an existing document
doc = Document('existing.docx')

# 1. Modify existing paragraphs
for paragraph in doc.paragraphs:
    if 'old text' in paragraph.text:
        paragraph.text = paragraph.text.replace('old text', 'new text')

# 2. Append new content
doc.add_paragraph('This is a newly added paragraph at the end.')

# Save changes
doc.save('updated.docx')
```

---

## Reading/Extracting Content

```python
doc = Document('document.docx')

# Extract all text
full_text = []
for para in doc.paragraphs:
    full_text.append(para.text)
print('\n'.join(full_text))

# Extract table data
for table in doc.tables:
    for row in table.rows:
        row_data = [cell.text for cell in row.cells]
        print('\t'.join(row_data))
```

---

## Common Pitfalls

- **Paragraphs vs Runs**: A paragraph is a block of text. A run is a contiguous sequence of text within a paragraph that shares the same formatting (bold, italic, font, color). To change formatting mid-sentence, you must use multiple runs.
- **Table Column Widths**: Setting column widths in `python-docx` can be tricky because Word often overrides them based on content. To force widths, you may need to iterate through all cells in a column and set their width individually.
- **Styles**: Always check if a style exists before creating it. Trying to create a style that already exists will throw a `ValueError`. Use `style in doc.styles` to check.
