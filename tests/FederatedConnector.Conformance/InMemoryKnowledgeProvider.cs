using FederatedConnector.Abstractions;

namespace FederatedConnector.Conformance;

/// <summary>
/// A minimal, conforming in-memory provider. It is the executable example of the contract, a test double for host
/// tests, and the self-test of the conformance kit. Permissions are enforced from the user's token, the way a real
/// source would, not from the host-supplied identity.
/// </summary>
public sealed class InMemoryKnowledgeProvider : IKnowledgeProvider
{
    private readonly IReadOnlyList<InMemoryDocument> _documents;
    private readonly IReadOnlyDictionary<string, string> _tokenToUser;

    public InMemoryKnowledgeProvider(
        IEnumerable<InMemoryDocument> documents,
        IReadOnlyDictionary<string, string> tokenToUser,
        AuthRequirement? auth = null,
        IReadOnlyDictionary<string, string>? collectionDescriptions = null)
    {
        _documents = documents.ToList();
        _tokenToUser = tokenToUser;
        Collections = collectionDescriptions ?? _documents.Select(d => d.Collection).Distinct().ToDictionary(c => c, c => $"The {c} collection");
        Descriptor = new ProviderDescriptor(
            "in-memory",
            "sample documents held in memory",
            auth ?? new AuthRequirement.EntraObo(["api://in-memory/.default"]),
            "collection");
    }

    public ProviderDescriptor Descriptor { get; }

    public IReadOnlyDictionary<string, string> Collections { get; }

    /// <summary>Optional artificial latency, to exercise timeouts.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Every operation performed against the "source".</summary>
    public List<string> Operations { get; } = [];

    public Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(ProviderContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Record("list");
        return Task.FromResult<IReadOnlyList<CollectionInfo>>(Collections.Select(kv => new CollectionInfo(kv.Key, kv.Value)).ToList());
    }

    public async Task<SearchResultSet> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var user = await UserAsync(ctx, ct);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        Record("search");
        if (query.Collection is not null && !Collections.ContainsKey(query.Collection))
        {
            throw new ProviderException(ProviderErrorKind.NotFound, "Unknown collection.");
        }

        var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToArray();
        var hits = _documents
            .Where(d => query.Collection is null || d.Collection == query.Collection)
            .Where(d => d.IsVisibleTo(user))
            .Where(d => terms.Any(t => (d.Title + " " + d.Text).Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(query.Top)
            .Select(d => new SearchHit(
                new HandlePayload(d.Collection, new Dictionary<string, string> { ["doc"] = d.Id }),
                d.Title,
                d.Text.Length > 200 ? d.Text[..200] : d.Text,
                d.Url,
                d.Collection,
                new DocumentMetadata(d.LastModified, d.Author, d.Label)))
            .ToList();
        return new SearchResultSet(hits);
    }

    public async Task<DocumentContent> FetchAsync(HandlePayload handle, ProviderContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var user = await UserAsync(ctx, ct);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        Record("fetch");
        if (handle.Collection is null || !Collections.ContainsKey(handle.Collection))
        {
            throw ProviderException.InvalidHandle("unknown collection");
        }

        var id = handle.Get("doc");
        var doc = _documents.FirstOrDefault(d => d.Id == id && d.Collection == handle.Collection);
        if (doc is null || !doc.IsVisibleTo(user))
        {
            throw new ProviderException(ProviderErrorKind.NotFound, "Document not visible.");
        }

        return new DocumentContent(doc.Title, doc.Url, doc.Text, "text/plain", new DocumentMetadata(doc.LastModified, doc.Author, doc.Label));
    }

    private async ValueTask<string?> UserAsync(ProviderContext ctx, CancellationToken ct)
    {
        if (Descriptor.Auth is AuthRequirement.None)
        {
            return null;
        }

        if (!ctx.UserToken.IsAvailable)
        {
            throw new ProviderException(ProviderErrorKind.Auth, "User credential required.");
        }

        var token = await ctx.UserToken.GetTokenAsync(null, ct);
        return _tokenToUser.TryGetValue(token, out var user)
            ? user
            : throw new ProviderException(ProviderErrorKind.Auth, "Token rejected by source.") { SourceStatus = 401 };
    }

    private void Record(string op)
    {
        lock (Operations)
        {
            Operations.Add(op);
        }
    }
}

/// <param name="AllowedUsers">Null means visible to everyone.</param>
public sealed record InMemoryDocument(
    string Id,
    string Collection,
    string Title,
    string Text,
    IReadOnlySet<string>? AllowedUsers = null,
    Uri? Url = null,
    DateTimeOffset? LastModified = null,
    string? Author = null,
    string? Label = null)
{
    public bool IsVisibleTo(string? user) => AllowedUsers is null || (user is not null && AllowedUsers.Contains(user));
}
