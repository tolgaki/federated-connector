using System.Text.Json;
using FederatedConnector.Abstractions;
using FederatedConnector.ContentExtraction;
using FederatedConnector.Conformance;
using FederatedConnector.Host.Auth;
using FederatedConnector.Host.Handles;
using FederatedConnector.Host.Telemetry;
using FederatedConnector.Host.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FederatedConnector.Host.Tests;

/// <summary>Regression tests for defects found in the dual review against the design.</summary>
public sealed class ReviewRegressionTests
{
    private sealed class SlowUserAccessor : IRequestUserAccessor
    {
        public async ValueTask<RequestUser?> GetCurrentAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new RequestUser(InMemoryConformanceFixture.Alice, new StaticUserTokenSource(InMemoryConformanceFixture.TokenA));
        }
    }

    [Fact]
    public async Task User_resolution_counts_against_the_tool_timeout()
    {
        var fixture = new InMemoryConformanceFixture();
        var options = new ConnectorOptions { DisplayName = "X" };
        options.Limits.ToolTimeoutSeconds = 1;
        var service = new ConnectorToolService(
            fixture.Provider,
            new SlowUserAccessor(),
            new HandleCodec(new byte[32], null, TimeProvider.System, TimeSpan.FromHours(1)),
            Options.Create(options),
            new StaticServiceCredentials("svc"),
            new BasicContentExtractor(),
            new ProviderSettings(new ConfigurationBuilder().Build()),
            new ConnectorTelemetry(),
            new ToolCatalog("X", fixture.Provider.Descriptor),
            NullLogger<ConnectorToolService>.Instance);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"query":"policy"}""");

        var started = DateTime.UtcNow;
        var result = await service.InvokeAsync("search", args, CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal("timeout", result.Structured["error"]!["category"]!.GetValue<string>());
    }

    private sealed class HugePayloadProvider : IKnowledgeProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("huge", "test", new AuthRequirement.None(), "collection");
        public Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(ProviderContext ctx, CancellationToken ct) => Task.FromResult<IReadOnlyList<CollectionInfo>>([]);
        public Task<DocumentContent> FetchAsync(HandlePayload handle, ProviderContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<SearchResultSet> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct) => Task.FromResult(new SearchResultSet(
        [
            new SearchHit(new HandlePayload("c", new Dictionary<string, string> { ["x"] = new('a', 5000) }), "too big", "s", null, "c", DocumentMetadata.Empty),
            new SearchHit(new HandlePayload("c", new Dictionary<string, string> { ["x"] = "small" }), "fine", "s", null, "c", DocumentMetadata.Empty),
        ]));
    }

    [Fact]
    public async Task Hits_whose_handles_would_be_rejected_are_dropped_and_flagged_partial()
    {
        var provider = new HugePayloadProvider();
        var service = new ConnectorToolService(
            provider,
            new AnonymousUserAccessor(),
            new HandleCodec(new byte[32], null, TimeProvider.System, TimeSpan.FromHours(1)),
            Options.Create(new ConnectorOptions()),
            new StaticServiceCredentials("svc"),
            new BasicContentExtractor(),
            new ProviderSettings(new ConfigurationBuilder().Build()),
            new ConnectorTelemetry(),
            new ToolCatalog("X", provider.Descriptor),
            NullLogger<ConnectorToolService>.Instance);

        var result = await service.InvokeAsync("search", JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"query":"q"}"""), CancellationToken.None);

        var results = result.Structured["results"]!.AsArray();
        Assert.Equal("fine", results.Single()!["title"]!.GetValue<string>());
        Assert.True(result.Structured["partial"]!.GetValue<bool>());
    }

    /// <summary>A provider for public data (auth mode None) must be able to pass the whole kit.</summary>
    public sealed class PublicOnlyFixture : IConformanceFixture
    {
        private readonly InMemoryKnowledgeProvider _provider = new(
            [new InMemoryDocument("p-1", "public", "Public policy", "A public policy document.")],
            new Dictionary<string, string>(),
            new AuthRequirement.None());

        public IKnowledgeProvider Provider => _provider;
        public string RestrictedQuery => "policy";
        public string CommonQuery => "policy";
        public ProviderContext CreateContext(ConformanceUser user) => TestContexts.Create(UserIdentity.Anonymous, null);
        public bool IsRestrictedDocument(SearchHit hit) => false;
        public IEnumerable<HandlePayload> ForeignPayloads => [new("other", new Dictionary<string, string> { ["doc"] = "p-1" })];
        public void AssertNoWrites() { }
    }

    public sealed class PublicOnlyConformance(PublicOnlyFixture f) : ProviderConformanceTests<PublicOnlyFixture>(f);
}
