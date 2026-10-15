using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FederatedConnector.Abstractions;

namespace FederatedConnector.ContentExtraction;

/// <summary>
/// Dependency-free extractor for common text formats and DOCX. PDF is not supported in v1; providers should
/// return the source's own text rendition (or an excerpt with <c>Truncated = true</c>) when extraction returns null.
/// </summary>
public sealed partial class BasicContentExtractor : IContentExtractor
{
    public const long MaxInputBytes = 32 * 1024 * 1024;

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain", "text/markdown", "text/csv", "application/json", "application/xml", "text/xml",
    };

    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public bool CanExtract(string mediaType)
    {
        var mt = Normalise(mediaType);
        return TextTypes.Contains(mt) || mt == "text/html" || mt == DocxType;
    }

    public async Task<string?> ExtractTextAsync(Stream content, string mediaType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var mt = Normalise(mediaType);
        if (!CanExtract(mt))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await CopyBoundedAsync(content, buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        if (mt == DocxType)
        {
            return ExtractDocx(buffer);
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return mt == "text/html" ? HtmlToText(text) : text;
    }

    private static string Normalise(string mediaType)
    {
        var semi = mediaType.IndexOf(';');
        return (semi >= 0 ? mediaType[..semi] : mediaType).Trim().ToLowerInvariant();
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxInputBytes)
            {
                throw new InvalidDataException("Document exceeds the extraction size limit.");
            }

            await target.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    internal static string HtmlToText(string html)
    {
        var noScripts = ScriptOrStyle().Replace(html, " ");
        var withBreaks = BlockTags().Replace(noScripts, "\n");
        var noTags = AnyTag().Replace(withBreaks, " ");
        return WebUtility.HtmlDecode(noTags);
    }

    internal static string? ExtractDocx(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        while (!xml.EOF)
        {
            if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == W && xml.LocalName == "t")
            {
                // ReadElementContentAsString already advances to the next node.
                sb.Append(xml.ReadElementContentAsString());
                continue;
            }

            if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == W)
            {
                switch (xml.LocalName)
                {
                    case "tab":
                        sb.Append('\t');
                        break;
                    case "br":
                        sb.Append('\n');
                        break;
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.NamespaceURI == W && xml.LocalName == "p")
            {
                sb.Append('\n');
            }

            xml.Read();
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();
}
