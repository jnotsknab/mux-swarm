---
name: frontend-design
description: "Design distinctive, polished UI that avoids generic 'AI slop'. Use when making visual design decisions: typography, color, spacing, hierarchy, motion, and component composition."
---

## When to use

Use this skill whenever making visual/aesthetic decisions in a UI — choosing fonts, colors, spacing, layout, or animation. The goal is output that looks *intentional*, not like it came from a default theme.

---

## Typography

**Use a real type scale.** Pick one from modular scale (1.25× or 1.333×) or a design token set. Never just `font-size: 16px` everywhere.

```css
/* Example 1.25× scale (base 16px) */
--text-xs:   0.64rem;
--text-sm:   0.8rem;
--text-base: 1rem;
--text-lg:   1.25rem;
--text-xl:   1.563rem;
--text-2xl:  1.953rem;
--text-3xl:  2.441rem;
```

**Font pairing rules:**
- One display/heading font, one body font — not three.
- High contrast between them: geometric sans + serif, or slab + grotesque.
- Good free pairings: Inter + Fraunces, Plus Jakarta Sans + Lora, DM Sans + DM Serif.
- System stack (`ui-sans-serif, system-ui, sans-serif`) is a fine, fast default for data/utility UIs.

**Measure and line-height:**
- Body text: `max-width: 65ch`, `line-height: 1.6`.
- Headings: tighter — `line-height: 1.1–1.2`.
- Display text: `letter-spacing: -0.02em` to -0.04em for large weights.

**Weight contrast creates hierarchy:** pair `font-weight: 800` headings with `400` body. Never use `500` as your only weight.

---

## Color

**Use a 60-30-10 split:**
- 60% — base/background (neutral, near-white or near-black)
- 30% — surface and secondary UI (cards, sidebars)
- 10% — accent (one confident, intentional color)

**Palette discipline:**
- Commit to one accent hue. One. Variations can be tints/shades of it.
- Derive semantic colors (success, warning, error, info) from distinct hue families — don't reuse your accent for success AND links.
- Build with CSS custom properties so dark mode is a `[data-theme="dark"]` root swap, not a duplication of every rule.

```css
:root {
  --color-bg:       #fafaf9;
  --color-surface:  #ffffff;
  --color-border:   #e5e5e4;
  --color-text:     #1c1917;
  --color-muted:    #78716c;
  --color-accent:   #7c3aed;   /* one accent — violet */
  --color-error:    #dc2626;
  --color-success:  #16a34a;
}
[data-theme="dark"] {
  --color-bg:      #0c0a09;
  --color-surface: #1c1917;
  --color-border:  #292524;
  --color-text:    #fafaf9;
  --color-muted:   #a8a29e;
}
```

**Contrast minimums:** body text ≥ 4.5:1, large/bold text ≥ 3:1. Test with a contrast checker before shipping.

---

## Spacing & rhythm

Use a consistent spacing scale — 4px base (multiples: 4, 8, 12, 16, 24, 32, 48, 64, 96).

```css
--space-1: 0.25rem;  /* 4px  */
--space-2: 0.5rem;   /* 8px  */
--space-3: 0.75rem;  /* 12px */
--space-4: 1rem;     /* 16px */
--space-6: 1.5rem;   /* 24px */
--space-8: 2rem;     /* 32px */
```

**Generous whitespace** is not wasted space — it creates breathing room that reads as quality. When in doubt, add more padding, not less.

**Alignment grids:** align to a column grid (12-col or 8-col). Mixing 3-col and 4-col gutters in the same layout is the #1 cause of a messy feel.

---

## Visual hierarchy

Every screen needs a **primary action** and a clear read-order:

1. **Size + weight** differentiate levels — not color alone.
2. **Contrast** draws attention: high-contrast = important, low-contrast = secondary.
3. Limit to **3 levels** of hierarchy. More than 3 = visual noise.
4. Primary CTA: filled, accent color, full contrast. Secondary: outlined or ghost. Tertiary: text link only.

---

## Motion

- **Fast:** most transitions 100–200ms. Never more than 400ms for interactive feedback.
- **Eased:** use `ease-out` for elements entering, `ease-in` for exiting. `ease-in-out` for state toggles.
- **Purposeful:** animate to communicate a state change, not to decorate.
- **Always honor `prefers-reduced-motion`:**

```css
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}
```

---

## Depth: shadows and borders

Use shadows sparingly — they should communicate elevation, not decoration.

```css
--shadow-sm: 0 1px 2px 0 rgb(0 0 0 / 0.05);
--shadow-md: 0 4px 6px -1px rgb(0 0 0 / 0.1), 0 2px 4px -2px rgb(0 0 0 / 0.1);
--shadow-lg: 0 10px 15px -3px rgb(0 0 0 / 0.1);
```

One shadow level per component (card = `shadow-md`, modal = `shadow-lg`). Don't apply `shadow-lg` to every card — it flattens the hierarchy.

Borders as separators: `1px solid var(--color-border)` — use over shadows when elevation doesn't apply.

---

## Responsive layout

- Design mobile-first. Add complexity at breakpoints, don't strip it.
- CSS Grid for layout; Flexbox for component internals.
- Prefer `clamp()` for fluid typography rather than breakpoint jumps:

```css
font-size: clamp(1rem, 2.5vw, 1.25rem);
```

- Touch targets: minimum 44×44px on mobile (even if the visible element is smaller, add padding).

---

## Anti-patterns to avoid

| Anti-pattern | Instead |
|---|---|
| Default Bootstrap / MUI out-of-box look | Override typography + color tokens before writing a single component |
| Centered *everything* | Left-align body text; center sparingly (heroes, empty states) |
| Gradient-soup (5+ stop background) | One subtle gradient max, or flat color |
| Emoji as primary design elements | Use icon libraries (Lucide, Phosphor) or SVG |
| Inconsistent spacing (12px here, 15px there) | Enforce the spacing scale — 0 freehand px values |
| Walls of `font-weight: 400` | Use weight contrast to create hierarchy |
| Accent color on every interactive element | Reserve accent for the primary action per view |
