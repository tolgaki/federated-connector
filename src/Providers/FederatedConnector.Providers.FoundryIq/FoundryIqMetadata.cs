using System.Text.Json;
using FederatedConnector.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace FederatedConnector.Providers.FoundryIq;

internal sealed record KnowledgeSourceInfo(string Name, string Kind, string? Index, string? KeyField)
{
    public bool IsIndexed => !string.IsNullOrEmpty(Index);
}

internal sealed record KnowledgeBaseInfo(string Name, string? Description, IReadOnlyList<KnowledgeSourceInfo> Sources)
{
    public KnowledgeSourceInfo? Source(string? name) =>
        name is null ? null : Sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// Reads knowledge base and knowledge source definitions (service credential only; definitions are not user data)
/// and caches them. Config overrides take precedence and, when complete, avoid discovery calls entirely.
/// </summary>
internal sealed class FoundryIqMetadata(FoundryIqOptions options, IMemoryCache cache)
{
    private TimeSpan CacheFor => TimeSpan.FromMinutes(Math.Max(1, options.MetadataCacheMinutes));

    public KnowledgeBaseOptions? Allowlisted(string? name) =>
        name is null ? null : options.KnowledgeBases.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.Ordinal));

    public async Task<KnowledgeBaseInfo> GetAsync(KnowledgeBaseOptions kb, SearchServiceClient client, ProviderContext ctx, CancellationToken ct)
    {
        var key = "fiq:kb:" + kb.Name;
        if (cache.TryGetValue(key, out KnowledgeBaseInfo? cached) && cached is not null)
        {
            return cached;
        }

        string? description = kb.Description;
        IEnumerable<string> sourceNames = kb.KnowledgeSources;
        if (kb.KnowledgeSources.Length == 0)
        {
            using var doc = await client.GetKnowledgeBaseAsync(kb.Name, ctx, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            description ??= Json.String(root, "description");
            sourceNames = Json.Array(root, "knowledgeSources")
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : Json.String(e, "name"))
                .OfType<string>()
                .ToList();
        }
        else if (description is null)
        {
            // Sources are configured, so the definition is only needed for its description. Least-privilege
            // deployments may not be allowed to read definitions; that must not break search or fetch.
            try
            {
                using var doc = await client.GetKnowledgeBaseAsync(kb.Name, ctx, ct).ConfigureAwait(false);
                description = Json.String(doc.RootElement, "description");
            }
            catch (ProviderException ex) when (ex.Kind is ProviderErrorKind.Forbidden or ProviderErrorKind.Auth or ProviderErrorKind.NotFound)
            {
                description = null;
            }
        }

        var sources = new List<KnowledgeSourceInfo>();
        foreach (var name in sourceNames.Distinct(StringComparer.Ordinal))
        {
            var ks = await GetSourceAsync(name, client, ctx, ct).ConfigureAwait(false);
            if (!options.ExcludedKnowledgeSourceKinds.Contains(ks.Kind, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(ks);
            }
        }

        var info = new KnowledgeBaseInfo(kb.Name, description, sources);
        cache.Set(key, info, CacheFor);
        return info;
    }

    public async Task<string> GetKeyFieldAsync(KnowledgeSourceInfo ks, SearchServiceClient client, ProviderContext ctx, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ks.KeyField))
        {
            return ks.KeyField;
        }

        var key = "fiq:key:" + ks.Index;
        if (cache.TryGetValue(key, out string? cached) && cached is not null)
        {
            return cached;
        }

        using var doc = await client.GetIndexAsync(ks.Index!, ctx, ct).ConfigureAwait(false);
        var field = Json.Array(doc.RootElement, "fields")
            .FirstOrDefault(f => f.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.True);
        var name = field.ValueKind == JsonValueKind.Object ? Json.String(field, "name") : null;
        if (string.IsNullOrEmpty(name))
        {
            throw new ProviderException(ProviderErrorKind.Unavailable, "Index has no key field.");
        }

        cache.Set(key, name, CacheFor);
        return name;
    }

    private async Task<KnowledgeSourceInfo> GetSourceAsync(string name, SearchServiceClient client, ProviderContext ctx, CancellationToken ct)
    {
        var configured = options.KnowledgeSources.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.Ordinal));
        if (configured?.Kind is { } kind && (configured.Index is not null || !IsIndexedKind(kind)))
        {
            return new KnowledgeSourceInfo(name, kind, configured.Index, configured.KeyField);
        }

        var key = "fiq:ks:" + name;
        if (cache.TryGetValue(key, out KnowledgeSourceInfo? cached) && cached is not null)
        {
            return cached;
        }

        using var doc = await client.GetKnowledgeSourceAsync(name, ctx, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var discoveredKind = configured?.Kind ?? Json.String(root, "kind") ?? "unknown";
        var index = configured?.Index ?? FindIndexName(root);
        var info = new KnowledgeSourceInfo(name, discoveredKind, index, configured?.KeyField);
        cache.Set(key, info, CacheFor);
        return info;
    }

    private static bool IsIndexedKind(string kind) =>
        !kind.Equals("web", StringComparison.OrdinalIgnoreCase) &&
        !kind.Equals("remoteSharePoint", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the backing index in a knowledge source definition (searchIndexName, or a created index resource).</summary>
    internal static string? FindIndexName(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String &&
                (p.Name.Equals("searchIndexName", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("index", StringComparison.OrdinalIgnoreCase)))
            {
                return p.Value.GetString();
            }
        }

        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Object && FindIndexName(p.Value) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

internal static class Json
{
    public static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    /// <summary>First non-empty string among <paramref name="names"/>; string arrays are joined.</summary>
    public static string? FirstText(JsonElement e, IEnumerable<string> names, string separator = "\n\n")
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v))
            {
                continue;
            }

            if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            {
                return v.GetString();
            }

            if (v.ValueKind == JsonValueKind.Array)
            {
                var parts = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();
                if (parts.Count > 0)
                {
                    return string.Join(separator, parts);
                }
            }
        }

        return null;
    }
}
