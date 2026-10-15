using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FederatedConnector.Abstractions;

namespace FederatedConnector.Host.Auth;

/// <summary>The user and credential for the current request, resolved by the auth mode.</summary>
public sealed record RequestUser(UserIdentity Identity, IUserTokenSource Token);

/// <summary>Resolves the calling user for the current request. Returns null when no valid user credential exists.</summary>
public interface IRequestUserAccessor
{
    ValueTask<RequestUser?> GetCurrentAsync(CancellationToken ct);
}

public static class UserHash
{
    /// <summary>Stable, non-reversible user id: base64url(SHA-256(tid:oid)), first 128 bits.</summary>
    public static string FromEntra(string tenantId, string objectId) =>
        Hash("entra:" + tenantId.ToLowerInvariant() + ":" + objectId.ToLowerInvariant());

    public static string FromSubject(string issuerOrSource, string subject) => Hash("sub:" + issuerOrSource + ":" + subject);

    public static string FromOpaqueToken(string token) => Hash("tok:" + token);

    /// <summary>Hash used for telemetry dimensions (tenant ids etc.).</summary>
    public static string ForTelemetry(string? value) => value is null ? "none" : Hash("tel:" + value)[..12];

    private static string Hash(string value) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

public static class EntraClaims
{
    private const string ScopeClaim = "http://schemas.microsoft.com/identity/claims/scope";
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaim = "http://schemas.microsoft.com/identity/claims/tenantid";

    public static string? ObjectId(ClaimsPrincipal p) => p.FindFirst("oid")?.Value ?? p.FindFirst(ObjectIdClaim)?.Value;

    public static string? TenantId(ClaimsPrincipal p) => p.FindFirst("tid")?.Value ?? p.FindFirst(TenantIdClaim)?.Value;

    public static IEnumerable<string> Scopes(ClaimsPrincipal p) =>
        p.FindAll("scp").Concat(p.FindAll(ScopeClaim))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>True for a delegated (user) token that carries <paramref name="requiredScope"/>. App-only tokens fail.</summary>
    public static bool HasDelegatedScope(ClaimsPrincipal p, string requiredScope)
    {
        if (p.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (string.Equals(p.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ObjectId(p) is not null && Scopes(p).Contains(requiredScope, StringComparer.Ordinal);
    }

    public static string? Name(ClaimsPrincipal p) => p.FindFirst("name")?.Value;
}

/// <summary>No user credential (auth mode None).</summary>
public sealed class NoUserTokenSource : IUserTokenSource
{
    public static NoUserTokenSource Instance { get; } = new();

    public bool IsAvailable => false;

    public ValueTask<string> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct) =>
        throw new ProviderException(ProviderErrorKind.Auth, "No user credential is available in auth mode None.");
}

public sealed class AnonymousUserAccessor : IRequestUserAccessor
{
    private static readonly RequestUser Anonymous = new(UserIdentity.Anonymous, NoUserTokenSource.Instance);

    public ValueTask<RequestUser?> GetCurrentAsync(CancellationToken ct) => ValueTask.FromResult<RequestUser?>(Anonymous);
}
