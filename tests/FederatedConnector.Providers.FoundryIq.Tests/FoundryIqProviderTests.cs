using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FederatedConnector.Abstractions;
using FederatedConnector.Conformance;

namespace FederatedConnector.Providers.FoundryIq.Tests;

public sealed class FoundryIqProviderTests
{
    private readonly FakeSearchService _search = new();

    private FoundryIqProvider Provider(Action<FoundryIqOptions>? configure = null)
    {
        var options = FoundryIqConformanceFixture.DefaultOptions();
        configure?.Invoke(options);
        return FoundryIqConformanceFixture.CreateProvider(_search, options);
    }

    private static ProviderContext Alice => TestContexts.Create(InMemoryConformanceFixture.Alice, FakeSearchService.AliceToken, new StaticServiceCredentials(FakeSearchService.ServiceToken));

    private static ProviderContext Bob => TestContexts.Create(InMemoryConformanceFixture.Bob, FakeSearchService.BobToken, new StaticServiceCredentials(FakeSearchService.ServiceToken));

    [Fact]
    public void Descriptor_declares_entra_obo_to_search()
    {
        var d = Provider().Descriptor;
        Assert.Equal("foundry-iq", d.Id);
        Assert.Equal("knowledge base", d.CollectionNoun);
        var obo = Assert.IsType<AuthRequirement.EntraObo>(d.Auth);
        Assert.Equal(["https://search.azure.com/user_impersonation"], obo.DownstreamScopes);
    }

