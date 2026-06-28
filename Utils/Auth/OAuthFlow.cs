using System.Net;
using System.Net.Http;

namespace MuxSwarm.Utils.Auth;

/// <summary>Terminal OAuth refresh failure (e.g. invalid_grant / revoked). Callers QUARANTINE the credential.</summary>
internal sealed class OAuthRefreshException(HttpStatusCode status, string body)
    : Exception($"OAuth refresh failed ({(int)status}): {Trim(body)}")
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>Whether a retry could plausibly succeed (5xx / 429), vs a terminal 4xx (dead refresh token).</summary>
    public bool Retryable { get; } = status == (HttpStatusCode)429 || (int)status >= 500;

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// Provider-independent OAuth orchestration: runs the browser PKCE login (authorize -&gt; loopback catch
/// -&gt; code exchange) and the refresh_token grant. The provider supplies only the URLs/constants and the
/// body/parse shapes (<see cref="IOAuthProvider"/>); this drives the flow once for all providers.
/// </summary>
internal sealed class OAuthFlow(HttpClient http)
{
    private readonly HttpClient _http = http;

    /// <summary>
    /// Run the full interactive login for <paramref name="provider"/>. Generates PKCE+state, opens the
    /// browser (or surfaces the URL for manual paste via <paramref name="showUrl"/>), waits for the
    /// loopback callback, validates state, and exchanges the code for tokens.
    /// </summary>
    public async Task<OAuthTokens> LoginAsync(IOAuthProvider provider, Action<string> showUrl, CancellationToken ct)
    {
        var pkce = Pkce.Generate();
        string state = Pkce.NewState();

        var q = System.Web.HttpUtility.ParseQueryString(string.Empty);
        q["code"] = "true";                       // matches the reference authorize params
        q["client_id"] = provider.ClientId;
        q["response_type"] = "code";
        q["redirect_uri"] = provider.RedirectUri;
        q["scope"] = provider.Scope;
        q["code_challenge"] = pkce.Challenge;
        q["code_challenge_method"] = "S256";
        q["state"] = state;
        foreach (var kv in provider.ExtraAuthorizeParams()) q[kv.Key] = kv.Value;
        string authUrl = $"{provider.AuthorizeUrl}?{q}";

        // Start the loopback catcher BEFORE opening the browser so we never miss a fast redirect.
        var callbackTask = LoopbackCallback.WaitForAsync(provider.RedirectPort, provider.RedirectPath, ct);

        if (!BrowserLauncher.TryOpen(authUrl))
            showUrl(authUrl);     // headless: tell the caller to print the URL for manual paste
        else
            showUrl(authUrl);     // also show it (so a wrong default browser can be worked around)

        var cb = await callbackTask.ConfigureAwait(false);
        if (!string.IsNullOrEmpty(cb.Error))
            throw new InvalidOperationException($"OAuth error from provider: {cb.Error}");
        if (string.IsNullOrEmpty(cb.Code))
            throw new InvalidOperationException("No authorization code was returned.");
        // The reference allows the provider to carry state in the code fragment; accept either source.
        string effectiveState = string.IsNullOrEmpty(cb.State) ? state : cb.State!;
        if (!string.IsNullOrEmpty(cb.State) && cb.State != state)
            throw new InvalidOperationException("OAuth state mismatch (possible CSRF) - login aborted.");

        using var content = provider.BuildTokenExchangeBody(cb.Code, effectiveState, pkce.Verifier);
        using var resp = await _http.PostAsync(provider.TokenUrl, content, ct).ConfigureAwait(false);
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({(int)resp.StatusCode}): {json}");

        var tokens = provider.ParseTokenResponse(json);
        tokens.Provider = provider.Id;
        tokens.LastRefresh = DateTimeOffset.UtcNow;
        tokens.Dead = false;
        return tokens;
    }

    /// <summary>
    /// Refresh <paramref name="current"/> using the refresh_token grant. Providers often do NOT return a
    /// new refresh token on refresh - the old one is preserved. Throws <see cref="OAuthRefreshException"/>
    /// on failure so the manager can quarantine a terminal (4xx) failure.
    /// </summary>
    public async Task<OAuthTokens> RefreshAsync(IOAuthProvider provider, OAuthTokens current, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
            throw new InvalidOperationException("No refresh token available; re-login required.");

        using var content = provider.BuildRefreshBody(current.RefreshToken!);
        using var resp = await _http.PostAsync(provider.TokenUrl, content, ct).ConfigureAwait(false);
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new OAuthRefreshException(resp.StatusCode, json);

        var refreshed = provider.ParseTokenResponse(json);
        refreshed.Provider = provider.Id;
        refreshed.LastRefresh = DateTimeOffset.UtcNow;
        refreshed.Dead = false;
        // Preserve durable fields the refresh response may omit.
        if (string.IsNullOrEmpty(refreshed.RefreshToken)) refreshed.RefreshToken = current.RefreshToken;
        if (string.IsNullOrEmpty(refreshed.AccountId)) refreshed.AccountId = current.AccountId;
        if (string.IsNullOrEmpty(refreshed.Email)) refreshed.Email = current.Email;
        return refreshed;
    }
}
