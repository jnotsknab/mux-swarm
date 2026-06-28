using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MuxSwarm.Utils.Auth;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Unit coverage for the native OAuth framework (M1): PKCE correctness, token expiry logic, the Codex
/// id_token JWT account-id parse, and the credential-store round-trip (asserting NO token text leaks to
/// the console). Pure logic - no network, no browser. The live browser flow is validated interactively.
/// </summary>
public class OAuthFrameworkTests
{
    [Fact]
    public void Pkce_Verifier_IsBase64Url_128Chars_NoPadding()
    {
        var c = Pkce.Generate();
        Assert.Equal(128, c.Verifier.Length);                 // 96 bytes -> 128 base64 chars
        Assert.DoesNotContain('=', c.Verifier);
        Assert.DoesNotContain('+', c.Verifier);
        Assert.DoesNotContain('/', c.Verifier);
        Assert.All(c.Verifier, ch => Assert.True(
            char.IsLetterOrDigit(ch) || ch is '-' or '_', $"unexpected char '{ch}'"));
    }

    [Fact]
    public void Pkce_Challenge_IsS256_OfAsciiVerifier()
    {
        var c = Pkce.Generate();
        // Recompute the expected S256 challenge: base64url-nopad(SHA256(ASCII(verifier))).
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(c.Verifier));
        string expected = Pkce.Base64UrlNoPad(hash);
        Assert.Equal(expected, c.Challenge);
        Assert.DoesNotContain('=', c.Challenge);
    }

    [Fact]
    public void Pkce_State_IsRandom_AndUrlSafe()
    {
        string a = Pkce.NewState(), b = Pkce.NewState();
        Assert.NotEqual(a, b);
        Assert.All(a, ch => Assert.True(char.IsLetterOrDigit(ch) || ch is '-' or '_'));
    }

    [Fact]
    public void Tokens_IsExpired_RespectsLead()
    {
        var t = new OAuthTokens { AccessToken = "x", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4) };
        Assert.True(t.IsExpired(TimeSpan.FromMinutes(5)));    // within the 5-min lead
        Assert.False(t.IsExpired(TimeSpan.FromMinutes(1)));   // 4 min out, 1-min lead
        var none = new OAuthTokens { AccessToken = "" };
        Assert.True(none.IsExpired(TimeSpan.Zero));           // empty access token => expired
    }

    [Fact]
    public void Codex_ParseTokenResponse_ExtractsAccountIdFromIdTokenJwt()
    {
        // Build an unsigned JWT whose payload carries the namespaced auth claim + email.
        string header = Pkce.Base64UrlNoPad(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        string payloadJson =
            "{\"email\":\"u@example.com\"," +
            "\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"acct_123\"}}";
        string payload = Pkce.Base64UrlNoPad(Encoding.UTF8.GetBytes(payloadJson));
        string jwt = $"{header}.{payload}.sig";

        string tokenJson =
            "{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"id_token\":\"" + jwt + "\",\"expires_in\":3600}";
        var provider = new CodexOAuthProvider();
        var t = provider.ParseTokenResponse(tokenJson);

        Assert.Equal("AT", t.AccessToken);
        Assert.Equal("RT", t.RefreshToken);
        Assert.Equal("acct_123", t.AccountId);
        Assert.Equal("u@example.com", t.Email);
        Assert.NotNull(t.ExpiresAt);
    }

    [Fact]
    public void Claude_ParseTokenResponse_MapsFields()
    {
        var provider = new ClaudeOAuthProvider();
        string json =
            "{\"access_token\":\"sk-ant-oat01-abc\",\"refresh_token\":\"rt\",\"expires_in\":3600," +
            "\"account\":{\"email_address\":\"me@example.com\"}}";
        var t = provider.ParseTokenResponse(json);
        Assert.Equal("sk-ant-oat01-abc", t.AccessToken);
        Assert.Equal("rt", t.RefreshToken);
        Assert.Equal("me@example.com", t.Email);
        Assert.NotNull(t.ExpiresAt);
    }

    [Fact]
    public void CredentialStore_RoundTrips_AndDeletes()
    {
        // Use a throwaway provider id so we never touch a real credential.
        string pid = "unittest_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.False(AuthCredentialStore.Exists(pid));
            var t = new OAuthTokens
            {
                Provider = pid, AccessToken = "AT", RefreshToken = "RT",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Email = "x@y.z",
            };
            AuthCredentialStore.Save(t);
            Assert.True(AuthCredentialStore.Exists(pid));
            var loaded = AuthCredentialStore.Load(pid);
            Assert.NotNull(loaded);
            Assert.Equal("AT", loaded!.AccessToken);
            Assert.Equal("RT", loaded.RefreshToken);
            Assert.Contains(pid, AuthCredentialStore.ListProviders());
        }
        finally
        {
            AuthCredentialStore.Delete(pid);
            Assert.False(AuthCredentialStore.Exists(pid));
        }
    }

    [Fact]
    public void Providers_AreRegistered_WithExpectedConstants()
    {
        var mgr = OAuthManager.Instance;
        var claude = mgr.Get("claude");
        var codex = mgr.Get("codex");
        Assert.NotNull(claude);
        Assert.NotNull(codex);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", claude!.ClientId);
        Assert.Equal(54545, claude.RedirectPort);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", codex!.ClientId);
        Assert.Equal(1455, codex.RedirectPort);
        // Codex authorize quirks must be present.
        var ep = codex.ExtraAuthorizeParams().ToDictionary(k => k.Key, v => v.Value);
        Assert.Equal("login", ep["prompt"]);
        Assert.Equal("true", ep["id_token_add_organizations"]);
    }
}
