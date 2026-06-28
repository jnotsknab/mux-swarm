using System.Net.Http;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// The per-provider "tack-on" unit for native subscription OAuth. A new OAuth provider is added by
/// implementing this interface (constants + a few small body/parse/inject methods) and registering it
/// with <see cref="OAuthManager"/>. Everything reusable - PKCE, the loopback redirect catcher, the
/// browser launch, the code/token HTTP exchange, the refresh loop, secure token storage - is provided
/// once by the framework (see <see cref="OAuthFlow"/>, <see cref="AuthCredentialStore"/>,
/// <see cref="OAuthManager"/>), NOT per provider.
///
/// Modeled on router-for-me/CLIProxyAPI's tiny Authenticator interface (Go) ported to C#.
/// </summary>
internal interface IOAuthProvider
{
    /// <summary>Stable provider key, e.g. "claude", "codex". Used as the auth-file name + config AuthType suffix.</summary>
    string Id { get; }

    /// <summary>Human label shown in the setup picker, e.g. "Claude (Max/Pro subscription)".</summary>
    string DisplayName { get; }

    /// <summary>OAuth authorize endpoint (opened in the browser).</summary>
    string AuthorizeUrl { get; }

    /// <summary>Token endpoint - used for BOTH the authorization_code exchange and refresh_token grants.</summary>
    string TokenUrl { get; }

    /// <summary>Public OAuth client id (the official first-party CLI client id; PKCE public client, no secret).</summary>
    string ClientId { get; }

    /// <summary>Fixed loopback port the provider's redirect_uri points at (e.g. Claude 54545, Codex 1455).</summary>
    int RedirectPort { get; }

    /// <summary>Redirect path, e.g. "/callback" or "/auth/callback".</summary>
    string RedirectPath { get; }

    /// <summary>Space-delimited OAuth scope string.</summary>
    string Scope { get; }

    /// <summary>How early before expiry to proactively refresh. Default 5 minutes (matches the reference).</summary>
    TimeSpan RefreshLead => TimeSpan.FromMinutes(5);

    /// <summary>Provider quirks appended to the authorize query (e.g. Codex: prompt=login, id_token_add_organizations=true).</summary>
    IEnumerable<KeyValuePair<string, string>> ExtraAuthorizeParams() => Array.Empty<KeyValuePair<string, string>>();

    /// <summary>
    /// The redirect_uri value sent in BOTH the authorize URL and the token exchange. Must be byte-identical
    /// in both. Defaults to http://localhost:{port}{path} (the reference uses "localhost", not 127.0.0.1,
    /// in the registered redirect - the loopback listener still binds 127.0.0.1).
    /// </summary>
    string RedirectUri => $"http://localhost:{RedirectPort}{RedirectPath}";

    /// <summary>Build the HTTP body for the authorization_code -> token exchange.</summary>
    HttpContent BuildTokenExchangeBody(string code, string state, string codeVerifier);

    /// <summary>Build the HTTP body for the refresh_token grant.</summary>
    HttpContent BuildRefreshBody(string refreshToken);

    /// <summary>Normalize the provider's token JSON into the universal <see cref="OAuthTokens"/> record.</summary>
    OAuthTokens ParseTokenResponse(string json);

    /// <summary>
    /// Optional: list selectable model ids after auth (for the setup model picker). Default = the static
    /// <see cref="StaticModels"/> set (e.g. subscription providers with a fixed model list). Providers with
    /// a live /v1/models endpoint override this.
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(OAuthTokens tokens, HttpClient http, CancellationToken ct)
        => Task.FromResult(StaticModels);

    /// <summary>Fallback model list when no live enumeration is available.</summary>
    IReadOnlyList<string> StaticModels => Array.Empty<string>();
}
