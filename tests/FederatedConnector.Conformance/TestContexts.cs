using FederatedConnector.Abstractions;
using FederatedConnector.ContentExtraction;
using Microsoft.Extensions.Configuration;

namespace FederatedConnector.Conformance;

/// <summary>Helpers for building provider contexts in tests.</summary>
public static class TestContexts
{
    public static ProviderContext Create(UserIdentity user, string? userToken, IServiceCredentialSource? service = null, IConfiguration? settings = null) =>
        new(
            user,
            new StaticUserTokenSource(userToken),
            service ?? new StaticServiceCredentials("service-token"),
            new BasicContentExtractor(),
            settings ?? new ConfigurationBuilder().Build());
}

/// <summary>A fixed user token; unavailable when null.</summary>
public sealed class StaticUserTokenSource(string? token) : IUserTokenSource
{
    public bool IsAvailable => token is not null;

    public ValueTask<string> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return token is null
            ? throw new ProviderException(ProviderErrorKind.Auth, "No user token.")
            : ValueTask.FromResult(token);
    }
}

public sealed class StaticServiceCredentials(string token, IReadOnlyDictionary<string, string>? secrets = null) : IServiceCredentialSource
{
    public List<IReadOnlyList<string>> RequestedScopes { get; } = [];

    public ValueTask<string> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        lock (RequestedScopes)
        {
            RequestedScopes.Add(scopes);
        }

        return ValueTask.FromResult(token);
    }

    public ValueTask<string?> GetSecretAsync(string name, CancellationToken ct) =>
        ValueTask.FromResult(secrets is not null && secrets.TryGetValue(name, out var v) ? v : null);
}
