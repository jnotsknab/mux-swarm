namespace MuxSwarm.Engine.NativeTools;

/// <summary>
/// Deterministic whitespace normalization for native Shell/REPL tool RESULTS before they enter
/// model context. Strips spaces/tabs that immediately precede a line break - the padding class
/// (PowerShell Format-Table pads every row to console width) that is pure dead-weight tokens.
/// LEADING whitespace is never touched (Python/YAML/diff/table indentation is semantic), and the
/// final UNTERMINATED line is never touched (a streaming delta can split mid-line, so its tail
/// spaces may be real content continued by the next delta). Applied once at result-render time,
/// so a given result is byte-stable across requests - prompt-cache prefixes never mutate.
/// Scope: native Shell jobs + Python REPL only; file reads and MCP results stay byte-exact.
/// </summary>
internal static class OutputWhitespace
{
    /// <summary>Gate: config <c>shell.trimOutputWhitespace</c> (default on).</summary>
    public static bool Enabled => App.Config?.Shell?.TrimOutputWhitespace ?? true;

    /// <summary>Apply the gate + normalization in one call (identity when disabled).</summary>
    public static string Apply(string text) => Enabled ? TrimLineTails(text) : text;

    /// <summary>
    /// Remove spaces/tabs immediately before each newline (CR/LF preserved as-is). Idempotent;
    /// the trailing unterminated segment is returned verbatim.
    /// </summary>
    public static string TrimLineTails(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        int lineStart = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\n') continue;
            int end = i;                                   // exclusive content end (before \n)
            bool hadCr = end > lineStart && s[end - 1] == '\r';
            if (hadCr) end--;
            int trimmed = end;
            while (trimmed > lineStart && (s[trimmed - 1] == ' ' || s[trimmed - 1] == '\t')) trimmed--;
            sb.Append(s, lineStart, trimmed - lineStart);
            if (hadCr) sb.Append('\r');
            sb.Append('\n');
            lineStart = i + 1;
        }
        sb.Append(s, lineStart, s.Length - lineStart);
        return sb.ToString();
    }
}
