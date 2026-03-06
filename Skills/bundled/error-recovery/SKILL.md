---
name: error-recovery
description: Patterns for diagnosing failures, reading error output, adapting approach, and recovering from errors without repeating the same mistake.
---

# Error Recovery Skill

Use this skill when a tool call fails, a script errors out, a command returns an unexpected result, or any step in your task doesn't produce the expected outcome.

## First Response to Any Error

1. **Read the entire error message** — don't just look at the last line. The root cause is often earlier in the output.
2. **Identify the error type** before acting:
   - Syntax error → fix the code
   - Missing dependency → install via `uv pip install` in the active venv
   - Permission denied → check paths and access
   - Timeout → simplify the operation or break into smaller steps
   - File not found → verify the path exists
   - Connection refused → check if the service is running
3. **Never retry the exact same command that just failed.** If it failed once, it will fail again. Change something.

## Diagnosing Script Errors

When a Python or Node script fails:

1. Check the line number in the traceback
2. Look for the actual error type: `ImportError`, `FileNotFoundError`, `SyntaxError`, `TypeError`, etc.
3. Common fixes:
   - `ModuleNotFoundError` → package not installed in the active venv. Install with `uv pip install <package>` and retry.
   - `FileNotFoundError` → the path doesn't exist. Verify the path using the Filesystem MCP before running the script.
   - `SyntaxError` → read the file back, find the bad line, fix it.
   - `PermissionError` → the path may be outside allowed paths or read-only. Check config allowed paths.

## Diagnosing Tool Failures

When an MCP tool returns an error:

1. Read the error message — it usually tells you exactly what's wrong
2. Check if you're passing the correct argument types and formats
3. Verify prerequisites: does the file exist? Is the directory accessible? Is the service running?
4. Try a simpler version of the same operation to isolate the problem

## Recovery Strategies

### Fix and Retry
The error is clear and the fix is obvious. Make the specific fix and retry.
- Typo in code → fix the typo
- Wrong argument → correct the argument
- Missing directory → create it first

### Simplify and Retry
The operation is too complex and failing in an unclear way. Break it down.
- Large script failing → split into smaller scripts that each do one thing
- Complex command → break into sequential simpler commands
- Big file operation → process in batches

### Pivot Approach
The current approach fundamentally won't work. Try a different method entirely.
- Library doesn't support the feature → use a different library
- Tool can't handle the input → preprocess the input differently
- Missing dependencies → install them in the venv and retry

### Abort and Report
After 2-3 genuine attempts with different approaches, stop and report the failure clearly.
- State what you tried
- State what the error was each time
- State what you think the root cause is
- Suggest what might fix it (even if you can't do it yourself)

## Error Output Best Practices

When reporting errors back:
- Include the exact error message — don't paraphrase
- Include which tool or command produced it
- Include what you changed between attempts
- Don't flood with full stack traces — extract the relevant lines

## Common Pitfalls

- Don't retry the same failing command hoping for a different result
- Don't ignore error messages and try something completely unrelated
- Don't spiral into increasingly complex workarounds — simplify instead
- Don't hide failures — if something broke, report it clearly
- Don't burn all iterations on one approach — if it fails twice, pivot
- Don't blame the tools — the error is almost always in your input
