using System.Net.Http;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// The framework-owned OAuth coordinator: a registry of <see cref="IOAuthProvider"/> plugins plus the
/// login / get-a-valid-token / refresh / quarantine lifecycle. Provider plugins stay tiny; this owns
/// everything reusable. Mirrors CLIProxyAPI's coreauth.Manager. Tokens persist via
/// <see cref="AuthCredentialStore"/>.
///
/// Quarantine: a terminal refresh failure (4xx invalid_grant / revoked) marks the stored credential
/// Dead so it is not replayed in a 401 loop; the next access attempt surfaces a "re-login" error until
/// the user logs in again (which clears Dead).
/// </summary>
internal sealed class OAuthManager
{
    private static readonly Lazy<OAuthManager> _instance = new(() => new OAuthManager());
    public static OAuthManager Instance => _instance.Value;

    private readonly Dictionary<string, IOAuthProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new();
    private readonly OAuthFlow _flow;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private OAuthManager()
    {
        _flow = new OAuthFlow(_http);
        Register(new ClaudeOAuthProvider());
        Register(new CodexOAuthProvider());
    }

    public void Register(IOAuthProvider provider) => _providers[provider.Id] = provider;

    public IReadOnlyCollection<IOAuthProvider> Providers => _providers.Values;

    public IOAuthProvider? Get(string id) => _providers.TryGetValue(id, out var p) ? p : null;

    /// <summary>
    /// Run the interactive browser login for <paramref name="providerId"/>, persist the credential, and
    /// return it. Clears any prior quarantine. <paramref name="showUrl"/> is invoked with the authorize
    /// URL so the caller can print it (manual-paste fallback / wrong-default-browser workaround).
    /// </summary>
    public async Task<OAuthTokens> LoginAsync(string providerId, Action<string> showUrl, CancellationToken ct)
    {
        var provider = Require(providerId);
        var tokens = await _flow.LoginAsync(provider, showUrl, ct).ConfigureAwait(false);
        AuthCredentialStore.Save(tokens);
        return tokens;
    }

    /// <summary>Logout: delete the stored credential.</summary>
    public void Logout(string providerId) => AuthCredentialStore.Delete(providerId);

    /// <summary>True when a (non-dead) credential is stored for the provider.</summary>
    public bool HasValidCredential(string providerId)
    {
        var t = AuthCredentialStore.Load(providerId);
        return t is not null && !t.Dead && !string.IsNullOrEmpty(t.RefreshToken ?? t.AccessToken);
    }

    /// <summary>The stored credential for a provider (may be expired/dead), or null.</summary>
    public OAuthTokens? Peek(string providerId) => AuthCredentialStore.Load(providerId);

    /// <summary>
    /// Return a credential whose access token is currently valid, refreshing if it is within the
    /// provider's <see cref="IOAuthProvider.RefreshLead"/> of expiry. On a terminal refresh failure the
    /// credential is QUARANTINED (Dead=true, persisted) and an exception is thrown so the caller can
    /// prompt re-login. Refreshes are serialized (single-flight) to avoid refresh-token rotation races.
    /// </summary>
    public async Task<OAuthTokens> GetValidTokensAsync(string providerId, CancellationToken ct)
    {
        var provider = Require(providerId);
        var tokens = AuthCredentialStore.Load(providerId)
            ?? throw new InvalidOperationException($"No stored login for '{providerId}'. Run /setup to log in.");
        if (tokens.Dead)
            throw new InvalidOperationException($"Login for '{providerId}' is expired/revoked. Re-login via /setup.");
        if (!tokens.IsExpired(provider.RefreshLead))
            return tokens;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-load + re-check under the gate (another caller may have just refreshed).
            tokens = AuthCredentialStore.Load(providerId) ?? tokens;
            if (tokens.Dead)
                throw new InvalidOperationException($"Login for '{providerId}' is expired/revoked. Re-login via /setup.");
            if (!tokens.IsExpired(provider.RefreshLead))
                return tokens;

            try
            {
                var refreshed = await _flow.RefreshAsync(provider, tokens, ct).ConfigureAwait(false);
                AuthCredentialStore.Save(refreshed);
                return refreshed;
            }
            catch (OAuthRefreshException ex) when (!ex.Retryable)
            {
                // Terminal failure -> quarantine so we do not loop on a dead refresh token.
                tokens.Dead = true;
                AuthCredentialStore.Save(tokens);
                throw new InvalidOperationException(
                    $"Login for '{providerId}' could not be refreshed (revoked/expired). Re-login via /setup.", ex);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private IOAuthProvider Require(string id) =>
        Get(id) ?? throw new InvalidOperationException($"Unknown OAuth provider '{id}'.");
}
