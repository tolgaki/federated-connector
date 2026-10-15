using System.Diagnostics;
using System.Text.Json;
using FederatedConnector.Abstractions;
using FederatedConnector.Host.Handles;
using FederatedConnector.Host.Tools;
using Xunit;

namespace FederatedConnector.Conformance;

/// <summary>
/// The provider conformance suite (design §3.1 and §7). Derive a sealed class in your provider's test project:
/// <code>public sealed class MyProviderConformance(MyFixture f) : ProviderConformanceTests&lt;MyFixture&gt;(f);</code>
/// </summary>
public abstract class ProviderConformanceTests<TFixture>(TFixture fixture) : IClassFixture<TFixture>
    where TFixture : class, IConformanceFixture
{
    protected TFixture Fixture { get; } = fixture;

    private IKnowledgeProvider Provider => Fixture.Provider;

    private bool NeedsUser => Provider.Descriptor.Auth is not AuthRequirement.None;

    private static readonly HandleCodec Codec = new(new byte[32], null, TimeProvider.System, TimeSpan.FromHours(1));

    [Fact]
    public void Descriptor_is_well_formed()
    {
        var d = Provider.Descriptor;
        Assert.False(string.IsNullOrWhiteSpace(d.Id));
        Assert.Equal(d.Id.ToLowerInvariant(), d.Id);
        Assert.DoesNotContain('.', d.Id);
        Assert.False(string.IsNullOrWhiteSpace(d.ContentDescription));
        Assert.False(string.IsNullOrWhiteSpace(d.CollectionNoun));
        if (d.Auth is AuthRequirement.EntraObo obo)
        {
            Assert.NotEmpty(obo.DownstreamScopes);
        }
    }

    [Fact]
    public void Host_exposes_only_read_only_annotated_tools()
    {
        var catalog = new ToolCatalog("Conformance", Provider.Descriptor);
        Assert.Equal(["search", "fetch", "list_collections"], catalog.Tools.Select(t => t.Name));
        Assert.All(catalog.Tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.True(t.Hints.ReadOnly);
            Assert.True(t.Hints.Idempotent);
            Assert.False(t.Hints.OpenWorld);
            Assert.False(t.Hints.Destructive);
        });
    }

    [Fact]
    public async Task List_collections_returns_named_collections()
    {
        var collections = await Provider.ListCollectionsAsync(Fixture.CreateContext(ConformanceUser.A), Ct());
        Assert.NotEmpty(collections);
        Assert.All(collections, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Fact]
    public async Task Handle_round_trip_fetches_the_searched_document()
    {
        var ctx = Fixture.CreateContext(ConformanceUser.A);
        var hits = (await Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 8), ctx, Ct())).Hits;
        Assert.NotEmpty(hits);

        foreach (var hit in hits)
        {
            var id = Codec.Encode(Provider.Descriptor.Id, hit.Handle, ctx.User);
            Assert.Equal(HandleDecodeStatus.Valid, Codec.TryDecode(id, Provider.Descriptor.Id, ctx.User, out var payload));
            var doc = await Provider.FetchAsync(payload!, ctx, Ct());
            Assert.False(string.IsNullOrWhiteSpace(doc.Title));
            Assert.False(string.IsNullOrWhiteSpace(doc.Text));
        }
    }

    [Fact]
    public async Task Payloads_are_small()
    {
        var ctx = Fixture.CreateContext(ConformanceUser.A);
        var hits = (await Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 20), ctx, Ct())).Hits;
        Assert.All(hits, h => Assert.True(JsonSerializer.SerializeToUtf8Bytes(h.Handle.Data).Length <= 1024, "Handle payload exceeds 1 KB."));
    }

    [Fact]
    public async Task Handle_issued_to_one_user_is_rejected_for_another()
    {
        var a = Fixture.CreateContext(ConformanceUser.A);
        var b = Fixture.CreateContext(ConformanceUser.B);
        if (a.User.HashedId == b.User.HashedId)
        {
            return; // auth mode None: no user binding
        }

        var hit = (await Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 1), a, Ct())).Hits.First();
        var id = Codec.Encode(Provider.Descriptor.Id, hit.Handle, a.User);
        Assert.Equal(HandleDecodeStatus.WrongUser, Codec.TryDecode(id, Provider.Descriptor.Id, b.User, out _));
    }

    /// <summary>
    /// The two-user permission test: A finds document X, B doesn't, and B can't fetch it even with A's payload
    /// (bypassing the host's user binding), because fetch re-reads as the user.
    /// </summary>
    [Fact]
    public async Task Two_user_permission_test()
    {
        var a = Fixture.CreateContext(ConformanceUser.A);
        var b = Fixture.CreateContext(ConformanceUser.B);
        var query = new SearchQuery(Fixture.RestrictedQuery, null, 20);

        var restricted = (await Provider.SearchAsync(query, a, Ct())).Hits.FirstOrDefault(Fixture.IsRestrictedDocument);
        Assert.NotNull(restricted);

        var bHits = (await Provider.SearchAsync(query, b, Ct())).Hits;
        Assert.DoesNotContain(bHits, Fixture.IsRestrictedDocument);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Provider.FetchAsync(restricted!.Handle, b, Ct()));
        Assert.Contains(ex.Kind, new[] { ProviderErrorKind.NotFound, ProviderErrorKind.Forbidden });
    }

    [Fact]
    public async Task Fetch_rejects_foreign_payloads()
    {
        var ctx = Fixture.CreateContext(ConformanceUser.A);
        var foreign = Fixture.ForeignPayloads.ToList();
        Assert.NotEmpty(foreign);
        foreach (var payload in foreign)
        {
            await Assert.ThrowsAsync<ProviderException>(() => Provider.FetchAsync(payload, ctx, Ct()));
        }
    }

    [Fact]
    public async Task Fetch_rejects_tampered_payloads()
    {
        var ctx = Fixture.CreateContext(ConformanceUser.A);
        var hit = (await Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 1), ctx, Ct())).Hits.First();

        foreach (var key in hit.Handle.Data.Keys)
        {
            var data = new Dictionary<string, string>(hit.Handle.Data) { [key] = "tampered-" + hit.Handle.Data[key] + "-\u0000'" };
            var tampered = hit.Handle with { Data = data };
            await AssertRejectedOrUnchanged(tampered, ctx, hit);
        }

        var otherCollection = hit.Handle with { Collection = "tampered-collection" };
        await Assert.ThrowsAsync<ProviderException>(() => Provider.FetchAsync(otherCollection, ctx, Ct()));
    }

    /// <summary>A tampered value must either be rejected, or (for non-identifying fields) not change what is read.</summary>
    private async Task AssertRejectedOrUnchanged(HandlePayload tampered, ProviderContext ctx, SearchHit original)
    {
        try
        {
            var doc = await Provider.FetchAsync(tampered, ctx, Ct());
            var expected = await Provider.FetchAsync(original.Handle, ctx, Ct());
            Assert.Equal(expected.Text, doc.Text);
        }
        catch (ProviderException)
        {
            // rejected: fine
        }
    }

    [Fact]
    public async Task Search_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 8), Fixture.CreateContext(ConformanceUser.A), cts.Token);
            Assert.True(result.Partial || result.Hits.Count == 0, "A cancelled search must be flagged partial or throw.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "Cancellation was not honoured promptly.");
    }

    [Fact]
    public async Task Fetch_honours_cancellation()
    {
        var ctx = Fixture.CreateContext(ConformanceUser.A);
        var hit = (await Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 1), ctx, Ct())).Hits.First();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.FetchAsync(hit.Handle, ctx, cts.Token));
    }

    [Fact]
    public async Task Search_without_user_credential_fails_closed()
    {
        if (!NeedsUser)
        {
            return;
        }

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 8), Fixture.CreateContext(ConformanceUser.Anonymous), Ct()));
        Assert.Equal(ProviderErrorKind.Auth, ex.Kind);
    }

    [Fact]
    public async Task Fetch_without_user_credential_fails_closed()
    {
        if (!NeedsUser)
        {
            return;
        }

        var hit = (await Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 1), Fixture.CreateContext(ConformanceUser.A), Ct())).Hits.First();
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider.FetchAsync(hit.Handle, Fixture.CreateContext(ConformanceUser.Anonymous), Ct()));
        Assert.Equal(ProviderErrorKind.Auth, ex.Kind);
    }

    [Fact]
    public async Task Source_is_never_written()
    {
        var ctx = Fixture.CreateContext(ConformanceUser.A);
        await Provider.ListCollectionsAsync(ctx, Ct());
        var hits = (await Provider.SearchAsync(new SearchQuery(Fixture.CommonQuery, null, 8), ctx, Ct())).Hits;
        foreach (var hit in hits)
        {
            await Provider.FetchAsync(hit.Handle, ctx, Ct());
        }

        Fixture.AssertNoWrites();
    }

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
