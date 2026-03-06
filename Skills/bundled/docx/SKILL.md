---
name: docx
description: Best practices for creating, editing, and manipulating Word documents (.docx files). Use when creating reports, memos, letters, templates, or any .docx output. Also use when reading, editing, or extracting content from existing Word documents.
requires_bins: [node, npm]
---

# Word Document Skill ({{os}})

## Quick Reference

| Task | Approach |
|------|----------|
| Create new document | Use `docx` npm package (JavaScript) |
| Edit existing document | Unpack → edit XML → repack |
| Read/extract content | `pandoc` or unpack for raw XML |

## Installation

```bash
npm install -g docx
```

---

## Creating New Documents

### Setup

```javascript
const fs = require('fs');
const { Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell, ImageRun,
        Header, Footer, AlignmentType, PageOrientation, LevelFormat, ExternalHyperlink,
        InternalHyperlink, Bookmark, FootnoteReferenceRun, PositionalTab,
        PositionalTabAlignment, PositionalTabRelativeTo, PositionalTabLeader,
        TabStopType, TabStopPosition, Column, SectionType,
        TableOfContents, HeadingLevel, BorderStyle, WidthType, ShadingType,
        VerticalAlign, PageNumber, PageBreak } = require('docx');

const doc = new Document({ sections: [{ children: [/* content */] }] });
Packer.toBuffer(doc).then(buffer => fs.writeFileSync("output.docx", buffer));
```

### Page Size

```javascript
// CRITICAL: docx defaults to A4 — always set explicitly
sections: [{
  properties: {
    page: {
      size: { width: 12240, height: 15840 }, // US Letter (DXA: 1440 = 1 inch)
      margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } // 1 inch margins
    }
  },
  children: [/* content */]
}]
```

