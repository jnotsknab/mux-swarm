---
name: ux-ui
description: "Design usable, accessible, humane interfaces — interaction patterns, component states, forms, feedback, and WCAG compliance. Use when making decisions about how UI behaves, not just how it looks."
---

## When to use

Use this skill when designing or auditing how an interface *works* — navigation, forms, error states, accessibility, feedback timing, copy tone. Pairs with `frontend-design` (which covers visual aesthetics); this skill covers behavior and usability.

---

## Information hierarchy & progressive disclosure

Show users only what they need at the current step. **Progressive disclosure** reduces cognitive load:

- Surface the 20% of features 80% of users need. Bury the rest behind "Advanced" or a secondary panel.
- One primary action per screen. Secondary actions visually subordinate.
- **Breadcrumbs** for deep hierarchies; **back button** always available and predictable.
- Modals for focused, interruptible tasks only — not as a shortcut for fitting more onto a page.

---

## Nielsen's 10 heuristics (apply these as a checklist)

1. **Visibility of system status** — always show what's happening (loading indicators, progress, confirmation).
2. **Match the real world** — use the user's language, not system jargon.
3. **User control and freedom** — undo, cancel, easy exit from any flow.
4. **Consistency and standards** — same words mean the same thing everywhere; follow platform conventions.
5. **Error prevention** — design to prevent errors before they happen (confirm destructive actions; disable invalid options).
6. **Recognition over recall** — surface options; don't make users memorize paths.
7. **Flexibility and efficiency** — keyboard shortcuts, saved preferences for power users; don't penalize novices.
8. **Aesthetic and minimalist design** — every element that isn't necessary competes for attention.
9. **Help users recognize, diagnose, and recover from errors** — plain language, specific, constructive.
10. **Help and documentation** — contextual help at the point of need, not only in a manual.

---

## Accessibility (WCAG AA minimum)

**Semantic HTML first** — the single highest-ROI accessibility investment:

```html
<!-- Not this -->
<div class="button" onclick="submit()">Submit</div>

<!-- This -->
<button type="submit">Submit</button>
```

Use `<nav>`, `<main>`, `<header>`, `<footer>`, `<section aria-labelledby>`, `<h1>`–`<h6>` in order. ARIA only fills gaps semantic HTML can't — `role="status"`, `aria-live="polite"` for dynamic updates, `aria-expanded` for toggles.

**Keyboard navigation:**
- Every interactive element reachable and operable via Tab/Enter/Space/arrow keys.
- Logical tab order (matches visual order).
- Modals: trap focus inside while open; return focus to trigger on close.
- Skip-to-content link at page top (visually hidden until focused).

**Focus states:** never `outline: none` without a replacement. Custom focus rings should be at least 2px solid, high-contrast:

```css
:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
}
```

**Color and contrast:**
- Text on background: ≥ 4.5:1 (AA). Large text (18px+ or 14px+ bold): ≥ 3:1.
- Never convey information by color alone — always pair with icon, text, or pattern.

**Alt text:** descriptive for informative images; `alt=""` for decorative. For complex charts, add a prose summary.

**Reduced motion:** always honor `prefers-reduced-motion` (see `frontend-design` skill for the CSS snippet).

---

## Every component needs these states

Never ship a component that's missing states — missing states are the #1 source of broken-looking UIs:

| State | What to show |
|---|---|
| **Empty** | Friendly illustration or message + a clear call to action |
| **Loading / skeleton** | Skeleton that matches the final layout's proportions (not a spinner alone) |
| **Error** | Inline, specific, actionable message — what failed and what to do next |
| **Success** | Confirmation that the action completed (toast, inline check, updated state) |
| **Disabled** | Visually muted + `disabled` attribute + `title`/tooltip explaining why |
| **Hover / focus** | Distinct from default; focus must be keyboard-accessible |
| **Active / pressed** | Brief visual depression (scale 0.98 or shade shift) |

