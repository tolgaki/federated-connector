using System.Text.Json;
using FederatedConnector.Abstractions;
using FederatedConnector.ContentExtraction;
using FederatedConnector.Host.Mcp;
using FederatedConnector.Host.Tools;

namespace FederatedConnector.Host.Tests;

public sealed class ToolCatalogTests
{
    private static readonly ProviderDescriptor Descriptor =
        new("foundry-iq", "HR policies and engineering runbooks", new AuthRequirement.EntraObo(["s"]), "knowledge base");

    [Fact]
    public void Tools_have_fixed_names_titles_and_read_only_annotations()
    {
        var catalog = new ToolCatalog("Contoso Knowledge", Descriptor);

        Assert.Equal(["search", "fetch", "list_collections"], catalog.Tools.Select(t => t.Name));
        Assert.Equal("Search Contoso Knowledge", catalog.Find("search")!.Title);
        Assert.Equal("Read Contoso Knowledge document", catalog.Find("fetch")!.Title);
        Assert.Equal("List Contoso Knowledge knowledge bases", catalog.Find("list_collections")!.Title);
        Assert.All(catalog.Tools, t => Assert.Equal(new ToolHints(true, true, false, false), t.Hints));
    }

    [Fact]
    public void Search_description_carries_the_content_description()
    {
        var search = new ToolCatalog("Contoso Knowledge", Descriptor).Find("search")!;
        Assert.Contains("Search Contoso Knowledge (HR policies and engineering runbooks)", search.Description);
        Assert.Contains("fetch", search.Description);
    }

    [Fact]
    public void Search_schema_requires_query_and_bounds_top()
    {
        var schema = new ToolCatalog("X", Descriptor).Find("search")!.InputSchema;
        Assert.Equal("query", schema.GetProperty("required")[0].GetString());
        var top = schema.GetProperty("properties").GetProperty("top");
        Assert.Equal(1, top.GetProperty("minimum").GetInt32());
        Assert.Equal(20, top.GetProperty("maximum").GetInt32());
        Assert.Equal(8, top.GetProperty("default").GetInt32());
    }

    [Fact]
    public void Mcp_mapping_sets_annotations()
    {
        var tool = McpWiring.ToMcpTool(new ToolCatalog("X", Descriptor).Find("fetch")!);
        Assert.Equal("Read X document", tool.Title);
        Assert.True(tool.Annotations!.ReadOnlyHint);
        Assert.True(tool.Annotations.IdempotentHint);
        Assert.False(tool.Annotations.OpenWorldHint);
        Assert.False(tool.Annotations.DestructiveHint);
        Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
    }

    [Theory]
    [InlineData("knowledge base", "knowledge bases")]
    [InlineData("shared drive", "shared drives")]
    [InlineData("library", "libraries")]
    [InlineData("workspace", "workspaces")]
    [InlineData("index", "indexes")]
    public void Pluralise(string noun, string expected) => Assert.Equal(expected, ToolCatalog.Pluralise(noun));
}

public sealed class TextTests
{
    [Fact]
    public void Normalise_collapses_whitespace_and_strips_control_characters()
    {
        Assert.Equal("a b\n\nc", TextNormalizer.Normalise("  a \t  b\r\n\r\n\r\n\r\n   c\u0007  "));
    }

    [Fact]
    public void Truncate_does_not_split_surrogate_pairs()
    {
        var (text, truncated) = TextNormalizer.Truncate("ab😀cd", 3);
        Assert.True(truncated);
        Assert.Equal("ab", text);
    }

    [Fact]
    public async Task Html_is_converted_to_text()
    {
        var extractor = new BasicContentExtractor();
        using var stream = new MemoryStream("<html><script>x()</script><p>Hello &amp; welcome</p><p>Bye</p></html>"u8.ToArray());
        var text = await extractor.ExtractTextAsync(stream, "text/html; charset=utf-8", CancellationToken.None);
        Assert.Equal("Hello & welcome\nBye", TextNormalizer.Normalise(text));
    }

    [Fact]
    public async Task Docx_text_is_extracted()
    {
        using var docx = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(docx, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            await w.WriteAsync("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                <w:p><w:r><w:t>First paragraph</w:t></w:r></w:p><w:p><w:r><w:t>Second</w:t></w:r></w:p>
                </w:body></w:document>
                """);
        }

        docx.Position = 0;
        var text = await new BasicContentExtractor().ExtractTextAsync(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", CancellationToken.None);
        Assert.Equal("First paragraph\nSecond", TextNormalizer.Normalise(text));
    }

    [Fact]
    public async Task Unsupported_media_type_returns_null()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        Assert.Null(await new BasicContentExtractor().ExtractTextAsync(stream, "application/pdf", CancellationToken.None));
    }
}
