namespace FederatedConnector.Abstractions;

public sealed record CollectionInfo(string Name, string? Description);

/// <param name="Text">The user's query text. Never logged by the host.</param>
/// <param name="Collection">Optional collection to restrict to; null means all allowlisted collections.</param>
/// <param name="Top">Maximum number of hits (the host clamps to 1–20).</param>
/// <param name="Filters">Reserved for v2 provider-specific filters. Always null in v1.</param>
public sealed record SearchQuery(
    string Text,
    string? Collection,
    int Top,
    IReadOnlyDictionary<string, string>? Filters = null);

/// <param name="Hits">Ranked hits, best first.</param>
/// <param name="Partial">True when some collections or pages were skipped (timeout, cancellation, transient failure).</param>
public sealed record SearchResultSet(IReadOnlyList<SearchHit> Hits, bool Partial = false)
{
    public static SearchResultSet Empty { get; } = new([]);
}

public sealed record SearchHit(
    HandlePayload Handle,
    string Title,
    string Snippet,
    Uri? Url,
    string? Collection,
    DocumentMetadata Metadata);

/// <param name="Truncated">Set by the provider when it can only return part of the document (for example a stored excerpt).
/// The host also sets it when it truncates to its own limit.</param>
public sealed record DocumentContent(
    string Title,
    Uri? Url,
    string Text,
    string? MediaType,
    DocumentMetadata Metadata,
    bool Truncated = false);

public sealed record DocumentMetadata(
    DateTimeOffset? LastModified = null,
    string? Author = null,
    string? SensitivityLabel = null,
    IReadOnlyDictionary<string, string>? Extra = null)
{
    public static DocumentMetadata Empty { get; } = new();
}

/// <summary>
/// The provider's opaque part of a result handle. The host signs it (it does not encrypt it), binds it to the
/// user and to the provider id, and gives it back on fetch. Keep it small and free of secrets.
/// The provider must validate it before use (obligation 4): never treat a value as a URL to follow blindly.
/// </summary>
public sealed record HandlePayload(string? Collection, IReadOnlyDictionary<string, string> Data)
{
    public string? Get(string key) => Data.TryGetValue(key, out var v) ? v : null;
}
