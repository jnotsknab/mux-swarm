using System.Net.Http;
using System.Text.Json;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// OpenAI Codex / ChatGPT-plan subscription OAuth provider. Constants + body/parse shapes ported from
/// router-for-me/CLIProxyAPI internal/auth/codex/*. Unlike Claude, the token exchange + refresh bodies
/// are FORM-ENCODED (application/x-www-form-urlencoded). The token response includes an id_token JWT;
/// the ChatGPT account id is extracted from its claim "https://api.openai.com/auth".chatgpt_account_id
/// (parsed WITHOUT signature verification). The Codex Responses-API request shape + headers + the
/// required instructions preamble live in the request path (M4), not here.
/// </summary>
internal sealed class CodexOAuthProvider : IOAuthProvider
{
    public string Id => "codex";
    public string DisplayName => "ChatGPT (Codex / GPT-5.x subscription)";
    public string AuthorizeUrl => "https://auth.openai.com/oauth/authorize";
    public string TokenUrl => "https://auth.openai.com/oauth/token";
    public string ClientId => "app_EMoamEEZ73f0CkXaXp7hrann";
    public int RedirectPort => 1455;
    public string RedirectPath => "/auth/callback";
    public string RedirectUri => $"http://localhost:{RedirectPort}{RedirectPath}";
    public string Scope => "openid email profile offline_access";

    public IReadOnlyList<string> StaticModels => new[] { "gpt-5", "gpt-5-codex" };

    public IEnumerable<KeyValuePair<string, string>> ExtraAuthorizeParams() => new[]
    {
        new KeyValuePair<string, string>("prompt", "login"),
        new KeyValuePair<string, string>("id_token_add_organizations", "true"),
        new KeyValuePair<string, string>("codex_cli_simplified_flow", "true"),
    };

    public HttpContent BuildTokenExchangeBody(string code, string state, string codeVerifier) =>
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier,
        });

    public HttpContent BuildRefreshBody(string refreshToken) =>
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = "openid profile email",   // NOTE: refresh scope differs from authorize (order + no offline_access)
        });

    public OAuthTokens ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var t = new OAuthTokens
        {
            Provider = Id,
            AccessToken = GetString(r, "access_token") ?? "",
            RefreshToken = GetString(r, "refresh_token"),
            IdToken = GetString(r, "id_token"),
        };
        if (r.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out int secs))
            t.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(secs);
        if (!string.IsNullOrEmpty(t.IdToken))
            (t.AccountId, t.Email) = ParseIdToken(t.IdToken!);
        return t;
    }

    /// <summary>
    /// Decode the id_token JWT payload (middle segment, base64url, re-padded) WITHOUT signature
    /// verification and pull chatgpt_account_id from the namespaced "https://api.openai.com/auth" claim,
    /// plus the email claim. Mirrors CLIProxyAPI jwt_parser.go.
    /// </summary>
    private static (string? accountId, string? email) ParseIdToken(string jwt)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return (null, null);
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
            byte[] bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            string? accountId = null;
            if (root.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
                accountId = GetString(auth, "chatgpt_account_id");
            string? email = GetString(root, "email");
            return (accountId, email);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
