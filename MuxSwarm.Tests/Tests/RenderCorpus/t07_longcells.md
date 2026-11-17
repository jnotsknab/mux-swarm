| Symptom | Likely cause | Fix |
|---|---|---|
| Columns drift right after CJK or emoji rows | Width math uses `string.Length`, not display cells | Use a cell-width function |
| `[hidden]` or [brackets] text missing | Unescaped Spectre markup | Escape before markup |
| Double-escaped `&lt;` or `\\` | The `Inline()` double-escape bug | Escape once |
| Wide table overflows or wraps mid-border | No max-width clamp | Clamp and wrap cells |
| averyveryverylongunbrokentokenwithoutanyspacesthatcannotwrapnaturallyatall | ragged | — |