    [Fact]
    public async Task Every_user_scoped_call_carries_the_service_credential_and_the_user_token()
    {
        var provider = Provider();
        var hits = (await provider.SearchAsync(new SearchQuery("policy", null, 8), Alice, default)).Hits;
        foreach (var hit in hits)
        {
            await provider.FetchAsync(hit.Handle, Alice, default);
        }

        var userScoped = _search.Requests.Where(r => r.Path.EndsWith("/retrieve", StringComparison.Ordinal) || r.Path.EndsWith("/docs/search", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(userScoped);
        Assert.All(_search.Requests, r => Assert.Equal("Bearer " + FakeSearchService.ServiceToken, r.Authorization));
        Assert.All(userScoped, r => Assert.Equal(FakeSearchService.AliceToken, r.UserToken));
    }

    [Fact]
    public async Task Retrieve_request_uses_intents_and_only_allowed_sources()
    {
        await Provider().SearchAsync(new SearchQuery("deploy", "eng-kb", 5), Alice, default);

        var retrieve = _search.Requests.Single(r => r.Path == "/knowledgebases/eng-kb/retrieve");
        Assert.Contains("api-version=2026-04-01", retrieve.Query);
        var body = JsonNode.Parse(retrieve.Body!)!;
        Assert.Equal("deploy", body["intents"]![0]!["search"]!.GetValue<string>());
        Assert.Equal("extractiveData", body["outputMode"]!.GetValue<string>());
        var sources = body["knowledgeSourceParams"]!.AsArray().Select(p => p!["knowledgeSourceName"]!.GetValue<string>()).ToList();
        Assert.Equal(["eng-ks", "web-ks"], sources);
        Assert.All(body["knowledgeSourceParams"]!.AsArray(), p => Assert.True(p!["includeReferenceSourceData"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Work_iq_references_are_never_surfaced()
    {
        var hits = (await Provider().SearchAsync(new SearchQuery("policy email", "eng-kb", 20), Alice, default)).Hits;
        Assert.DoesNotContain(hits, h => h.Title == "Loop-back email");
        Assert.DoesNotContain(hits, h => h.Handle.Get("ks") == "wiq-ks");
    }

    [Fact]
    public async Task Fan_out_merges_and_ranks_across_knowledge_bases()
    {
        var hits = (await Provider().SearchAsync(new SearchQuery("policy", null, 20), Alice, default)).Hits;
        Assert.Contains(hits, h => h.Collection == "hr-kb");
        Assert.Contains(hits, h => h.Collection == "eng-kb");
        Assert.Equal(2, _search.Requests.Count(r => r.Path.EndsWith("/retrieve", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task One_failing_knowledge_base_yields_partial_results()
    {
        _search.Override = r => r.RequestUri!.AbsolutePath.Contains("eng-kb/retrieve", StringComparison.Ordinal)
            ? FakeSearchService.Status(HttpStatusCode.ServiceUnavailable)
            : null;

        var result = await Provider().SearchAsync(new SearchQuery("policy", null, 20), Alice, default);

        Assert.True(result.Partial);
        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, h => Assert.Equal("hr-kb", h.Collection));
    }

    [Fact]
    public async Task All_knowledge_bases_failing_surfaces_the_error_without_the_body()
    {
        _search.Override = r => r.RequestUri!.AbsolutePath.EndsWith("/retrieve", StringComparison.Ordinal)
            ? FakeSearchService.Status(HttpStatusCode.Forbidden, "{\"secret\":\"do-not-leak\"}")
            : null;

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider().SearchAsync(new SearchQuery("policy", null, 8), Alice, default));

        Assert.Equal(ProviderErrorKind.Forbidden, ex.Kind);
        Assert.Equal(403, ex.SourceStatus);
        Assert.DoesNotContain("do-not-leak", ex.Message);
    }

    [Fact]
    public async Task Unknown_user_token_maps_to_auth()
    {
        var ctx = TestContexts.Create(InMemoryConformanceFixture.Alice, "stale-token", new StaticServiceCredentials(FakeSearchService.ServiceToken));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider().SearchAsync(new SearchQuery("policy", "hr-kb", 8), ctx, default));
        Assert.Equal(ProviderErrorKind.Auth, ex.Kind);
    }

    [Fact]
    public async Task Collection_not_on_the_allowlist_is_not_found_and_not_queried()
    {
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider().SearchAsync(new SearchQuery("policy", "finance-kb", 8), Alice, default));
        Assert.Equal(ProviderErrorKind.NotFound, ex.Kind);
        Assert.Empty(_search.Requests);
    }

    [Fact]
    public async Task List_collections_reads_descriptions_and_caches_them()
    {
        var provider = Provider(o => o.KnowledgeBases[1].Description = "Configured description");

        var first = await provider.ListCollectionsAsync(Alice, default);
        var calls = _search.Requests.Count;
        var second = await provider.ListCollectionsAsync(Bob, default);

        Assert.Equal([new CollectionInfo("hr-kb", "HR policies and benefits"), new CollectionInfo("eng-kb", "Configured description")], first);
        Assert.Equal(first, second);
        Assert.Equal(calls, _search.Requests.Count);
    }

    [Fact]
    public async Task List_collections_degrades_when_a_definition_is_unreadable()
    {
        _search.Override = r => r.RequestUri!.AbsolutePath == "/knowledgebases/eng-kb" ? FakeSearchService.Status(HttpStatusCode.Forbidden) : null;
        var collections = await Provider().ListCollectionsAsync(Alice, default);
        Assert.Equal(["hr-kb", "eng-kb"], collections.Select(c => c.Name));
    }

    [Fact]
    public async Task Fully_configured_sources_skip_discovery()
    {
        var provider = Provider(o =>
        {
            o.KnowledgeBases = [new KnowledgeBaseOptions { Name = "hr-kb", Description = "HR", KnowledgeSources = ["hr-ks"] }];
            o.KnowledgeSources = [new KnowledgeSourceOptions { Name = "hr-ks", Kind = "searchIndex", Index = "hr-index", KeyField = "id" }];
        });

        var hit = (await provider.SearchAsync(new SearchQuery("leave", null, 1), Alice, default)).Hits.Single();
        await provider.FetchAsync(hit.Handle, Alice, default);

        Assert.All(_search.Requests, r => Assert.Equal("POST", r.Method));
    }

    [Fact]
    public async Task Ga_fetch_is_a_key_filtered_search_as_the_user_with_escaping()
    {
        var provider = Provider();
        var hit = (await provider.SearchAsync(new SearchQuery("travel", "hr-kb", 5), Alice, default)).Hits.Single();
        Assert.Equal("hr'3", hit.Handle.Get("docKey"));

        var doc = await provider.FetchAsync(hit.Handle, Alice, default);

        Assert.Equal("Travel policy (O'Brien edition)", doc.Title);
        Assert.Contains("Book travel", doc.Text);
        Assert.Equal("Contoso HR", doc.Metadata.Author);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), doc.Metadata.LastModified);
        var search = _search.Requests.Last(r => r.Path.EndsWith("/docs/search", StringComparison.Ordinal));
        Assert.Equal("id eq 'hr''3'", JsonNode.Parse(search.Body!)!["filter"]!.GetValue<string>());
    }

    [Fact]
    public async Task Preview_fetch_follows_citation_url_after_an_access_check()
    {
        var provider = Provider(o => o.UsePreviewApi = true);
        var hit = (await provider.SearchAsync(new SearchQuery("compensation", "hr-kb", 5), Alice, default)).Hits.Single();
        Assert.Equal("Highly Confidential", hit.Metadata.SensitivityLabel);
        Assert.NotNull(hit.Handle.Get("citationUrl"));

        var doc = await provider.FetchAsync(hit.Handle, Alice, default);
        Assert.Equal("Executive compensation policy", doc.Title);
        Assert.Contains(_search.Requests, r => r.Method == "GET" && r.Path == "/indexes/hr-index/docs/hr-2" && r.UserToken == FakeSearchService.AliceToken);

        // The citation GET in the fake does not trim; the access check must still stop Bob.
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(hit.Handle, Bob, default));
        Assert.Equal(ProviderErrorKind.NotFound, ex.Kind);
        Assert.DoesNotContain(_search.Requests, r => r.Method == "GET" && r.UserToken == FakeSearchService.BobToken);
    }

    [Theory]
    [InlineData("https://evil.example.com/indexes/hr-index/docs/hr-1")]
    [InlineData("http://contoso-search.search.windows.net/indexes/hr-index/docs/hr-1")]
    [InlineData("https://user:pw@contoso-search.search.windows.net/indexes/hr-index/docs/hr-1")]
    [InlineData("https://contoso-search.search.windows.net:8443/indexes/hr-index/docs/hr-1")]
    [InlineData("https://contoso-search.search.windows.net/indexes/eng-index/docs/eng-1")]
    [InlineData("not a url")]
    public async Task Citation_urls_outside_the_configured_source_are_rejected(string citationUrl)
    {
        var provider = Provider(o => o.UsePreviewApi = true);
        var payload = new HandlePayload("hr-kb", new Dictionary<string, string>
        {
            ["kb"] = "hr-kb", ["ks"] = "hr-ks", ["index"] = "hr-index", ["docKey"] = "hr-1", ["citationUrl"] = citationUrl,
        });

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.FetchAsync(payload, Alice, default));

        Assert.Equal(ProviderErrorKind.NotFound, ex.Kind);
        Assert.DoesNotContain(_search.Requests, r => r.Method == "GET" && r.Path.Contains("/docs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remote_source_fetch_returns_the_stored_excerpt_marked_truncated()
    {
        var provider = Provider();
        var hit = (await provider.SearchAsync(new SearchQuery("public page", "eng-kb", 5), Alice, default)).Hits.Single();
        Assert.Equal("https://www.example.org/public-policy", hit.Url!.AbsoluteUri);

        var doc = await provider.FetchAsync(hit.Handle, Alice, default);

        Assert.True(doc.Truncated);
        Assert.StartsWith("A public page about policy.", doc.Text);
        Assert.Equal("Public policy page", doc.Title);
    }

    [Fact]
    public async Task Recorded_retrieve_response_maps_to_hits()
    {
        var provider = Provider(o => o.UsePreviewApi = true);
        var kb = new KnowledgeBaseInfo("hr-kb", "HR", [new("hr-ks", "searchIndex", "hr-index", null), new("web-ks", "web", null, null)]);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieve-response.json")));

        var hits = provider.MapReferences(doc.RootElement, kb);

        Assert.Equal(3, hits.Count);
        var first = hits[0].Hit;
        Assert.Equal("Parental leave policy", first.Title);
        Assert.Equal("https://intranet.contoso.com/hr/leave", first.Url!.AbsoluteUri);
        Assert.Equal("General", first.Metadata.SensitivityLabel);
        Assert.Equal("Contoso HR", first.Metadata.Author);
        Assert.Equal("hr-index", first.Handle.Get("index"));
        Assert.StartsWith("https://contoso-search.search.windows.net/indexes/hr-index/docs/hr-1", first.Handle.Get("citationUrl"));
        Assert.Equal(2.71, hits[0].Score);

        Assert.Equal("hr-7", hits[1].Hit.Title);
        Assert.Equal("Sick leave accrues monthly.", hits[1].Hit.Snippet);

        var web = hits[2].Hit;
        Assert.Equal("Statutory leave overview", web.Title);
        Assert.Null(web.Handle.Get("index"));
        Assert.Equal("Statutory parental leave is 14 weeks.", web.Handle.Get("x"));
    }

    [Fact]
    public async Task Ga_mode_does_not_keep_citation_urls_in_handles()
    {
        var provider = Provider(o => o.UsePreviewApi = false);
        var kb = new KnowledgeBaseInfo("hr-kb", "HR", [new("hr-ks", "searchIndex", "hr-index", null)]);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieve-response.json")));
        Assert.All(provider.MapReferences(doc.RootElement, kb), h => Assert.Null(h.Hit.Handle.Get("citationUrl")));
    }

    [Fact]
    public async Task Search_without_a_user_token_never_calls_search()
    {
        var anonymous = TestContexts.Create(UserIdentity.Anonymous, null, new StaticServiceCredentials(FakeSearchService.ServiceToken));
        await Assert.ThrowsAsync<ProviderException>(() => Provider().SearchAsync(new SearchQuery("policy", null, 8), anonymous, default));
        Assert.DoesNotContain(_search.Requests, r => r.Path.EndsWith("/retrieve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slow_source_is_cancelled_promptly()
    {
        _search.Delay = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().SearchAsync(new SearchQuery("policy", null, 8), Alice, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Index_name_is_found_in_nested_knowledge_source_definitions()
    {
        using var doc = JsonDocument.Parse("""{"name":"ks","kind":"azureBlob","azureBlobParameters":{"createdResources":{"index":"blob-idx","indexer":"x"}}}""");
        Assert.Equal("blob-idx", FoundryIqMetadata.FindIndexName(doc.RootElement));
    }
}
