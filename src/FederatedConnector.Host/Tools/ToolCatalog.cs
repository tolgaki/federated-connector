using System.Text.Json;
using FederatedConnector.Abstractions;

namespace FederatedConnector.Host.Tools;

public sealed record ToolHints(bool ReadOnly, bool Idempotent, bool OpenWorld, bool Destructive);

public sealed record ToolDefinition(string Name, string Title, string Description, JsonElement InputSchema, ToolHints Hints);

/// <summary>
/// The fixed Copilot-facing tool surface. Names and input shapes are the same for every provider; titles and
/// descriptions are filled in from the connector display name and the provider descriptor.
/// </summary>
public sealed class ToolCatalog
{
    public const string Search = "search";
    public const string Fetch = "fetch";
    public const string ListCollections = "list_collections";
    public const int MinTop = 1;
    public const int MaxTop = 20;
    public const int DefaultTop = 8;

    private static readonly ToolHints ReadOnlyHints = new(ReadOnly: true, Idempotent: true, OpenWorld: false, Destructive: false);

    public ToolCatalog(string displayName, ProviderDescriptor descriptor)
    {
        var noun = descriptor.CollectionNoun;
        var nouns = Pluralise(noun);
        Tools =
        [
            new ToolDefinition(
                Search,
                $"Search {displayName}",
                $"Search {displayName} ({descriptor.ContentDescription}). Returns ranked results with ids; call `fetch` with an id to read the full document. " +
                $"Optionally restrict to one {noun} by name (see `list_collections`).",
                Schema($$"""
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "What to search for, in natural language or keywords." },
                    "collection": { "type": "string", "description": "Optional {{noun}} name from list_collections. Omit to search all." },
                    "top": { "type": "integer", "minimum": {{MinTop}}, "maximum": {{MaxTop}}, "default": {{DefaultTop}}, "description": "Maximum number of results." }
                  },
                  "required": ["query"],
                  "additionalProperties": false
                }
                """),
                ReadOnlyHints),
            new ToolDefinition(
                Fetch,
                $"Read {displayName} document",
                $"Read the full text of a {displayName} document, using an id returned by `search`. Long documents are truncated.",
                Schema("""
                {
                  "type": "object",
                  "properties": {
                    "id": { "type": "string", "description": "The id of a search result." }
                  },
                  "required": ["id"],
                  "additionalProperties": false
                }
                """),
                ReadOnlyHints),
            new ToolDefinition(
                ListCollections,
                $"List {displayName} {nouns}",
                $"List the {nouns} available in {displayName}, with descriptions. Pass a name as `collection` to `search` to narrow results.",
                Schema("""
                {
                  "type": "object",
                  "properties": {},
                  "additionalProperties": false
                }
                """),
                ReadOnlyHints),
        ];
    }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public ToolDefinition? Find(string name) => Tools.FirstOrDefault(t => t.Name == name);

    internal static string Pluralise(string noun)
    {
        if (noun.EndsWith('s') || noun.EndsWith('x') || noun.EndsWith("ch", StringComparison.Ordinal) || noun.EndsWith("sh", StringComparison.Ordinal))
        {
            return noun + "es";
        }

        if (noun.Length > 1 && noun.EndsWith('y') && !"aeiou".Contains(noun[^2]))
        {
            return noun[..^1] + "ies";
        }

        return noun + "s";
    }

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
