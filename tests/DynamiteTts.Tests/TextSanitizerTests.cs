using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class TextSanitizerTests
{
    [Fact]
    public void Sanitize_MarkdownLink_ExtractsTitleOnly()
    {
        var input = "Check out the [Lemonade Server Documentation](https://lemonade-server.ai/docs/api/openai/) for details.";
        var sanitized = TextSanitizer.Sanitize(input);

        Assert.Equal("Check out the Lemonade Server Documentation for details.", sanitized);
    }

    [Fact]
    public void Sanitize_RawUrl_ReplacesWithCleanPhrase()
    {
        var input = "Visit https://github.com/lemonade-sdk/lemonade for updates.";
        var sanitized = TextSanitizer.Sanitize(input);

        Assert.Contains("link to github.com", sanitized);
        Assert.DoesNotContain("https://", sanitized);
    }

    [Fact]
    public void Sanitize_CodeBlocksAndTicks_RemovesFencesAndBackticks()
    {
        var input = "Use the `RegisterHotKey` API within ```csharp var x = 1; ``` cleanly.";
        var sanitized = TextSanitizer.Sanitize(input);

        Assert.DoesNotContain("`", sanitized);
        Assert.Contains("RegisterHotKey", sanitized);
    }

    [Fact]
    public void Sanitize_MarkdownHeadersAndBullets_StripsClutter()
    {
        var input = "### Important Note\n* First item\n* Second item\n- Third item";
        var sanitized = TextSanitizer.Sanitize(input);

        Assert.DoesNotContain("###", sanitized);
        Assert.DoesNotContain("*", sanitized);
        Assert.Contains("Important Note", sanitized);
        Assert.Contains("First item", sanitized);
    }
}
