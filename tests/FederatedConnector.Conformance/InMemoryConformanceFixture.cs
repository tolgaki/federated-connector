using FederatedConnector.Abstractions;
using FederatedConnector.Host.Auth;
using Xunit;

namespace FederatedConnector.Conformance;

/// <summary>Fixture for <see cref="InMemoryKnowledgeProvider"/>; also a template for real provider fixtures.</summary>
public sealed class InMemoryConformanceFixture : IConformanceFixture
{
    public const string TokenA = "token-alice";
    public const string TokenB = "token-bob";

    public static readonly UserIdentity Alice = new(UserHash.FromEntra("tenant-1", "alice"), "tenant-1", "Alice");
    public static readonly UserIdentity Bob = new(UserHash.FromEntra("tenant-1", "bob"), "tenant-1", "Bob");

    public InMemoryConformanceFixture()
        : this(null)
    {
    }

    public static InMemoryConformanceFixture WithAuth(AuthRequirement auth) => new(auth);

    private InMemoryConformanceFixture(AuthRequirement? auth)
    {
        Memory = new InMemoryKnowledgeProvider(SampleDocuments(), new Dictionary<string, string> { [TokenA] = "alice", [TokenB] = "bob" }, auth);
    }

    public InMemoryKnowledgeProvider Memory { get; }

    public IKnowledgeProvider Provider => Memory;

    public string RestrictedQuery => "compensation";

    public string CommonQuery => "policy";

    public static IEnumerable<InMemoryDocument> SampleDocuments() =>
    [
        new("hr-1", "hr", "Parental leave policy", "Employees receive 20 weeks of parental leave under this policy.", Url: new Uri("https://intranet.example.com/hr/leave"), Author: "HR"),
        new("hr-2", "hr", "Executive compensation policy", "Confidential compensation bands for executives.", new HashSet<string> { "alice" }, Label: "Confidential"),
        new("eng-1", "engineering", "Deployment runbook", "How to deploy the service and the rollback policy."),
    ];

    public ProviderContext CreateContext(ConformanceUser user) => user switch
    {
        ConformanceUser.A => TestContexts.Create(Alice, TokenA),
        ConformanceUser.B => TestContexts.Create(Bob, TokenB),
        _ => TestContexts.Create(UserIdentity.Anonymous, null),
    };

    public bool IsRestrictedDocument(SearchHit hit) => hit.Handle.Get("doc") == "hr-2";

    public IEnumerable<HandlePayload> ForeignPayloads =>
    [
        new("other-collection", new Dictionary<string, string> { ["doc"] = "hr-1" }),
        new(null, new Dictionary<string, string> { ["doc"] = "hr-1" }),
        new("hr", new Dictionary<string, string> { ["doc"] = "https://evil.example.com/hr-1" }),
    ];

    public void AssertNoWrites()
    {
        lock (Memory.Operations)
        {
            Assert.All(Memory.Operations, op => Assert.Contains(op, new[] { "list", "search", "fetch" }));
        }
    }
}
