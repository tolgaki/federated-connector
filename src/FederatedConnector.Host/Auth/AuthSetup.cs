using FederatedConnector.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

namespace FederatedConnector.Host.Auth;

/// <summary>Configures the one inbound authentication method this deployment uses, derived from the provider.</summary>
public static class AuthSetup
{
    public const string McpPolicy = "mcp-user";

    public static void AddConnectorAuth(this IServiceCollection services, IConfiguration config, ConnectorOptions options, ProviderDescriptor descriptor)
    {
        switch (descriptor.Auth)
        {
            case AuthRequirement.EntraObo:
                AddEntraObo(services, config, options);
                services.AddScoped<IRequestUserAccessor, EntraOboUserAccessor>();
                break;
            case AuthRequirement.OAuthPassthrough:
                services.AddHttpClient(PassthroughUserAccessor.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));
                services.AddScoped<IRequestUserAccessor, PassthroughUserAccessor>();
                services.AddAuthorization();
                break;
            case AuthRequirement.None:
                services.AddSingleton<IRequestUserAccessor, AnonymousUserAccessor>();
                services.AddAuthorization();
                break;
        }
    }

    private static void AddEntraObo(IServiceCollection services, IConfiguration config, ConnectorOptions options)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(config.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddInMemoryTokenCaches();

        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
        {
            if (options.AcceptedAudiences.Count > 0)
            {
                jwt.TokenValidationParameters.ValidAudiences = options.AcceptedAudiences;
            }

            jwt.Events ??= new JwtBearerEvents();
            var previous = jwt.Events.OnTokenValidated;
            jwt.Events.OnTokenValidated = async context =>
            {
                if (previous is not null)
                {
                    await previous(context).ConfigureAwait(false);
                }

                var tid = context.Principal is null ? null : EntraClaims.TenantId(context.Principal);
                if (options.AllowedTenants.Count > 0 &&
                    (tid is null || !options.AllowedTenants.Contains(tid, StringComparer.OrdinalIgnoreCase)))
                {
                    context.Fail("Tenant is not allowed.");
                }
            };
        });

        services.AddAuthorization(o => o.AddPolicy(McpPolicy, p => p
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(c => EntraClaims.HasDelegatedScope(c.User, options.RequiredScope))));
    }

    /// <summary>
    /// OAuthPassthrough has no local validator, so the endpoint only requires that a bearer token is present and
    /// answers 401 with a challenge otherwise. The source validates the token on first use.
    /// </summary>
    public static IApplicationBuilder UseBearerRequired(this IApplicationBuilder app, PathString path) =>
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(path) && PassthroughUserAccessor.BearerToken(ctx) is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next(ctx).ConfigureAwait(false);
        });
}
