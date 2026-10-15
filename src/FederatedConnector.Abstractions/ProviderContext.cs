using Microsoft.Extensions.Configuration;

namespace FederatedConnector.Abstractions;

/// <summary>Everything a provider receives from the host for one call.</summary>
public sealed class ProviderContext
{
    public ProviderContext(
        UserIdentity user,
        IUserTokenSource userToken,
        IServiceCredentialSource serviceCredentials,
        IContentExtractor extractor,
        IConfiguration settings)
    {
        User = user ?? throw new ArgumentNullException(nameof(user));
        UserToken = userToken ?? throw new ArgumentNullException(nameof(userToken));
        ServiceCredentials = serviceCredentials ?? throw new ArgumentNullException(nameof(serviceCredentials));
        Extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Stable hashed id, tenant, display name if known.</summary>
    public UserIdentity User { get; }

    /// <summary>OBO token or forwarded OAuth token for the current user.</summary>
    public IUserTokenSource UserToken { get; }

    /// <summary>Managed identity tokens and Key Vault secrets.</summary>
    public IServiceCredentialSource ServiceCredentials { get; }

    /// <summary>Optional binary → text conversion.</summary>
    public IContentExtractor Extractor { get; }

    /// <summary>The provider's own configuration section (Providers:{Name}).</summary>
    public IConfiguration Settings { get; }
}

/// <param name="HashedId">Stable, non-reversible id of the user (hash of tenant + object id, or source subject).</param>
/// <param name="TenantId">Entra tenant id when known.</param>
/// <param name="DisplayName">Display name when known. Never logged by the host.</param>
public sealed record UserIdentity(string HashedId, string? TenantId = null, string? DisplayName = null)
{
    public static UserIdentity Anonymous { get; } = new("anonymous");

    public bool IsAnonymous => HashedId == "anonymous";
}

/// <summary>The user's credential for the downstream source.</summary>
public interface IUserTokenSource
{
    /// <summary>False when no user credential exists (auth mode None).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns a token that represents the user to the source. For EntraObo, <paramref name="scopes"/> must be a
    /// subset of the provider's declared downstream scopes (null means all of them). For OAuthPassthrough the
    /// forwarded token is returned and <paramref name="scopes"/> is ignored.
    /// Throws <see cref="ProviderException"/> with <see cref="ProviderErrorKind.Auth"/> when unavailable.
    /// </summary>
    ValueTask<string> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct);
}

/// <summary>Service credentials supplied by the host (managed identity, Key Vault).</summary>
public interface IServiceCredentialSource
{
    ValueTask<string> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct);

    /// <summary>Returns a secret from the connector's Key Vault, or null if absent.</summary>
    ValueTask<string?> GetSecretAsync(string name, CancellationToken ct);
}

/// <summary>Optional shared helper that converts binary documents to text.</summary>
public interface IContentExtractor
{
    bool CanExtract(string mediaType);

    /// <summary>Returns extracted text, or null when the media type is not supported.</summary>
    Task<string?> ExtractTextAsync(Stream content, string mediaType, CancellationToken ct);
}
