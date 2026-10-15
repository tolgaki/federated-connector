namespace FederatedConnector.Abstractions;

/// <summary>
/// The data-source side of a federated connector. A provider owns only how to search, read and list
/// one kind of source, using the credentials the host hands it. Everything Copilot sees, and everything
/// security-critical (token validation, handle signing, limits, error sanitising), belongs to the host.
/// </summary>
/// <remarks>
/// Provider obligations (checked by the conformance kit):
/// 1. Read only. 2. Query as the user. 3. Re-check access on fetch. 4. Validate handle payloads.
/// 5. Keep payloads small and secret-free. 6. Honour cancellation and timeouts.
/// </remarks>
public interface IKnowledgeProvider
{
    ProviderDescriptor Descriptor { get; }

    Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(ProviderContext ctx, CancellationToken ct);

    Task<SearchResultSet> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct);

    Task<DocumentContent> FetchAsync(HandlePayload handle, ProviderContext ctx, CancellationToken ct);
}
