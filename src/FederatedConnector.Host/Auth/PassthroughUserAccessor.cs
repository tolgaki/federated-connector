using System.Net.Http.Headers;
using System.Text.Json;
using FederatedConnector.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace FederatedConnector.Host.Auth;

/// <summary>
/// OAuthPassthrough: Copilot holds the source's own OAuth token and sends it as a bearer token. The source is the
/// validator. If the provider declares a userinfo endpoint, it is called once per token (cached briefly) to obtain a
/// stable subject for handle binding; otherwise handles are bound to a hash of the token itself and stop working
/// when Copilot refreshes the token.
/// </summary>
public sealed class PassthroughUserAccessor(
    IHttpContextAccessor http,
    IHttpClientFactory httpClients,
    IMemoryCache cache,
    ProviderDescriptor descriptor,
    ILogger<PassthroughUserAccessor> logger) : IRequestUserAccessor
{
    public const string HttpClientName = "passthrough-userinfo";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public async ValueTask<RequestUser?> GetCurrentAsync(CancellationToken ct)
    {
        var token = BearerToken(http.HttpContext);
        if (token is null)
        {
            return null;
        }

        var tokenHash = UserHash.FromOpaqueToken(token);
        var source = new PassthroughTokenSource(token);
        if (descriptor.Auth is not AuthRequirement.OAuthPassthrough { UserInfoUrl: { } userInfo })
        {
            return new RequestUser(new UserIdentity(tokenHash), source);
        }

        if (cache.TryGetValue("ui:" + tokenHash, out UserIdentity? cached) && cached is not null)
        {
            return new RequestUser(cached, source);
        }

        var identity = await ResolveSubjectAsync(userInfo, token, ct).ConfigureAwait(false);
        if (identity is null)
        {
            return null;
        }

        cache.Set("ui:" + tokenHash, identity, CacheFor);
        return new RequestUser(identity, source);
    }

    internal static string? BearerToken(HttpContext? ctx)
    {
        var header = ctx?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private async Task<UserIdentity?> ResolveSubjectAsync(Uri userInfo, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, userInfo);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Userinfo endpoint rejected the forwarded token with status {Status}.", (int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var sub = FirstString(doc.RootElement, "sub", "id", "user_id", "account_id");
            return sub is null ? null : new UserIdentity(UserHash.FromSubject(userInfo.Host, sub));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning("Userinfo lookup failed: {Error}.", ex.GetType().Name);
            return null;
        }
    }

    private static string? FirstString(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String)
                {
                    return v.GetString();
                }

                if (v.ValueKind == JsonValueKind.Number)
                {
                    return v.GetRawText();
                }
            }
        }

        return null;
    }
}

public sealed class PassthroughTokenSource(string token) : IUserTokenSource
{
    public bool IsAvailable => true;

    public ValueTask<string> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct) => ValueTask.FromResult(token);
}
