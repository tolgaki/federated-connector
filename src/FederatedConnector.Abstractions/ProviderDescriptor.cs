namespace FederatedConnector.Abstractions;

/// <param name="Id">Stable, lowercase provider id, e.g. "foundry-iq". Embedded in result handles.</param>
/// <param name="ContentDescription">What the data is; goes into tool descriptions so Copilot routes correctly.</param>
/// <param name="Auth">How the host must authenticate users for this provider.</param>
/// <param name="CollectionNoun">Singular noun for a collection, e.g. "knowledge base", "shared drive".</param>
public sealed record ProviderDescriptor(
    string Id,
    string ContentDescription,
    AuthRequirement Auth,
    string CollectionNoun);

/// <summary>The single inbound authentication method of a connector deployment.</summary>
public abstract record AuthRequirement
{
    private AuthRequirement() { }

    /// <summary>Copilot sends an Entra token for our app; the host performs OBO to these scopes.</summary>
    public sealed record EntraObo(IReadOnlyList<string> DownstreamScopes) : AuthRequirement;

    /// <summary>
    /// Copilot holds the source's own OAuth token (Developer Portal OAuth registration); the host forwards it.
    /// The source is the validator. If <paramref name="UserInfoUrl"/> is supplied, the host calls it once per
    /// token (cached briefly) to obtain a stable subject for handle binding.
    /// </summary>
    public sealed record OAuthPassthrough(
        Uri AuthorizeUrl,
        Uri TokenUrl,
        IReadOnlyList<string> Scopes,
        bool SupportsPkce,
        Uri? UserInfoUrl = null) : AuthRequirement;

    /// <summary>Public data only. No user context is available.</summary>
    public sealed record None : AuthRequirement;

    public string Mode => this switch
    {
        EntraObo => "EntraObo",
        OAuthPassthrough => "OAuthPassthrough",
        None => "None",
        _ => throw new InvalidOperationException("Unknown auth requirement."),
    };
}
