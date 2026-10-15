using System.Security.Claims;
using FederatedConnector.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace FederatedConnector.Host.Auth;

/// <summary>
/// EntraObo: the inbound JWT has already been validated by Microsoft.Identity.Web (audience, issuer, tenant allowlist).
/// Here we require a delegated token with the connector scope and an object id, then expose an OBO token source.
/// </summary>
public sealed class EntraOboUserAccessor(
    IHttpContextAccessor http,
    IOptions<ConnectorOptions> options,
    ProviderDescriptor descriptor) : IRequestUserAccessor
{
    public ValueTask<RequestUser?> GetCurrentAsync(CancellationToken ct)
    {
        var ctx = http.HttpContext;
        var principal = ctx?.User;
        if (ctx is null || principal is null || !EntraClaims.HasDelegatedScope(principal, options.Value.RequiredScope))
        {
            return ValueTask.FromResult<RequestUser?>(null);
        }

        var tid = EntraClaims.TenantId(principal);
        var oid = EntraClaims.ObjectId(principal);
        if (tid is null || oid is null || !TenantAllowed(tid))
        {
            return ValueTask.FromResult<RequestUser?>(null);
        }

        var scopes = descriptor.Auth is AuthRequirement.EntraObo obo ? obo.DownstreamScopes : [];
        var identity = new UserIdentity(UserHash.FromEntra(tid, oid), tid, EntraClaims.Name(principal));
        var tokens = new EntraOboTokenSource(ctx.RequestServices.GetRequiredService<IAuthorizationHeaderProvider>(), principal, scopes);
        return ValueTask.FromResult<RequestUser?>(new RequestUser(identity, tokens));
    }

    private bool TenantAllowed(string tid)
    {
        var allowed = options.Value.AllowedTenants;
        return allowed.Count == 0 || allowed.Contains(tid, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>On-behalf-of token acquisition, restricted to the provider's declared downstream scopes.</summary>
public sealed class EntraOboTokenSource(
    IAuthorizationHeaderProvider headers,
    ClaimsPrincipal user,
    IReadOnlyList<string> allowedScopes) : IUserTokenSource
{
    public bool IsAvailable => true;

    public async ValueTask<string> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        var requested = scopes is null || scopes.Count == 0 ? allowedScopes : scopes;
        if (requested.Count == 0 || requested.Any(s => !allowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            throw new ProviderException(ProviderErrorKind.Auth, "Provider requested a scope it did not declare.");
        }

        try
        {
            var header = await headers.CreateAuthorizationHeaderForUserAsync(
                requested,
                new AuthorizationHeaderProviderOptions(),
                user,
                ct).ConfigureAwait(false);
            const string prefix = "Bearer ";
            return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..] : header;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new ProviderException(ProviderErrorKind.Auth, "OBO requires user interaction or consent.", ex);
        }
        catch (MicrosoftIdentityWebChallengeUserException ex)
        {
            throw new ProviderException(ProviderErrorKind.Auth, "OBO challenge required.", ex);
        }
        catch (MsalServiceException ex)
        {
            throw new ProviderException(ProviderErrorKind.Auth, "OBO exchange failed: " + ex.ErrorCode, ex);
        }
    }
}
