using FederatedConnector.Abstractions;
using FederatedConnector.Conformance;

namespace FederatedConnector.Host.Tests;

/// <summary>Self-test of the conformance kit against the in-memory reference provider.</summary>
public sealed class InMemoryProviderConformance(InMemoryConformanceFixture fixture)
    : ProviderConformanceTests<InMemoryConformanceFixture>(fixture);

/// <summary>The kit must catch a provider that ignores permissions on fetch (replays without re-checking).</summary>
public sealed class ConformanceKitCatchesViolations
{
    [Fact]
    public async Task Two_user_test_fails_for_a_provider_that_does_not_recheck_on_fetch()
    {
        var fixture = new LeakyFixture();
        var suite = new LeakySuite(fixture);
        await Assert.ThrowsAnyAsync<Exception>(suite.Two_user_permission_test);
    }

    private sealed class LeakySuite(LeakyFixture f) : ProviderConformanceTests<LeakyFixture>(f);

    public sealed class LeakyFixture : IConformanceFixture
    {
        private readonly InMemoryConformanceFixture _inner = new();

        public IKnowledgeProvider Provider => new LeakyProvider(_inner.Provider);
        public string RestrictedQuery => _inner.RestrictedQuery;
        public string CommonQuery => _inner.CommonQuery;
        public IEnumerable<HandlePayload> ForeignPayloads => _inner.ForeignPayloads;
        public ProviderContext CreateContext(ConformanceUser user) => _inner.CreateContext(user);
        public bool IsRestrictedDocument(SearchHit hit) => _inner.IsRestrictedDocument(hit);
        public void AssertNoWrites() => _inner.AssertNoWrites();
    }

    /// <summary>Fetches with user A's context regardless of who asks: a permission leak.</summary>
    private sealed class LeakyProvider(IKnowledgeProvider inner) : IKnowledgeProvider
    {
        public ProviderDescriptor Descriptor => inner.Descriptor;
        public Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(ProviderContext ctx, CancellationToken ct) => inner.ListCollectionsAsync(ctx, ct);
        public Task<SearchResultSet> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct) => inner.SearchAsync(query, ctx, ct);
        public Task<DocumentContent> FetchAsync(HandlePayload handle, ProviderContext ctx, CancellationToken ct) =>
            inner.FetchAsync(handle, TestContexts.Create(InMemoryConformanceFixture.Alice, InMemoryConformanceFixture.TokenA), ct);
    }
}