For lists specifically: empty state must never be a blank space. Show "No results" + help text.

---

## Affordances & feedback

- Interactive elements must **look** interactive (contrast, cursor change, visible boundary or underline for links).
- Feedback within **100ms** feels instant. 100–1000ms needs a spinner. >1s needs a progress bar or estimated time.
- Destructive actions (delete, overwrite) need confirmation — but use a **confirmation dialog**, not disabling the button after click (user doesn't know why nothing happened).
- Use **optimistic UI** for fast, reversible actions (like, bookmark): update immediately, roll back on error.

---

## Micro-interactions

- Button press → **100ms** visual acknowledgment.
- Form field blur → **150ms** inline validation.
- Toast/snackbar → **4–6s** auto-dismiss (pause on hover), positioned non-blocking (bottom-right or top-right).
- Skeleton → content: match dimensions exactly so the layout doesn't jump.
- Page transition: **150–250ms** fade or slide — subtle, never decorative.

---

## Forms

Good form design dramatically reduces abandonment:

- **Label every field** — always above the input, never placeholder-only.
- **Inline validation on blur**, not only on submit. Show success state (`✓`) for completed fields.
- **Error messages:** specific ("Password must be at least 8 characters") not generic ("Invalid input").
- **Group related fields** visually (address block, card details).
- Error recovery: **preserve user input** — never clear a form on error.
- `type="email"`, `type="tel"`, `type="number"` — use correct types for mobile keyboard optimization.
- Autofocus the first field in isolated flows (login, modal forms).
- Required fields: mark optional ones (`optional`) rather than every required one with `*`.

```html
<label for="email">Email address</label>
<input
  id="email"
  type="email"
  autocomplete="email"
  aria-describedby="email-error"
  aria-invalid="true"
/>
<p id="email-error" role="alert">Enter a valid email address.</p>
```

---

## Perceived performance

Users perceive <100ms as instant. Every technique below buys subjective speed:

- **Skeleton screens** instead of spinners for content that has known structure.
- **Optimistic updates** for actions that rarely fail.
- **Lazy-load** below-fold images and routes.
- Show **partial content** as it arrives rather than waiting for the full payload.
- Avoid layout shift (CLS) — reserve space for images and async content.

---

## Mobile & touch

- **Minimum tap target: 44×44px** (WCAG 2.5.5). Use padding to expand small visual elements.
- Thumb-zone design: primary actions in the lower third of the screen.
- Swipe gestures: discoverable via visual affordance (drag handle, visible scroll) — never the only way to perform an action.
- `touch-action: manipulation` to remove the 300ms tap delay on clickable elements.
- Test on a real device; browser DevTools mobile emulation misses touch feel and real viewport quirks.

---

## Copywriting

The interface's words are part of the UX:

- **Error messages:** "Something went wrong" is useless. Say what failed and what to do: "We couldn't save your changes. Check your connection and try again."
- **Button labels:** verb + noun ("Save draft", "Delete account") not just "OK" or "Submit".
- **Empty state copy:** tell the user what they'll find here and how to get started.
- **Confirmation dialogs:** restate the consequence ("Delete 'Project Alpha'? This cannot be undone."), not "Are you sure?".
- Tone: direct, human, no jargon, no passive voice.

---

## Anti-patterns

| Anti-pattern | Fix |
|---|---|
| Validate only on submit | Validate on blur per field; show errors inline |
| Missing empty/error/loading states | Design all 6 states before calling a component done |
| Low contrast text ("it looks lighter, it's elegant") | Run a contrast check; elegance doesn't override readability |
| Disabled buttons with no explanation | Tooltip or inline copy explaining why; or let users click and show the error |
| Tiny tap targets on mobile | 44px minimum; use padding |
| Placeholder text as the only label | Labels above inputs, always |
| Modal-heavy flows for non-interruptive content | Use inline panels, drawers, or dedicated pages |
| `aria-*` on everything | Use semantic HTML first; ARIA fills the gaps only |
