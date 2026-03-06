---
name: markdown-report
description: Best practices for creating well-structured markdown reports, summaries, documentation, and written deliverables saved to files.
---

# Markdown Report Skill

Use this skill when producing any written deliverable that will be saved as a markdown file — research reports, summaries, documentation, analysis, meeting notes, or any structured text output.

## Document Structure

Every report should follow this skeleton:

```
# Title

> One-line summary of what this document covers and its key conclusion.

## TL;DR
- 3-5 bullet points capturing the most important findings
- A reader who only reads this section should understand the main takeaways

## Section 1: [Topic]
Body text organized by subtopic, not by source.

## Section 2: [Topic]
Continue with the next logical section.

## Conclusion
Brief synthesis — what does this all mean? What are the implications or next steps?

## Sources
- [Source Name](URL) — accessed YYYY-MM-DD
```

## Writing Guidelines

- Lead with conclusions, not process. The reader wants answers, not a journal of your research.
- Use headers liberally — a reader should be able to scan headers alone and understand the structure.
- Keep paragraphs short — 3-5 sentences max. Dense walls of text are unreadable.
- Use bold for key terms or findings on first mention.
- Use tables for comparisons — never describe tabular data in paragraph form.
- Use bullet points for lists of 3+ items — never inline them in a paragraph.

## Tables

When comparing options, features, or data points, always use a table:

```markdown
| Feature | Option A | Option B | Option C |
|---------|----------|----------|----------|
| Price   | $10/mo   | $25/mo   | Free     |
| Speed   | Fast     | Faster   | Slow     |
```

Never write "Option A costs $10/mo and is fast, while Option B costs $25/mo and is faster..." — that belongs in a table.

## Code Blocks

When including code, commands, or technical output:
- Always specify the language for syntax highlighting: ```python, ```bash, ```json
- Add a brief comment above the block explaining what it does
- Keep blocks short — extract the relevant portion, don't dump entire files

## File Naming

- Use lowercase with dashes: `quarterly-report.md`, `api-comparison.md`
- Include date for time-sensitive reports: `2026-02-20-market-analysis.md`
- Be descriptive — `report.md` is useless, `q1-2026-sales-analysis.md` is clear

## Metadata Header

For important documents, include a metadata block at the top:

```markdown
---
title: Q1 2026 Market Analysis
date: 2026-02-20
author: Mux Agent
status: final
---
```

## Common Pitfalls

- Don't bury the key finding at the bottom — put it in the TL;DR and introduction
- Don't use headers inconsistently — pick a hierarchy and stick with it
- Don't write a report that reads like a conversation transcript
- Don't skip the Sources section — always attribute where information came from
- Don't produce a report longer than necessary — conciseness is a feature
- Don't forget to save the file — always confirm the write was successful
