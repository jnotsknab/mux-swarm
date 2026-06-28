using System.Text.Json.Serialization;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// Universal, provider-agnostic OAuth credential record. Mirrors CLIProxyAPI's "metadata bag" idea: a
/// single record every provider normalizes into, persisted by <see cref="AuthCredentialStore"/>. The
/// durable secret here is <see cref="RefreshToken"/>; <see cref="AccessToken"/> is short-lived and
/// re-minted via refresh. This is serialized to the restricted per-provider auth file - NEVER to the
/// synced config and NEVER logged.
/// </summary>
internal sealed class OAuthTokens
{
    /// <summary>Provider key this credential belongs to ("claude", "codex").</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    /// <summary>Short-lived OAuth access token (sent as the bearer on API calls).</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    /// <summary>Durable refresh token used to mint new access tokens. The one secret that must persist.</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    /// <summary>Absolute UTC expiry of the current access token (null = unknown).</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Account id where the provider requires one on requests (Codex: from the id_token JWT).</summary>
    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    /// <summary>Account email (informational; shown in setup, not required on requests).</summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>Raw id_token JWT if the provider returned one (Codex). Kept for re-deriving claims.</summary>
    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    /// <summary>UTC timestamp of the last successful refresh/exchange.</summary>
    [JsonPropertyName("last_refresh")]
    public DateTimeOffset? LastRefresh { get; set; }

    /// <summary>
    /// True when the refresh token has been QUARANTINED after a terminal refresh failure (invalid_grant /
    /// revoked). A dead credential is not replayed in a 401 loop; the next use surfaces "re-login required".
    /// Cleared on the next successful login.
    /// </summary>
    [JsonPropertyName("dead")]
    public bool Dead { get; set; }

    /// <summary>Provider-specific extra fields that do not fit the common shape.</summary>
    [JsonPropertyName("extra")]
    public Dictionary<string, string> Extra { get; set; } = new();

    /// <summary>True when the access token is missing or within <paramref name="lead"/> of expiry.</summary>
    public bool IsExpired(TimeSpan lead) =>
        string.IsNullOrEmpty(AccessToken) ||
        (ExpiresAt is { } e && DateTimeOffset.UtcNow >= e - lead);
}
