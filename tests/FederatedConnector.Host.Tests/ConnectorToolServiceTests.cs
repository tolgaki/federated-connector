using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class ConnectorToolServiceTests
{
    private static readonly byte[] Key = new byte[32];

    private sealed class FixedUserAccessor(RequestUser? user) : IRequestUserAccessor
    {
        public ValueTask<RequestUser?> GetCurrentAsync(CancellationToken ct) => ValueTask.FromResult(user);
    }

    private static RequestUser AliceUser => new(InMemoryConformanceFixture.Alice, new StaticUserTokenSource(InMemoryConformanceFixture.TokenA));
    private static RequestUser BobUser => new(InMemoryConformanceFixture.Bob, new StaticUserTokenSource(InMemoryConformanceFixture.TokenB));

    private static (ConnectorToolService Service, HandleCodec Codec) Create(IKnowledgeProvider provider, RequestUser? user, Action<ConnectorOptions>? configure = null)
    {
        var options = new ConnectorOptions { DisplayName = "Contoso Knowledge" };
        configure?.Invoke(options);
        var codec = new HandleCodec(Key, null, TimeProvider.System, TimeSpan.FromHours(1));
        var service = new ConnectorToolService(
            provider,
            new FixedUserAccessor(user),
            codec,
            Options.Create(options),
            new StaticServiceCredentials("svc"),
            new BasicContentExtractor(),
            new ProviderSettings(new ConfigurationBuilder().Build()),
            new ConnectorTelemetry(),
            new ToolCatalog(options.DisplayName, provider.Descriptor),
            NullLogger<ConnectorToolService>.Instance);
        return (service, codec);
    }

    private static Dictionary<string, JsonElement> Args(object args) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!;

    private static string Category(ToolResult r) => r.Structured["error"]!["category"]!.GetValue<string>();

    [Fact]
    public async Task Fails_closed_before_the_provider_when_no_user_credential()
    {
        var fixture = new InMemoryConformanceFixture();
        var (service, _) = Create(fixture.Memory, user: null);

        var result = await service.InvokeAsync("search", Args(new { query = "policy" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("auth", Category(result));
        Assert.Empty(fixture.Memory.Operations);
    }

    [Fact]
    public async Task Fails_closed_for_anonymous_identity_when_provider_needs_a_user()
    {
        var fixture = new InMemoryConformanceFixture();
        var (service, _) = Create(fixture.Memory, new RequestUser(UserIdentity.Anonymous, new StaticUserTokenSource("x")));
        var result = await service.InvokeAsync("list_collections", null, CancellationToken.None);
        Assert.Equal("auth", Category(result));
        Assert.Empty(fixture.Memory.Operations);
    }

    [Fact]
    public async Task Auth_mode_none_runs_without_a_user()
    {
        var fixture = InMemoryConformanceFixture.WithAuth(new AuthRequirement.None());
        var (service, _) = Create(fixture.Memory, user: null);
        var result = await service.InvokeAsync("search", Args(new { query = "policy" }), CancellationToken.None);
        Assert.False(result.IsError);
        var results = result.Structured["results"]!.AsArray();
        Assert.NotEmpty(results);
        Assert.DoesNotContain(results, r => r!["title"]!.GetValue<string>().Contains("compensation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_returns_signed_ids_and_result_fields()
    {
        var fixture = new InMemoryConformanceFixture();
        var (service, codec) = Create(fixture.Memory, AliceUser);

        var result = await service.InvokeAsync("search", Args(new { query = "leave", collection = "hr" }), CancellationToken.None);

        Assert.False(result.IsError);
        var hit = result.Structured["results"]!.AsArray().Single()!.AsObject();
        Assert.Equal("Parental leave policy", hit["title"]!.GetValue<string>());
        Assert.Equal("hr", hit["collection"]!.GetValue<string>());
        Assert.Equal("https://intranet.example.com/hr/leave", hit["url"]!.GetValue<string>());
        Assert.Equal("HR", hit["metadata"]!["author"]!.GetValue<string>());
        Assert.Equal(HandleDecodeStatus.Valid, codec.TryDecode(hit["id"]!.GetValue<string>(), "in-memory", InMemoryConformanceFixture.Alice, out _));
        Assert.Equal(result.Structured.ToJsonString(), result.Text);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 20)]
    public async Task Top_is_clamped(int requested, int expectedMax)
    {
        var provider = new RecordingProvider();
        var (service, _) = Create(provider, AliceUser);
        await service.InvokeAsync("search", Args(new { query = "x", top = requested }), CancellationToken.None);
        Assert.Equal(expectedMax, provider.LastQuery!.Top);
    }

    [Fact]
    public async Task Search_requires_a_query()
    {
        var (service, _) = Create(new RecordingProvider(), AliceUser);
        var result = await service.InvokeAsync("search", Args(new { query = "   " }), CancellationToken.None);
        Assert.Equal("invalid_request", Category(result));
    }

    [Fact]
    public async Task Fetch_round_trip_and_truncation()
    {
        var fixture = new InMemoryConformanceFixture();
        var (service, _) = Create(fixture.Memory, AliceUser, o => o.Limits.MaxFetchChars = 10);
        var search = await service.InvokeAsync("search", Args(new { query = "leave" }), CancellationToken.None);
        var id = search.Structured["results"]![0]!["id"]!.GetValue<string>();

        var fetch = await service.InvokeAsync("fetch", Args(new { id }), CancellationToken.None);

        Assert.False(fetch.IsError);
        Assert.Equal(id, fetch.Structured["id"]!.GetValue<string>());
        Assert.Equal(10, fetch.Structured["text"]!.GetValue<string>().Length);
        Assert.True(fetch.Structured["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Fetch_with_another_users_id_is_not_found_and_never_reaches_the_provider()
    {
        var fixture = new InMemoryConformanceFixture();
        var (alice, _) = Create(fixture.Memory, AliceUser);
        var search = await alice.InvokeAsync("search", Args(new { query = "compensation" }), CancellationToken.None);
        var id = search.Structured["results"]![0]!["id"]!.GetValue<string>();
        fixture.Memory.Operations.Clear();

        var (bob, _) = Create(fixture.Memory, BobUser);
        var fetch = await bob.InvokeAsync("fetch", Args(new { id }), CancellationToken.None);

        Assert.Equal("not_found", Category(fetch));
        Assert.Empty(fixture.Memory.Operations);
    }

    [Theory]
    [InlineData("not-a-handle")]
    [InlineData("https://evil.example.com/x")]
    public async Task Fetch_rejects_garbage_ids(string id)
    {
        var (service, _) = Create(new RecordingProvider(), AliceUser);
        Assert.Equal("not_found", Category(await service.InvokeAsync("fetch", Args(new { id }), CancellationToken.None)));
    }

    [Fact]
    public async Task Provider_errors_are_sanitised()
    {
        var provider = new RecordingProvider { Throw = new ProviderException(ProviderErrorKind.Unavailable, "SECRET upstream body: password=hunter2") };
        var (service, _) = Create(provider, AliceUser);

        var result = await service.InvokeAsync("search", Args(new { query = "x" }), CancellationToken.None);

        Assert.Equal("unavailable", Category(result));
        Assert.DoesNotContain("SECRET", result.Text);
        Assert.DoesNotContain("hunter2", result.Text);
    }

    [Fact]
    public async Task Unexpected_exceptions_are_sanitised()
    {
        var provider = new RecordingProvider { Throw = new InvalidOperationException("stack details /etc/passwd") };
        var (service, _) = Create(provider, AliceUser);
        var result = await service.InvokeAsync("list_collections", null, CancellationToken.None);
        Assert.Equal("unavailable", Category(result));
        Assert.DoesNotContain("passwd", result.Text);
    }

    [Fact]
    public async Task Slow_provider_times_out()
    {
        var fixture = new InMemoryConformanceFixture();
        fixture.Memory.Delay = TimeSpan.FromSeconds(10);
        var (service, _) = Create(fixture.Memory, AliceUser, o => o.Limits.ToolTimeoutSeconds = 1);

        var result = await service.InvokeAsync("search", Args(new { query = "policy" }), CancellationToken.None);

        Assert.Equal("timeout", Category(result));
    }

    [Fact]
    public async Task Partial_results_are_flagged()
    {
        var provider = new RecordingProvider { Partial = true };
        var (service, _) = Create(provider, AliceUser);
        var result = await service.InvokeAsync("search", Args(new { query = "x" }), CancellationToken.None);
        Assert.True(result.Structured["partial"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Unknown_tool_is_rejected()
    {
        var (service, _) = Create(new RecordingProvider(), AliceUser);
        var result = await service.InvokeAsync("delete_everything", null, CancellationToken.None);
        Assert.Equal("invalid_request", Category(result));
    }

    private sealed class RecordingProvider : IKnowledgeProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("recording", "test data", new AuthRequirement.EntraObo(["s"]), "collection");
        public SearchQuery? LastQuery { get; private set; }
        public Exception? Throw { get; init; }
        public bool Partial { get; init; }

        public Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(ProviderContext ctx, CancellationToken ct) =>
            Throw is not null ? Task.FromException<IReadOnlyList<CollectionInfo>>(Throw) : Task.FromResult<IReadOnlyList<CollectionInfo>>([new("c", null)]);

        public Task<SearchResultSet> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
        {
            LastQuery = query;
            return Throw is not null ? Task.FromException<SearchResultSet>(Throw) : Task.FromResult(new SearchResultSet([], Partial));
        }

        public Task<DocumentContent> FetchAsync(HandlePayload handle, ProviderContext ctx, CancellationToken ct) =>
            Task.FromResult(new DocumentContent("t", null, "x", null, DocumentMetadata.Empty));
    }
}
