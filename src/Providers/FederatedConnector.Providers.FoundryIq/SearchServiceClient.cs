using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FederatedConnector.Abstractions;

namespace FederatedConnector.Providers.FoundryIq;

/// <summary>
/// Thin, read-only client for the Azure AI Search data plane. Every user-scoped call carries the service credential
/// in <c>Authorization</c> and the user's token in <c>x-ms-query-source-authorization</c>, so Search trims results.
/// Source response bodies are never surfaced in exceptions.
/// </summary>
internal sealed class SearchServiceClient(HttpClient http, FoundryIqOptions options)
{
    public const string UserTokenHeader = "x-ms-query-source-authorization";

    private Uri Endpoint => options.Endpoint ?? throw new ProviderException(ProviderErrorKind.Unavailable, "Foundry IQ endpoint is not configured.");

    public Task<JsonDocument> GetKnowledgeBaseAsync(string kb, ProviderContext ctx, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Url($"knowledgebases/{Escape(kb)}"), null, ctx, userScoped: false, ct);

    public Task<JsonDocument> GetKnowledgeSourceAsync(string ks, ProviderContext ctx, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Url($"knowledgesources/{Escape(ks)}"), null, ctx, userScoped: false, ct);

    public Task<JsonDocument> GetIndexAsync(string index, ProviderContext ctx, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Url($"indexes/{Escape(index)}"), null, ctx, userScoped: false, ct);

    public Task<JsonDocument> RetrieveAsync(string kb, JsonObject body, ProviderContext ctx, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, Url($"knowledgebases/{Escape(kb)}/retrieve", options.UsePreviewApi), body, ctx, userScoped: true, ct);

    public Task<JsonDocument> SearchDocumentsAsync(string index, JsonObject body, ProviderContext ctx, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, Url($"indexes/{Escape(index)}/docs/search"), body, ctx, userScoped: true, ct);

    /// <summary>GET a citationUrl verbatim. The caller must have validated that it points at the configured endpoint.</summary>
    public Task<JsonDocument> GetCitationAsync(Uri citationUrl, ProviderContext ctx, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, citationUrl, null, ctx, userScoped: true, ct);

    private Uri Url(string path, bool preview = false)
    {
        var version = preview ? options.PreviewApiVersion : options.ApiVersion;
        return new Uri(Endpoint, $"{path}?api-version={Uri.EscapeDataString(version)}");
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private async Task<JsonDocument> SendAsync(HttpMethod method, Uri url, JsonObject? body, ProviderContext ctx, bool userScoped, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, url);
        var serviceToken = await ctx.ServiceCredentials.GetTokenAsync([options.ServiceScope], ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        if (userScoped)
        {
            // Obligation 2: never query user-restricted data without the user's identity.
            if (!ctx.UserToken.IsAvailable)
            {
                throw new ProviderException(ProviderErrorKind.Auth, "No user token available for a user-scoped Search call.");
            }

            var userToken = await ctx.UserToken.GetTokenAsync([options.UserScope], ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(userToken))
            {
                throw new ProviderException(ProviderErrorKind.Auth, "Empty user token.");
            }

            request.Headers.TryAddWithoutValidation(UserTokenHeader, userToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(ProviderErrorKind.Timeout, "Search request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ProviderErrorKind.Unavailable, "Search request failed: " + ex.HttpRequestError, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw MapStatus(response.StatusCode, method, url);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new ProviderException(ProviderErrorKind.Unavailable, "Search returned malformed JSON.", ex);
            }
        }
    }

    private static ProviderException MapStatus(HttpStatusCode status, HttpMethod method, Uri url)
    {
        var kind = status switch
        {
            HttpStatusCode.Unauthorized => ProviderErrorKind.Auth,
            HttpStatusCode.Forbidden => ProviderErrorKind.Forbidden,
            HttpStatusCode.NotFound => ProviderErrorKind.NotFound,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ProviderErrorKind.Timeout,
            _ => ProviderErrorKind.Unavailable,
        };
        return new ProviderException(kind, $"Search {method} {url.AbsolutePath} returned {(int)status}.") { SourceStatus = (int)status };
    }
}
