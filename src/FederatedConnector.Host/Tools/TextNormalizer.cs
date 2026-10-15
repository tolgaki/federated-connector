using System.Text;

namespace FederatedConnector.Host.Tools;

/// <summary>Host-side text normalisation applied to every snippet and document.</summary>
public static class TextNormalizer
{
    public static string Normalise(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var newlines = 0;
        var pendingSpace = false;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            var ch = raw == '\t' ? ' ' : raw;
            if (ch == '\n')
            {
                pendingSpace = false;
                if (newlines < 2 && sb.Length > 0)
                {
                    sb.Append('\n');
                }

                newlines++;
                continue;
            }

            if (char.IsControl(ch) || ch == '\uFEFF')
            {
                continue;
            }

            if (ch == ' ')
            {
                pendingSpace = sb.Length > 0 && newlines == 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            newlines = 0;
            sb.Append(ch);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Cuts at <paramref name="max"/> characters without splitting a surrogate pair.</summary>
    public static (string Text, bool Truncated) Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return (text, false);
        }

        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return (text[..cut], true);
    }
}
