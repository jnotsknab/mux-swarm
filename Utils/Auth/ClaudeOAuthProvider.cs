using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// Claude (Anthropic) Max/Pro subscription OAuth provider. Constants + body/parse shapes ported from
/// router-for-me/CLIProxyAPI internal/auth/claude/*. The token exchange + refresh bodies are JSON (the
/// Anthropic OAuth endpoint expects application/json, NOT form-encoded - unlike Codex). The mandatory
/// Claude-Code request headers and system-prompt spoof live in the request path (M3), not here; this
/// type only owns login + token lifecycle.
/// </summary>
internal sealed class ClaudeOAuthProvider : IOAuthProvider
{
    public string Id => "claude";
    public string DisplayName => "Claude (Max/Pro subscription)";
    public string AuthorizeUrl => "https://claude.ai/oauth/authorize";
    public string TokenUrl => "https://api.anthropic.com/v1/oauth/token";
    public string ClientId => "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public int RedirectPort => 54545;
    public string RedirectPath => "/callback";
    public string RedirectUri => $"http://localhost:{RedirectPort}{RedirectPath}";
    public string Scope => "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    // Anthropic exposes a native model list; the picker can enumerate it with the OAuth bearer.
    public IReadOnlyList<string> StaticModels => new[]
    {
        "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5",
    };

    public HttpContent BuildTokenExchangeBody(string code, string state, string codeVerifier)
    {
        // The reference splits a "code#state" fragment; our LoopbackCallback already separates them, but
        // mirror the reference body exactly (JSON).
        var body = new Dictionary<string, object?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = state,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier,
        };
        return JsonContent(body);
    }

    public HttpContent BuildRefreshBody(string refreshToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        };
        return JsonContent(body);
    }

    public OAuthTokens ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var t = new OAuthTokens
        {
            Provider = Id,
            AccessToken = GetString(r, "access_token") ?? "",
            RefreshToken = GetString(r, "refresh_token"),
        };
        if (r.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out int secs))
            t.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(secs);
        if (r.TryGetProperty("account", out var acct) && acct.ValueKind == JsonValueKind.Object)
            t.Email = GetString(acct, "email_address");
        return t;
    }

    private static HttpContent JsonContent(Dictionary<string, object?> body)
    {
        string json = JsonSerializer.Serialize(body);
        var c = new StringContent(json, Encoding.UTF8, "application/json");
        return c;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
