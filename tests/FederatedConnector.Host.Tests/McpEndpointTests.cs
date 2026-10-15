using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FederatedConnector.Abstractions;
using FederatedConnector.Conformance;
using FederatedConnector.SmokeTest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FederatedConnector.Host.Tests;

/// <summary>End-to-end over MCP Streamable HTTP, in process, using the same composition as Program.</summary>
public sealed class McpEndpointTests
{
    private static async Task<WebApplication> StartAsync(AuthRequirement auth)
    {
        var fixture = InMemoryConformanceFixture.WithAuth(auth);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Connector:Provider"] = "InMemory",
            ["Connector:DisplayName"] = "Contoso Knowledge",
            ["Connector:HandleSigningKey"] = Convert.ToBase64String(new byte[32]),
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000002",
        });
        builder.AddConnector([new ProviderRegistration("InMemory", _ => fixture.Provider.Descriptor, (s, _) => s.AddSingleton(fixture.Provider))]);
        var app = builder.Build();
        app.MapConnector();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Smoke_test_passes_against_the_host()
    {
        await using var app = await StartAsync(new AuthRequirement.None());
        var log = new StringWriter();

        var ok = await SmokeRunner.RunAsync(
            app.GetTestClient(),
            new SmokeOptions(new Uri("http://localhost/mcp"), null, "policy", SkipSearch: false, RequireResults: true),
            log,
            CancellationToken.None);

        Assert.True(ok, log.ToString());
        Assert.Contains("PASS  tools/list", log.ToString());
        Assert.Contains("PASS  fetch", log.ToString());
    }

    [Fact]
    public async Task Passthrough_mode_forwards_the_bearer_token_to_the_provider()
    {
        await using var app = await StartAsync(new AuthRequirement.OAuthPassthrough(
            new Uri("https://idp.example.com/authorize"), new Uri("https://idp.example.com/token"), ["read"], SupportsPkce: true));
        var log = new StringWriter();

        var ok = await SmokeRunner.RunAsync(
            app.GetTestClient(),
            new SmokeOptions(new Uri("http://localhost/mcp"), InMemoryConformanceFixture.TokenA, "compensation", false, true),
            log,
            CancellationToken.None);

        Assert.True(ok, log.ToString());
    }

    [Fact]
    public async Task Passthrough_mode_rejects_requests_without_a_bearer_token()
    {
        await using var app = await StartAsync(new AuthRequirement.OAuthPassthrough(
            new Uri("https://idp.example.com/authorize"), new Uri("https://idp.example.com/token"), ["read"], SupportsPkce: true));

        var response = await PostInitialize(app.GetTestClient(), bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Passthrough_token_rejected_by_source_becomes_an_auth_error()
    {
        await using var app = await StartAsync(new AuthRequirement.OAuthPassthrough(
            new Uri("https://idp.example.com/authorize"), new Uri("https://idp.example.com/token"), ["read"], SupportsPkce: true));
        var log = new StringWriter();

        var ok = await SmokeRunner.RunAsync(
            app.GetTestClient(),
            new SmokeOptions(new Uri("http://localhost/mcp"), "expired-token", "policy", false, false),
            log,
            CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("FAIL  search — tool error: auth", log.ToString());
    }

    [Fact]
    public async Task Entra_mode_rejects_unauthenticated_requests()
    {
        await using var app = await StartAsync(new AuthRequirement.EntraObo(["https://search.azure.com/user_impersonation"]));

        var response = await PostInitialize(app.GetTestClient(), bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        await using var app = await StartAsync(new AuthRequirement.EntraObo(["https://search.azure.com/user_impersonation"]));
        var response = await app.GetTestClient().GetAsync(new Uri("http://localhost/healthz"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostInitialize(HttpClient client, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client.SendAsync(request);
    }
}
