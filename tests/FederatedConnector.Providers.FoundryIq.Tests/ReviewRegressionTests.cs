using System.Net;
using System.Text.Json;
using FederatedConnector.Abstractions;
using FederatedConnector.Conformance;
using FederatedConnector.Host.Handles;

namespace FederatedConnector.Providers.FoundryIq.Tests;

/// <summary>Regression tests for defects found in the dual review against the design.</summary>
public sealed class ReviewRegressionTests
{
    private readonly FakeSearchService _search = new();

    private static ProviderContext Ctx(UserIdentity user, string token) =>
        TestContexts.Create(user, token, new StaticServiceCredentials(FakeSearchService.ServiceToken));

    private static ProviderContext Alice => Ctx(InMemoryConformanceFixture.Alice, FakeSearchService.AliceToken);

    private static ProviderContext Bob => Ctx(InMemoryConformanceFixture.Bob, FakeSearchService.BobToken);

    private FoundryIqProvider Provider(Action<FoundryIqOptions>? configure = null)
    {
        var options = FoundryIqConformanceFixture.DefaultOptions();
        configure?.Invoke(options);
        return FoundryIqConformanceFixture.CreateProvider(_search, options);
    }

    [Fact]
    public void Remote_hits_with_long_non_ascii_text_produce_decodable_handles()
    {
        var provider = Provider(o => o.UsePreviewApi = true);
        var kb = new KnowledgeBaseInfo("hr-kb", "HR", [new("web-ks", "web", null, null)]);
        var reference = new
        {
            references = new[]
            {
                new
                {
                    type = "web",
                    id = "0",
                    url = "https://www.example.org/" + new string('页', 60),
                    title = new string('政', 200),
                    sourceData = new { snippet = new string('策', 600) },
                },
            },
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(reference));
        var hit = provider.MapReferences(doc.RootElement, kb).Single().Hit;

        var codec = new HandleCodec(new byte[32], null, TimeProvider.System, TimeSpan.FromHours(1));
        var id = codec.Encode("foundry-iq", hit.Handle, InMemoryConformanceFixture.Alice);

        Assert.True(id.Length <= HandleCodec.MaxHandleLength, $"handle is {id.Length} characters");
        Assert.Equal(HandleDecodeStatus.Valid, codec.TryDecode(id, "foundry-iq", InMemoryConformanceFixture.Alice, out _));
    }

    [Theory]
    [InlineData("https://contoso-search.search.windows.net/indexes/hr-index/docs/hr-2?api-version=2026-08-01-preview")]
    [InlineData("https://contoso-search.search.windows.net/indexes/hr-index-other/docs/hr-1?api-version=2026-08-01-preview")]
    [InlineData("https://contoso-search.search.windows.net/indexes/other/docs/hr-index?api-version=2026-08-01-preview")]
    public async Task Citation_url_must_name_the_checked_index_and_document(string citationUrl)
    {
        // Bob may see hr-1 (the access check passes) but not hr-2; the citation must not read anything else.
        var payload = new HandlePayload("hr-kb", new Dictionary<string, string>
        {
            ["kb"] = "hr-kb", ["ks"] = "hr-ks", ["index"] = "hr-index", ["docKey"] = "hr-1", ["citationUrl"] = citationUrl,
        });

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider(o => o.UsePreviewApi = true).FetchAsync(payload, Bob, default));

        Assert.Equal(ProviderErrorKind.NotFound, ex.Kind);
        Assert.DoesNotContain(_search.Requests, r => r.Method == "GET" && r.Path.Contains("/docs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Citation_url_for_the_checked_document_is_followed()
    {
        var payload = new HandlePayload("hr-kb", new Dictionary<string, string>
        {
            ["kb"] = "hr-kb", ["ks"] = "hr-ks", ["index"] = "hr-index", ["docKey"] = "hr-1",
            ["citationUrl"] = "https://contoso-search.search.windows.net/indexes/hr-index/docs/hr-1?api-version=2026-08-01-preview",
        });

        var doc = await Provider(o => o.UsePreviewApi = true).FetchAsync(payload, Bob, default);

        Assert.Equal("Parental leave policy", doc.Title);
    }

    [Fact]
    public async Task Configured_sources_work_without_permission_to_read_knowledge_base_definitions()
    {
        _search.Override = r => r.Method == HttpMethod.Get && !r.RequestUri!.AbsolutePath.Contains("/docs", StringComparison.Ordinal)
            ? FakeSearchService.Status(HttpStatusCode.Forbidden)
            : null;
        var provider = Provider(o =>
        {
            o.KnowledgeBases = [new KnowledgeBaseOptions { Name = "hr-kb", KnowledgeSources = ["hr-ks"] }];
            o.KnowledgeSources = [new KnowledgeSourceOptions { Name = "hr-ks", Kind = "searchIndex", Index = "hr-index", KeyField = "id" }];
        });

        var hit = (await provider.SearchAsync(new SearchQuery("leave", null, 5), Alice, default)).Hits.Single();
        var doc = await provider.FetchAsync(hit.Handle, Alice, default);
        var collections = await provider.ListCollectionsAsync(Alice, default);

        Assert.Equal("Parental leave policy", doc.Title);
        Assert.Equal("hr-kb", collections.Single().Name);
    }
}
