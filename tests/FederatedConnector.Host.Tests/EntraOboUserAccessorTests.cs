using System.Security.Claims;
using FederatedConnector.Abstractions;
using FederatedConnector.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;

namespace FederatedConnector.Host.Tests;

public sealed class EntraOboUserAccessorTests
{
    private const string Scope = "https://search.azure.com/user_impersonation";
    private static readonly ProviderDescriptor Descriptor = new("foundry-iq", "d", new AuthRequirement.EntraObo([Scope]), "knowledge base");

    private sealed class FakeHeaders : IAuthorizationHeaderProvider
    {
        public List<string[]> Requested { get; } = [];

        public Task<string> CreateAuthorizationHeaderForUserAsync(IEnumerable<string> scopes, AuthorizationHeaderProviderOptions? options = null, ClaimsPrincipal? claimsPrincipal = null, CancellationToken cancellationToken = default)
        {
            Requested.Add([.. scopes]);
            return Task.FromResult("Bearer obo-token");
        }

        public Task<string> CreateAuthorizationHeaderForAppAsync(string scopes, AuthorizationHeaderProviderOptions? downstreamApiOptions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> CreateAuthorizationHeaderAsync(IEnumerable<string> scopes, AuthorizationHeaderProviderOptions? options = null, ClaimsPrincipal? claimsPrincipal = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static (EntraOboUserAccessor Accessor, FakeHeaders Headers) Create(ClaimsPrincipal principal, params string[] allowedTenants)
    {
        var headers = new FakeHeaders();
        var services = new ServiceCollection().AddSingleton<IAuthorizationHeaderProvider>(headers).BuildServiceProvider();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal, RequestServices = services } };
        var options = Options.Create(new ConnectorOptions { AllowedTenants = [.. allowedTenants] });
        return (new EntraOboUserAccessor(http, options, Descriptor), headers);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    [Fact]
    public async Task Delegated_token_with_scope_yields_user_and_obo_token()
    {
        var (accessor, headers) = Create(Principal(("oid", "alice"), ("tid", "t1"), ("scp", "openid access_as_user")));

        var user = await accessor.GetCurrentAsync(CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal(UserHash.FromEntra("t1", "alice"), user!.Identity.HashedId);
        Assert.Equal("obo-token", await user.Token.GetTokenAsync(null, CancellationToken.None));
        Assert.Equal([Scope], headers.Requested.Single());
    }

    [Fact]
    public async Task App_only_token_is_rejected()
    {
        var (accessor, _) = Create(Principal(("oid", "app"), ("tid", "t1"), ("roles", "Read.All"), ("idtyp", "app")));
        Assert.Null(await accessor.GetCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_scope_is_rejected()
    {
        var (accessor, _) = Create(Principal(("oid", "alice"), ("tid", "t1"), ("scp", "User.Read")));
        Assert.Null(await accessor.GetCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tenant_outside_allowlist_is_rejected()
    {
        var (accessor, _) = Create(Principal(("oid", "alice"), ("tid", "t2"), ("scp", "access_as_user")), "t1");
        Assert.Null(await accessor.GetCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Provider_cannot_request_undeclared_scopes()
    {
        var (accessor, headers) = Create(Principal(("oid", "alice"), ("tid", "t1"), ("scp", "access_as_user")));
        var user = await accessor.GetCurrentAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ProviderException>(async () => await user!.Token.GetTokenAsync(["https://graph.microsoft.com/.default"], CancellationToken.None));
        Assert.Equal(ProviderErrorKind.Auth, ex.Kind);
        Assert.Empty(headers.Requested);
    }
}
