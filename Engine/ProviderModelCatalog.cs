using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MuxSwarm.Engine;

/// <summary>Loads an OpenAI-compatible model catalog solely from the supplied active provider.</summary>
internal static class ProviderModelCatalog
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxModelIds = 20000;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>A complete ordinally sorted catalog, or an empty catalog with a safe diagnostic.</summary>
    internal sealed record Result(IReadOnlyList<string> Models, string? Error);

    /// <summary>Fetches the selected provider catalog with a ten-second deadline and no redirects.</summary>
    internal static Task<Result> LoadAsync(ProviderConfig? provider, CancellationToken ct = default)
        => LoadAsync(provider, Client, Environment.GetEnvironmentVariable, ct);

    /// <summary>Fetches a catalog using injected HTTP and environment access; caller cancellation propagates.</summary>
    internal static async Task<Result> LoadAsync(ProviderConfig? provider, HttpClient client,
        Func<string, string?> env, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (provider is null) return Fail("Select an active provider before loading models.");
        if (!TryModelsUri(provider.Endpoint, out var uri))
            return Fail("Configure a valid HTTP(S) provider endpoint without embedded credentials.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnvVar))
            {
                var key = env(provider.ApiKeyEnvVar);
                if (string.IsNullOrWhiteSpace(key))
                    return Fail("Set the provider's configured API-key environment variable before loading models.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            if (provider.Headers is not null)
            {
                foreach (var (name, template) in provider.Headers)
                {
                    // Match Common.ExpandEnvVars: unresolved placeholders stay literal. The injected
                    // resolver deliberately owns all environment access, including header expansion.
                    var value = Regex.Replace(template, @"\$\{([^}]+)\}",
                        match => env(match.Groups[1].Value) ?? match.Value);
                    if (name.Any(char.IsControl) || value.Any(c => c is '\r' or '\n'))
                        return Fail("Correct the provider's invalid HTTP header configuration.");
                    request.Headers.Remove(name);
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        return Fail("Correct the provider's invalid HTTP header configuration.");
                }
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Fail($"Provider model catalog returned HTTP {(int)response.StatusCode}. Check endpoint and authentication, or enter a model ID manually.");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                return Fail("Provider model catalog exceeds the 8 MiB response limit. Enter a model ID manually.");

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) break;
                if (body.Length + read > MaxResponseBytes)
                    return Fail("Provider model catalog exceeds the 8 MiB response limit. Enter a model ID manually.");
                body.Write(buffer, 0, read);
            }

            token.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Fail("Provider response is not an OpenAI-compatible model catalog. Check the endpoint or enter a model ID manually.");

            var models = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var item in data.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String) continue;
                var value = id.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) continue;
                if (++count > MaxModelIds)
                    return Fail("Provider model catalog exceeds the 20,000 model-ID limit. Enter a model ID manually.");
                models.Add(value);
            }

            return models.Count == 0
                ? Fail("Provider returned no usable model IDs. Enter a model ID manually or check the provider catalog.")
                : new Result(models.OrderBy(id => id, StringComparer.Ordinal).ToArray(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail("Provider model catalog request timed out. Retry or enter a model ID manually.");
        }
        catch (JsonException)
        {
            return Fail("Provider returned malformed model-catalog JSON. Check the endpoint or enter a model ID manually.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Fail("Could not reach the provider model catalog. Check connectivity or enter a model ID manually.");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return Fail("Could not load the model catalog. Check the provider endpoint and HTTP header configuration.");
        }
    }

    private static Result Fail(string error) => new(Array.Empty<string>(), error);

    /// <summary>Context-length lookup outcome: null Tokens = the provider does not expose it.</summary>
    internal sealed record ContextResult(int? Tokens, string? Error);

    /// <summary>
    /// Best-effort context-window lookup for one model from the provider's /models catalog.
    /// Providers expose the window under different keys (OpenRouter: context_length /
    /// top_provider.context_length; others: max_context_length, context_window,
    /// max_input_tokens); the first plausible positive value wins. A missing model or a
    /// catalog without the metadata returns null Tokens WITHOUT an error - callers treat
    /// that as "provider does not expose it" (explicit no-op), never a failure.
    /// </summary>
    internal static async Task<ContextResult> LoadContextLengthAsync(
        ProviderConfig? provider, string modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return new ContextResult(null, "No model selected.");
        if (provider is null) return new ContextResult(null, "Select an active provider first.");
        if (!TryModelsUri(provider.Endpoint, out var uri) || uri is null)
            return new ContextResult(null, "Configure a valid HTTP(S) provider endpoint first.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnvVar))
            {
                var key = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar);
                if (!string.IsNullOrWhiteSpace(key))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ContextResult(null, $"Provider catalog returned HTTP {(int)response.StatusCode}.");
            var body = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            if (body.Length > MaxResponseBytes) return new ContextResult(null, "Catalog response too large.");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return new ContextResult(null, null);   // not an OpenAI-shaped catalog: treat as not-exposed

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !string.Equals(id.GetString(), modelId, StringComparison.Ordinal)) continue;
                return new ContextResult(ExtractContextTokens(item), null);
            }
            return new ContextResult(null, null);       // model not listed: not exposed
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new ContextResult(null, "Provider catalog request timed out."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return new ContextResult(null, "Could not reach or parse the provider catalog.");
        }
    }

    /// <summary>Probe the known context-window keys on a catalog model object (top-level, then nested).</summary>
    internal static int? ExtractContextTokens(JsonElement model)
    {
        foreach (var key in new[] { "context_length", "max_context_length", "context_window", "max_input_tokens", "max_model_len" })
            if (model.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var n) && n > 0) return n;
        if (model.TryGetProperty("top_provider", out var tp) && tp.ValueKind == JsonValueKind.Object
            && tp.TryGetProperty("context_length", out var tpl) && tpl.ValueKind == JsonValueKind.Number
            && tpl.TryGetInt32(out var tn) && tn > 0) return tn;
        return null;
    }

    private static bool TryModelsUri(string? endpoint, out Uri? modelsUri)
    {
        modelsUri = null;
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count >= 2 && segments[^2].Equals("chat", StringComparison.OrdinalIgnoreCase) &&
            segments[^1].Equals("completions", StringComparison.OrdinalIgnoreCase))
            segments.RemoveRange(segments.Count - 2, 2);
        else if (segments.Count > 0 && (segments[^1].Equals("responses", StringComparison.OrdinalIgnoreCase) ||
                 segments[^1].Equals("completions", StringComparison.OrdinalIgnoreCase)))
            segments.RemoveAt(segments.Count - 1);
        segments.Add("models");
        modelsUri = new UriBuilder(uri)
        {
            Path = "/" + string.Join('/', segments),
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
        return true;
    }
}
