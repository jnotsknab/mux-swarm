---
name: xlsx
description: Create, read, and edit Excel workbooks with formulas, formatting, charts, and template preservation. Use when working with .xlsx/.xls files, spreadsheet generation, or bulk tabular data IO.
requires_bins: [uv, python]
---

## When to use

- Generate Excel reports, invoices, or data exports from code
- Read/parse existing workbooks (preserving styles on edit)
- Add charts, conditional formatting, named ranges, or data validation
- Bulk tabular IO between pandas DataFrames and Excel

## Setup

```python
# Install in current REPL session
import subprocess
subprocess.run(["uv", "pip", "install", "openpyxl", "pandas", "et-xmlfile"], check=True)
```

## Read an existing workbook (preserve styles)

```python
from openpyxl import load_workbook

wb = load_workbook("report.xlsx")          # data_only=True to read cached formula values
ws = wb.active                             # or wb["Sheet1"]

for row in ws.iter_rows(min_row=2, values_only=True):
    print(row)                             # tuple per row

# Read a single cell
val = ws["B3"].value
```

> **Gotcha — formula vs value:** `load_workbook("f.xlsx")` returns the formula string (`=SUM(A1:A10)`).
> Use `load_workbook("f.xlsx", data_only=True)` for the last-calculated value — but only if Excel
> previously saved cached values. Fresh programmatic files have no cache; calculate manually or use
> pandas `read_excel` instead.

## Write / create a workbook

```python
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side, numbers
from openpyxl.utils import get_column_letter
import datetime

wb = Workbook()
ws = wb.active
ws.title = "Sales"

# Headers with bold + fill
header_font = Font(bold=True, color="FFFFFF")
header_fill = PatternFill("solid", fgColor="2E75B6")
for col, hdr in enumerate(["Date", "Product", "Revenue"], start=1):
    cell = ws.cell(row=1, column=col, value=hdr)
    cell.font = header_font
    cell.fill = header_fill
    cell.alignment = Alignment(horizontal="center")

# Data rows
ws.append([datetime.date(2024, 1, 15), "Widget A", 1250.50])
ws.append([datetime.date(2024, 1, 16), "Widget B", 980.00])

# Number / date formatting
for row in ws.iter_rows(min_row=2, max_row=ws.max_row):
    row[0].number_format = "YYYY-MM-DD"   # date column
    row[2].number_format = '#,##0.00'     # currency column

# Column widths
ws.column_dimensions["A"].width = 14
ws.column_dimensions["C"].width = 12

# Formula
ws["C10"] = "=SUM(C2:C9)"
ws["C10"].number_format = '#,##0.00'

wb.save("output.xlsx")
```

## Datetime gotcha

```python
# openpyxl stores datetimes as Python datetime objects — NOT strings.
# Excel serial numbers are auto-converted by openpyxl on read.
import datetime
ws["A1"] = datetime.datetime(2024, 6, 1, 9, 30)   # correct
ws["A1"].number_format = "YYYY-MM-DD HH:MM"
# BAD: ws["A1"] = "2024-06-01"  -- stored as text, not a date
```

## Pandas bulk IO

```python
import pandas as pd

# Read (returns DataFrame; engine='openpyxl' default for .xlsx)
df = pd.read_excel("data.xlsx", sheet_name="Sales", header=0)

# Write — basic (loses formatting)
df.to_excel("out.xlsx", index=False, sheet_name="Sheet1")

# Write with ExcelWriter for multi-sheet or style hooks
with pd.ExcelWriter("out.xlsx", engine="openpyxl") as writer:
    df.to_excel(writer, sheet_name="Data", index=False)
    df.describe().to_excel(writer, sheet_name="Summary")
```

## Preserve formatting when editing an existing workbook

```python
# load_workbook keeps all styles, images, charts already present
wb = load_workbook("template.xlsx")
ws = wb["Data"]

# Edit only the cells you need — do NOT recreate the sheet
ws["D5"] = 42
ws["D5"].number_format = "#,##0"

# Save to a NEW file to keep the original safe
wb.save("filled_template.xlsx")
```

> **Merged cells gotcha:** writing to a merged region's non-top-left cell raises `ValueError`.
> Check `ws.merged_cells` and only write to the top-left cell of a merged range.

```python
# Unmerge if you need to overwrite
ws.unmerge_cells("A1:C1")
ws["A1"] = "New Header"
```

## Add a chart

```python
from openpyxl.chart import BarChart, Reference

chart = BarChart()
chart.title = "Revenue by Day"
chart.y_axis.title = "Revenue"
chart.x_axis.title = "Date"

data = Reference(ws, min_col=3, min_row=1, max_row=ws.max_row)  # include header
cats = Reference(ws, min_col=1, min_row=2, max_row=ws.max_row)
chart.add_data(data, titles_from_data=True)
chart.set_categories(cats)
chart.shape = 4
ws.add_chart(chart, "E2")

wb.save("output.xlsx")
```

## Common gotchas summary

| Issue | Fix |
|---|---|
| Formula string instead of value | `load_workbook(..., data_only=True)` |
| Date stored as text | Assign `datetime.date`/`datetime.datetime`, set `number_format` |
| Writing to merged cell errors | Unmerge first, or write only to top-left cell |
| Styles lost on `to_excel` | Use `ExcelWriter` + post-process with `load_workbook` |
| Missing cached formula values | Pre-calculate in Python or open+save in Excel first |
