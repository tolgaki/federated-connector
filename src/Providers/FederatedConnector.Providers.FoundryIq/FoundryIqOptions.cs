namespace FederatedConnector.Providers.FoundryIq;

/// <summary>Configuration section <c>Providers:FoundryIq</c>.</summary>
public sealed class FoundryIqOptions
{
    /// <summary>Azure AI Search endpoint, e.g. https://contoso.search.windows.net.</summary>
    public Uri? Endpoint { get; set; }

    public string ContentDescription { get; set; } = "documents from Foundry IQ knowledge bases";

    /// <summary>GA API version: intents and extractive output only.</summary>
    public string ApiVersion { get; set; } = "2026-04-01";

    /// <summary>Preview API version: adds citationUrl and sensitivity labels.</summary>
    public string PreviewApiVersion { get; set; } = "2026-08-01-preview";

    /// <summary>Use the preview API for retrieve (citationUrl, labels). Fetch then follows citationUrl.</summary>
    public bool UsePreviewApi { get; set; }

    public string OutputMode { get; set; } = "extractiveData";

    /// <summary>The allowlist. Only these knowledge bases are searched, listed or fetched from.</summary>
    public List<KnowledgeBaseOptions> KnowledgeBases { get; set; } = [];

    /// <summary>Optional per-knowledge-source overrides; a fully specified entry skips discovery.</summary>
    public List<KnowledgeSourceOptions> KnowledgeSources { get; set; } = [];

    /// <summary>Knowledge source kinds never queried or returned. Work IQ sources would loop back into Copilot.</summary>
    public string[] ExcludedKnowledgeSourceKinds { get; set; } = ["workIQ"];

    public string[] TitleFields { get; set; } = ["title", "name", "metadata_title", "metadata_storage_name", "fileName"];

    public string[] ContentFields { get; set; } = ["content", "chunk", "text", "merged_content", "snippet", "description"];

    public string[] UrlFields { get; set; } = ["url", "docUrl", "blobUrl", "sourceUrl", "metadata_storage_path"];

    public string[] LastModifiedFields { get; set; } = ["lastModified", "metadata_storage_last_modified", "last_modified", "modifiedDateTime"];

    public string[] AuthorFields { get; set; } = ["author", "metadata_author", "createdBy"];

    /// <summary>
    /// When fetching through citationUrl, also confirm access with a search filtered to the document key under the
    /// user's token. Keep true until it is verified that citationUrl applies document-level trimming.
    /// </summary>
    public bool VerifyCitationUrlTrimming { get; set; } = true;

    public int MetadataCacheMinutes { get; set; } = 10;

    public int MaxSnippetChars { get; set; } = 600;

    /// <summary>Excerpt kept in the handle for remote (non-indexed) sources, which cannot be re-read.</summary>
    public int MaxPayloadExcerptChars { get; set; } = 300;

    public string ServiceScope { get; set; } = "https://search.azure.com/.default";

    public string UserScope { get; set; } = "https://search.azure.com/user_impersonation";
}

public sealed class KnowledgeBaseOptions
{
    public string Name { get; set; } = "";

    /// <summary>Overrides the description read from the knowledge base definition.</summary>
    public string? Description { get; set; }

    /// <summary>Optional: narrow to these knowledge sources. Empty means all non-excluded sources.</summary>
    public string[] KnowledgeSources { get; set; } = [];
}

public sealed class KnowledgeSourceOptions
{
    public string Name { get; set; } = "";
    public string? Kind { get; set; }
    public string? Index { get; set; }
    public string? KeyField { get; set; }
}
