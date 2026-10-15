using FederatedConnector.Abstractions;

namespace FederatedConnector.Conformance;

public enum ConformanceUser
{
    /// <summary>Can see the restricted document.</summary>
    A,
    /// <summary>Cannot see the restricted document.</summary>
    B,
    /// <summary>No user credential at all.</summary>
    Anonymous,
}

/// <summary>
/// What a provider supplies to run the conformance kit. Typically backed by recorded or simulated source traffic,
/// or (opt-in) by a live tenant configured from the environment.
/// </summary>
public interface IConformanceFixture
{
    IKnowledgeProvider Provider { get; }

    /// <summary>A context for the given user. For <see cref="ConformanceUser.Anonymous"/>, the token source must be unavailable.</summary>
    ProviderContext CreateContext(ConformanceUser user);

    /// <summary>A query for which user A finds the restricted document and user B does not.</summary>
    string RestrictedQuery { get; }

    /// <summary>A query that returns at least one result for user A (may equal <see cref="RestrictedQuery"/>).</summary>
    string CommonQuery { get; }

    /// <summary>Identifies the restricted document among hits.</summary>
    bool IsRestrictedDocument(SearchHit hit);

    /// <summary>Payloads that point outside the configured source (other host, non-allowlisted collection, …). Fetch must reject all.</summary>
    IEnumerable<HandlePayload> ForeignPayloads { get; }

    /// <summary>Throws if the source observed any write operation.</summary>
    void AssertNoWrites();
}