| Paper | Width | Height | Content Width (1" margins) |
|-------|-------|--------|---------------------------|
| US Letter | 12,240 | 15,840 | 9,360 |
| A4 | 11,906 | 16,838 | 9,026 |

**Landscape:** Pass portrait dimensions and set orientation — docx swaps them internally:
```javascript
size: { width: 12240, height: 15840, orientation: PageOrientation.LANDSCAPE }
```

### Styles

```javascript
const doc = new Document({
  styles: {
    default: { document: { run: { font: "Arial", size: 24 } } }, // 12pt
    paragraphStyles: [
      { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 32, bold: true, font: "Arial" },
        paragraph: { spacing: { before: 240, after: 240 }, outlineLevel: 0 } },
      { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 28, bold: true, font: "Arial" },
        paragraph: { spacing: { before: 180, after: 180 }, outlineLevel: 1 } },
    ]
  },
  sections: [{ children: [] }]
});
```

### Lists — NEVER use unicode bullets

```javascript
// ❌ WRONG
new Paragraph({ children: [new TextRun("• Item")] })

// ✅ CORRECT
const doc = new Document({
  numbering: {
    config: [
      { reference: "bullets",
        levels: [{ level: 0, format: LevelFormat.BULLET, text: "•", alignment: AlignmentType.LEFT,
          style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] },
      { reference: "numbers",
        levels: [{ level: 0, format: LevelFormat.DECIMAL, text: "%1.", alignment: AlignmentType.LEFT,
          style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] },
    ]
  },
  sections: [{ children: [
    new Paragraph({ numbering: { reference: "bullets", level: 0 }, children: [new TextRun("Bullet")] }),
    new Paragraph({ numbering: { reference: "numbers", level: 0 }, children: [new TextRun("Numbered")] }),
  ]}]
});
// Same reference = continues numbering. Different reference = restarts.
```

### Tables

```javascript
// CRITICAL: Set width on both table AND each cell. Use ShadingType.CLEAR not SOLID.
const border = { style: BorderStyle.SINGLE, size: 1, color: "CCCCCC" };
const borders = { top: border, bottom: border, left: border, right: border };

new Table({
  width: { size: 9360, type: WidthType.DXA }, // US Letter content width
  columnWidths: [4680, 4680], // Must sum to table width
  rows: [new TableRow({
    children: [new TableCell({
      borders,
      width: { size: 4680, type: WidthType.DXA },
      shading: { fill: "D5E8F0", type: ShadingType.CLEAR },
      margins: { top: 80, bottom: 80, left: 120, right: 120 },
      children: [new Paragraph({ children: [new TextRun("Cell")] })]
    })]
  })]
})
// Always use WidthType.DXA — WidthType.PERCENTAGE breaks in Google Docs
```

### Images

```javascript
// CRITICAL: type is required
new Paragraph({
  children: [new ImageRun({
    type: "png",
    data: fs.readFileSync("image.png"),
    transformation: { width: 200, height: 150 },
    altText: { title: "Title", description: "Desc", name: "Name" }
  })]
})
```

### Page Breaks

```javascript
// CRITICAL: PageBreak must be inside a Paragraph
new Paragraph({ children: [new PageBreak()] })
// or
new Paragraph({ pageBreakBefore: true, children: [new TextRun("New page")] })
```

### Hyperlinks

```javascript
// External
new Paragraph({
  children: [new ExternalHyperlink({
    children: [new TextRun({ text: "Click here", style: "Hyperlink" })],
    link: "https://example.com",
  })]
})

// Internal (bookmark + reference)
new Paragraph({ heading: HeadingLevel.HEADING_1, children: [
  new Bookmark({ id: "chapter1", children: [new TextRun("Chapter 1")] }),
]})
new Paragraph({ children: [new InternalHyperlink({
  children: [new TextRun({ text: "See Chapter 1", style: "Hyperlink" })],
  anchor: "chapter1",
})]})
```

### Footnotes

```javascript
const doc = new Document({
  footnotes: {
    1: { children: [new Paragraph("Source: Annual Report 2024")] },
  },
  sections: [{ children: [new Paragraph({
    children: [new TextRun("Revenue grew 15%"), new FootnoteReferenceRun(1)]
  })]}]
});
```

### Tab Stops

```javascript
// Right-align on same line
new Paragraph({
  children: [new TextRun("Company Name"), new TextRun("\tJanuary 2025")],
  tabStops: [{ type: TabStopType.RIGHT, position: TabStopPosition.MAX }],
})
```

### Headers and Footers

```javascript
sections: [{
  headers: {
    default: new Header({ children: [new Paragraph({ children: [new TextRun("Header")] })] })
  },
  footers: {
    default: new Footer({ children: [new Paragraph({
      children: [new TextRun("Page "), new TextRun({ children: [PageNumber.CURRENT] })]
    })] })
  },
  children: []
}]
```

### Table of Contents

```javascript
// Headings must use HeadingLevel ONLY — no custom styles
new TableOfContents("Table of Contents", { hyperlink: true, headingStyleRange: "1-3" })
```

### Critical Rules

- **Set page size explicitly** — defaults to A4
- **Never use `\n`** — use separate Paragraph elements
- **Never use unicode bullets** — use `LevelFormat.BULLET`
- **PageBreak must be in Paragraph** — standalone is invalid
- **ImageRun requires `type`** — always specify png/jpg/etc
- **Tables need dual widths** — `columnWidths` array AND cell `width`
- **Always use `WidthType.DXA`** — never PERCENTAGE
- **Use `ShadingType.CLEAR`** — never SOLID for shading
- **Never use tables as dividers** — use Paragraph border instead
- **TOC requires HeadingLevel only** — no custom styles on headings
- **Override built-in styles** — use exact IDs: "Heading1", "Heading2"
- **Include `outlineLevel`** — required for TOC (0 for H1, 1 for H2)

---

## Editing Existing Documents

Unpack → edit XML → repack.

### Step 1: Unpack

```bash
# Unzip the .docx (it's a ZIP archive)
# Unix/Mac
unzip document.docx -d unpacked/

# Windows (PowerShell)
Expand-Archive document.docx -DestinationPath unpacked/
```

Edit XML files in `unpacked/word/`.

### Step 2: Edit XML

Key rules:
- Element order in `<w:pPr>`: `<w:pStyle>`, `<w:numPr>`, `<w:spacing>`, `<w:ind>`, `<w:jc>`, `<w:rPr>` last
- Add `xml:space="preserve"` to `<w:t>` with leading/trailing spaces
- RSIDs must be 8-digit hex (e.g., `00AB1234`)

**Tracked changes:**
```xml
<w:ins w:id="1" w:author="Mux" w:date="2025-01-01T00:00:00Z">
  <w:r><w:t>inserted text</w:t></w:r>
</w:ins>

<w:del w:id="2" w:author="Mux" w:date="2025-01-01T00:00:00Z">
  <w:r><w:delText>deleted text</w:delText></w:r>
</w:del>
```

### Step 3: Repack

```bash
# Unix/Mac
cd unpacked && zip -r ../output.docx .

# Windows (PowerShell)
Compress-Archive -Path unpacked\* -DestinationPath output.docx
```

---

## Reading Content

```bash
# Install pandoc (cross-platform: https://pandoc.org/installing.html)
pandoc document.docx -o output.md
```

---

## Common Pitfalls

- Don't mix manual formatting with styles
- Set column widths explicitly in tables
- Always save/write the buffer — don't leave in memory
- Test output in both Word and Google Docs if cross-compatibility matters

