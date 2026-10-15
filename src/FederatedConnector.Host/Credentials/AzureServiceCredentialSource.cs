using System.Collections.Concurrent;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using FederatedConnector.Abstractions;
using Microsoft.Extensions.Options;

namespace FederatedConnector.Host.Credentials;

/// <summary>Managed identity tokens and Key Vault secrets for providers.</summary>
public sealed class AzureServiceCredentialSource : IServiceCredentialSource
{
    private readonly TokenCredential _credential;
    private readonly SecretClient? _secrets;
    private readonly ConcurrentDictionary<string, AccessToken> _tokens = new();

    public AzureServiceCredentialSource(IOptions<ConnectorOptions> options)
    {
        var o = options.Value;
        _credential = string.IsNullOrEmpty(o.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(o.ManagedIdentityClientId));
        _secrets = o.KeyVaultUri is null ? null : new SecretClient(o.KeyVaultUri, _credential);
    }

    public async ValueTask<string> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var key = string.Join(' ', scopes);
        if (_tokens.TryGetValue(key, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached.Token;
        }

        try
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext([.. scopes]), ct).ConfigureAwait(false);
            _tokens[key] = token;
            return token.Token;
        }
        catch (AuthenticationFailedException ex)
        {
            throw new ProviderException(ProviderErrorKind.Unavailable, "Service credential unavailable.", ex);
        }
    }

    public async ValueTask<string?> GetSecretAsync(string name, CancellationToken ct)
    {
        if (_secrets is null)
        {
            return null;
        }

        try
        {
            var secret = await _secrets.GetSecretAsync(name, cancellationToken: ct).ConfigureAwait(false);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
