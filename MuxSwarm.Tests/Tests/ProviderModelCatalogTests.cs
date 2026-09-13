using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Offline provider catalog contract tests; every HTTP request uses an in-memory handler.</summary>
public class ProviderModelCatalogTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => send(request, ct);
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static ProviderConfig Provider(string endpoint = "https://chosen.example/custom/v2")
        => new() { Endpoint = endpoint };

    /// <summary>Normalization retains the selected provider authority/path without inventing v1.</summary>
    [Theory]
    [InlineData("https://chosen.example/custom/v2/chat/completions/?secret=hidden#fragment", "https://chosen.example/custom/v2/models")]
    [InlineData("https://chosen.example/custom/v2/responses", "https://chosen.example/custom/v2/models")]
    [InlineData("https://chosen.example/custom/v2/completions", "https://chosen.example/custom/v2/models")]
    [InlineData("http://chosen.example:4321/", "http://chosen.example:4321/models")]
    [InlineData("https://chosen.example/custom/v2/CHAT/COMPLETIONS", "https://chosen.example/custom/v2/models")]
    public async Task NormalizesOnlyPassedEndpoint(string endpoint, string expected)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(expected, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json("{\"data\":[{\"id\":\"model\"}]}"));
        }));
        var result = await ProviderModelCatalog.LoadAsync(Provider(endpoint), client, _ => null);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "model" }, result.Models);
    }

    /// <summary>Injected environment values supply bearer auth and configured headers override it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticationAndHeaderExpansion(bool overrideAuthorization)
    {
        var provider = Provider();
        provider.ApiKeyEnvVar = "KEY";
        provider.Headers = new() { ["X-Client"] = "prefix-${CLIENT}-${UNSET}" };
        if (overrideAuthorization) provider.Headers["authorization"] = "Bearer ${OVERRIDE}";
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(overrideAuthorization ? "Bearer replacement" : "Bearer original", request.Headers.Authorization!.ToString());
            Assert.Equal("prefix-test-${UNSET}", request.Headers.GetValues("X-Client").Single());
            return Task.FromResult(Json("{\"data\":[{\"id\":\"model\"}]}"));
        }));
        var result = await ProviderModelCatalog.LoadAsync(provider, client,
            name => name switch { "KEY" => "original", "CLIENT" => "test", "OVERRIDE" => "replacement", _ => null });
        Assert.Null(result.Error);
    }

    /// <summary>IDs are ordinally distinct/sorted, not trimmed or inferred from model names.</summary>
    [Fact]
    public async Task PreservesActualIdsAndSkipsInvalidEntries()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(
            "{\"data\":[{\"id\":\"z\"},{\"id\":\"A\"},{\"id\":\"a\"},{\"id\":\"A\"},{\"id\":\" spaced \"},{\"id\":\" \"},{\"id\":\"bad\\n\"},{\"id\":null},{\"id\":3},{},false]}"))));
        var result = await ProviderModelCatalog.LoadAsync(Provider(), client, _ => null);
        Assert.Null(result.Error);
        Assert.Equal(new[] { " spaced ", "A", "a", "z" }, result.Models);
    }

    /// <summary>Invalid configuration never reaches the HTTP transport.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("file:///private/file")]
    [InlineData("ftp://chosen.example")]
    [InlineData("https://user:secret@chosen.example")]
    public async Task RejectsInvalidEndpoints(string? endpoint)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Transport must not run")));
        var result = await ProviderModelCatalog.LoadAsync(new ProviderConfig { Endpoint = endpoint }, client, _ => null);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Models);
        Assert.DoesNotContain("secret", result.Error!);
    }

    /// <summary>Missing provider or configured key fails before sending a request.</summary>
    [Fact]
    public async Task MissingProviderOrKeyFailsLocally()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Transport must not run")));
        Assert.NotNull((await ProviderModelCatalog.LoadAsync(null, client, _ => null)).Error);
        var provider = Provider();
        provider.ApiKeyEnvVar = "KEY";
        Assert.NotNull((await ProviderModelCatalog.LoadAsync(provider, client, _ => null)).Error);
    }

    /// <summary>Malformed/unsupported and empty responses return actionable errors, never partial catalogs.</summary>
    [Theory]
    [InlineData("not json secret")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":[]}")]
    public async Task RejectsMalformedOrEmptyResponses(string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
        var result = await ProviderModelCatalog.LoadAsync(Provider(), client, _ => null);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("secret", result.Error!);
        Assert.Empty(result.Models);
    }

    /// <summary>HTTP errors disclose only numeric status, including redirects, not remote text or URLs.</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(302)]
    [InlineData(500)]
    public async Task SafeHttpErrors(int status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json("secret body", (HttpStatusCode)status))));
        var result = await ProviderModelCatalog.LoadAsync(Provider("https://chosen.example/?secret=query"), client, _ => null);
        Assert.Contains(status.ToString(), result.Error!);
        Assert.DoesNotContain("secret", result.Error!);
        Assert.DoesNotContain("chosen.example", result.Error!);
        Assert.Empty(result.Models);
    }

    /// <summary>Transport diagnostics cannot leak secrets into UI error messages.</summary>
    [Fact]
    public async Task SafeNetworkError()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("secret URL and key")));
        var result = await ProviderModelCatalog.LoadAsync(Provider(), client, _ => null);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("secret", result.Error!);
    }

    /// <summary>Caller cancellation remains cancellation rather than a friendly error.</summary>
    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProviderModelCatalog.LoadAsync(Provider(), client, _ => null, cts.Token));
    }

    /// <summary>Non-caller cancellation is reported as a bounded request timeout.</summary>
    [Fact]
    public async Task TimeoutIsFriendly()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("secret")));
        var result = await ProviderModelCatalog.LoadAsync(Provider(), client, _ => null);
        Assert.Contains("timed out", result.Error!);
    }

    /// <summary>Catalog size limits reject oversized responses instead of silently truncating.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOversizedCatalogs(bool bytes)
    {
        var body = bytes ? new string(' ', 8 * 1024 * 1024 + 1)
            : "{\"data\":[" + string.Join(",", Enumerable.Repeat("{\"id\":\"m\"}", 20001)) + "]}";
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
        var result = await ProviderModelCatalog.LoadAsync(Provider(), client, _ => null);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Models);
    }
}
