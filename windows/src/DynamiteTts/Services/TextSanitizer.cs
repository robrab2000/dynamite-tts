using System;
using System.Text.RegularExpressions;

namespace DynamiteTts.Services;

public static class TextSanitizer
{
    private static readonly Regex MarkdownLinkRegex = new(@"\[([^\]]+)\]\((?:https?://[^\s\)]+|[^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex RawUrlRegex = new(@"(?:https?://|www\.)[^\s<>()]+", RegexOptions.Compiled);
    private static readonly Regex CodeBlockFenceRegex = new(@"```[a-zA-Z0-9_\-]*\n?", RegexOptions.Compiled);
    private static readonly Regex HeaderHashesRegex = new(@"^\s*#{1,6}\s+", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex BulletMarkerRegex = new(@"^\s*[-*+•]\s+", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex BoldItalicRegex = new(@"(\*\*|__|\*|_)(.*?)\1", RegexOptions.Compiled);
    private static readonly Regex ExcessiveWhitespaceRegex = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex ExcessiveNewlinesRegex = new(@"\n{3,}", RegexOptions.Compiled);

    public static string Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var text = input;

        // 1. Convert Markdown links [Title](url) -> Title
        text = MarkdownLinkRegex.Replace(text, "$1");

        // 2. Replace remaining raw URLs with a natural utterance
        text = RawUrlRegex.Replace(text, match =>
        {
            var url = match.Value;
            try
            {
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }
                var uri = new Uri(url);
                var host = uri.Host.Replace("www.", "");
                return $"link to {host}";
            }
            catch
            {
                return "link";
            }
        });

        // 3. Remove code block markers and inline code ticks
        text = CodeBlockFenceRegex.Replace(text, "");
        text = text.Replace("```", "").Replace("`", "");

        // 4. Strip markdown header hashes and bullet markers
        text = HeaderHashesRegex.Replace(text, "");
        text = BulletMarkerRegex.Replace(text, "");

        // 5. Strip bold/italic markers
        text = BoldItalicRegex.Replace(text, "$2");

        // 6. Normalize newlines and excessive whitespace
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        text = ExcessiveNewlinesRegex.Replace(text, "\n\n");
        text = ExcessiveWhitespaceRegex.Replace(text, " ");

        return text.Trim();
    }
}
