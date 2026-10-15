using FederatedConnector.Abstractions;
using FederatedConnector.Conformance;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FederatedConnector.Providers.FoundryIq.Tests;

/// <summary>Runs the conformance kit against the Foundry IQ provider over the simulated Search service.</summary>
public sealed class FoundryIqConformanceFixture : IConformanceFixture
{
    public FoundryIqConformanceFixture()
        : this(usePreview: false)
    {
    }

    private FoundryIqConformanceFixture(bool usePreview)
    {
        Search = new FakeSearchService();
        Options = DefaultOptions();
        Options.UsePreviewApi = usePreview;
        Foundry = CreateProvider(Search, Options);
    }

    public static FoundryIqConformanceFixture Preview() => new(usePreview: true);

    public FakeSearchService Search { get; }

    public FoundryIqOptions Options { get; }

    public FoundryIqProvider Foundry { get; }

    public IKnowledgeProvider Provider => Foundry;

    public string RestrictedQuery => "compensation";

    public string CommonQuery => "policy";

    public static FoundryIqOptions DefaultOptions() => new()
    {
        Endpoint = FakeSearchService.Endpoint,
        KnowledgeBases =
        [
            new KnowledgeBaseOptions { Name = "hr-kb" },
            new KnowledgeBaseOptions { Name = "eng-kb" },
        ],
    };

    public static FoundryIqProvider CreateProvider(FakeSearchService search, FoundryIqOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), new FakeHttpClientFactory(search), new MemoryCache(new MemoryCacheOptions()), NullLogger<FoundryIqProvider>.Instance);

    public ProviderContext CreateContext(ConformanceUser user) => user switch
    {
        ConformanceUser.A => TestContexts.Create(InMemoryConformanceFixture.Alice, FakeSearchService.AliceToken, new StaticServiceCredentials(FakeSearchService.ServiceToken)),
        ConformanceUser.B => TestContexts.Create(InMemoryConformanceFixture.Bob, FakeSearchService.BobToken, new StaticServiceCredentials(FakeSearchService.ServiceToken)),
        _ => TestContexts.Create(UserIdentity.Anonymous, null, new StaticServiceCredentials(FakeSearchService.ServiceToken)),
    };

    public bool IsRestrictedDocument(SearchHit hit) => hit.Handle.Get("docKey") == "hr-2";

    public IEnumerable<HandlePayload> ForeignPayloads =>
    [
        // Knowledge base not on the allowlist.
        Payload("finance-kb", "fin-ks", "fin-index", "f-1"),
        // Index that does not back the knowledge source.
        Payload("hr-kb", "hr-ks", "eng-index", "eng-1"),
        // Excluded (Work IQ) knowledge source.
        Payload("hr-kb", "wiq-ks", null, "x"),
        // Knowledge source from a different knowledge base.
        Payload("hr-kb", "eng-ks", "eng-index", "eng-1"),
        // Collection that disagrees with the payload.
        new("eng-kb", new Dictionary<string, string> { ["kb"] = "hr-kb", ["ks"] = "hr-ks", ["index"] = "hr-index", ["docKey"] = "hr-1" }),
    ];

    private static HandlePayload Payload(string kb, string ks, string? index, string docKey)
    {
        var data = new Dictionary<string, string> { ["kb"] = kb, ["ks"] = ks, ["docKey"] = docKey };
        if (index is not null)
        {
            data["index"] = index;
        }

        return new HandlePayload(kb, data);
    }

    public void AssertNoWrites()
    {
        lock (Search.Requests)
        {
            Assert.All(Search.Requests, r =>
            {
                Assert.Contains(r.Method, new[] { "GET", "POST" });
                if (r.Method == "POST")
                {
                    Assert.True(r.Path.EndsWith("/retrieve", StringComparison.Ordinal) || r.Path.EndsWith("/docs/search", StringComparison.Ordinal), $"Unexpected POST {r.Path}");
                }
            });
        }
    }
}

public sealed class FoundryIqConformance(FoundryIqConformanceFixture fixture)
    : ProviderConformanceTests<FoundryIqConformanceFixture>(fixture);

public sealed class FoundryIqPreviewConformanceFixture : IConformanceFixture
{
    private readonly FoundryIqConformanceFixture _inner = FoundryIqConformanceFixture.Preview();

    public FoundryIqPreviewConformanceFixture() => _inner.Search.CitationGetTrims = false;

    public IKnowledgeProvider Provider => _inner.Provider;
    public string RestrictedQuery => _inner.RestrictedQuery;
    public string CommonQuery => _inner.CommonQuery;
    public ProviderContext CreateContext(ConformanceUser user) => _inner.CreateContext(user);
    public bool IsRestrictedDocument(SearchHit hit) => _inner.IsRestrictedDocument(hit);

    public IEnumerable<HandlePayload> ForeignPayloads =>
        _inner.ForeignPayloads.Append(new HandlePayload("hr-kb", new Dictionary<string, string>
        {
            ["kb"] = "hr-kb", ["ks"] = "hr-ks", ["index"] = "hr-index", ["docKey"] = "hr-1",
            ["citationUrl"] = "https://evil.example.com/indexes/hr-index/docs/hr-1",
        }));

    public void AssertNoWrites() => _inner.AssertNoWrites();
}

/// <summary>Preview API path (citationUrl). Citation GETs in the fake do not trim, so this proves the access re-check.</summary>
public sealed class FoundryIqPreviewConformance(FoundryIqPreviewConformanceFixture fixture)
    : ProviderConformanceTests<FoundryIqPreviewConformanceFixture>(fixture);
