using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using FederatedConnector.Abstractions;
using FederatedConnector.ContentExtraction;
using FederatedConnector.Host.Auth;
using FederatedConnector.Host.Credentials;
using FederatedConnector.Host.Handles;
using FederatedConnector.Host.Mcp;
using FederatedConnector.Host.Telemetry;
using FederatedConnector.Host.Tools;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace FederatedConnector.Host;

/// <summary>Composition of the provider-agnostic host. Used by Program and by the integration tests.</summary>
public static class ConnectorHost
{
    public const string RateLimitPolicy = "per-user";

    /// <summary>Registers the host for the provider named in <c>Connector:Provider</c>. Returns the provider descriptor.</summary>
    public static ProviderDescriptor AddConnector(this WebApplicationBuilder builder, IEnumerable<ProviderRegistration> providers)
    {
        var config = builder.Configuration;
        var services = builder.Services;
        var options = config.GetSection(ConnectorOptions.SectionName).Get<ConnectorOptions>() ?? new ConnectorOptions();
        services.Configure<ConnectorOptions>(config.GetSection(ConnectorOptions.SectionName));

        var registration = providers.FirstOrDefault(p => string.Equals(p.Name, options.Provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Connector:Provider '{options.Provider}' is not a compiled-in provider. Known: {string.Join(", ", providers.Select(p => p.Name))}.");

        var providerSection = config.GetSection("Providers:" + registration.Name);
        var descriptor = registration.Describe(providerSection);
        Validate(descriptor);

        services.AddSingleton(descriptor);
        services.AddSingleton(new ProviderSettings(providerSection));
        registration.Register(services, providerSection);

        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IContentExtractor, BasicContentExtractor>();
        services.AddSingleton<IServiceCredentialSource, AzureServiceCredentialSource>();
        services.AddSingleton<ConnectorTelemetry>();
        services.AddSingleton(new ToolCatalog(options.DisplayName, descriptor));
        services.AddSingleton(sp => CreateHandleCodec(options, sp.GetRequiredService<TimeProvider>(), builder.Environment, sp.GetRequiredService<ILogger<HandleCodec>>()));
        services.AddScoped<ConnectorToolService>();

        services.AddConnectorAuth(config, options, descriptor);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.Limits.RequestsPerMinutePerUser),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        services.AddConnectorMcp(options.DisplayName);

        var otel = services.AddOpenTelemetry()
            .WithTracing(t => t.AddSource(ConnectorTelemetry.SourceName))
            .WithMetrics(m => m.AddMeter(ConnectorTelemetry.SourceName));
        if (!string.IsNullOrEmpty(config["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            otel.UseAzureMonitor();
        }

        return descriptor;
    }

    public static WebApplication MapConnector(this WebApplication app)
    {
        var descriptor = app.Services.GetRequiredService<ProviderDescriptor>();
        app.UseRouting();
        if (descriptor.Auth is AuthRequirement.OAuthPassthrough)
        {
            app.UseBearerRequired(McpWiring.Route);
        }

        if (descriptor.Auth is AuthRequirement.EntraObo)
        {
            app.UseAuthentication();
        }

        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        var mcp = app.MapMcp(McpWiring.Route).RequireRateLimiting(RateLimitPolicy);
        if (descriptor.Auth is AuthRequirement.EntraObo)
        {
            mcp.RequireAuthorization(AuthSetup.McpPolicy);
        }

        return app;
    }

    private static void Validate(ProviderDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(d.Id) || d.Id != d.Id.ToLowerInvariant() || d.Id.Contains('.'))
        {
            throw new InvalidOperationException("Provider id must be a non-empty lowercase string without dots.");
        }

        if (d.Auth is AuthRequirement.EntraObo { DownstreamScopes.Count: 0 })
        {
            throw new InvalidOperationException("EntraObo providers must declare at least one downstream scope.");
        }
    }

    private static HandleCodec CreateHandleCodec(ConnectorOptions o, TimeProvider time, IWebHostEnvironment env, ILogger logger)
    {
        byte[] key;
        if (!string.IsNullOrEmpty(o.HandleSigningKey))
        {
            key = Convert.FromBase64String(o.HandleSigningKey);
        }
        else if (env.IsDevelopment())
        {
            logger.LogWarning("Connector:HandleSigningKey is not set; using an ephemeral key (Development only).");
            key = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            throw new InvalidOperationException("Connector:HandleSigningKey is required outside Development.");
        }

        var previous = string.IsNullOrEmpty(o.PreviousHandleSigningKey) ? null : Convert.FromBase64String(o.PreviousHandleSigningKey);
        return new HandleCodec(key, previous, time, TimeSpan.FromMinutes(Math.Max(1, o.HandleLifetimeMinutes)));
    }

    private static string PartitionKey(HttpContext ctx)
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated == true && EntraClaims.ObjectId(user) is { } oid)
        {
            return "u:" + UserHash.FromEntra(EntraClaims.TenantId(user) ?? "", oid);
        }

        if (PassthroughUserAccessor.BearerToken(ctx) is { } token)
        {
            return "t:" + UserHash.FromOpaqueToken(token);
        }

        return "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
