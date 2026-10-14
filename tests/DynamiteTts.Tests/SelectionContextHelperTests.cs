using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class SelectionContextHelperTests
{
    [Theory]
    [InlineData("Lemonade", true)]
    [InlineData("mutex", true)]
    [InlineData("A whole sentence that is clearly longer than eighty characters of selected text for explain mode.", false)]
    public void IsShortSelection_UsesCharThreshold(string text, bool expected)
    {
        Assert.Equal(expected, SelectionContextHelper.IsShortSelection(text));
    }

    [Fact]
    public void NormalizeContext_ReturnsNull_WhenSameAsSelection()
    {
        Assert.Null(SelectionContextHelper.NormalizeContext("Lemonade", "Lemonade"));
    }

    [Fact]
    public void NormalizeContext_ReturnsSurroundingSentence()
    {
        var context = SelectionContextHelper.NormalizeContext(
            "Lemonade",
            "Explain path: Lemonade chat with adaptive prompt.");
        Assert.Equal("Explain path: Lemonade chat with adaptive prompt.", context);
    }

    [Fact]
    public void BuildExplainUserMessage_IncludesContextBlock()
    {
        var message = SelectionContextHelper.BuildExplainUserMessage(
            "Lemonade",
            "Explain path: Lemonade chat with adaptive prompt.");

        Assert.StartsWith("Surrounding text:", message);
        Assert.Contains("Lemonade chat with adaptive prompt", message);
        Assert.Contains("explain only this highlighted word or phrase", message);
        Assert.Contains("Lemonade", message);
        // Context should appear before the bare term is isolated.
        Assert.True(message.IndexOf("Surrounding text:", StringComparison.Ordinal) <
                    message.IndexOf("highlighted word or phrase", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildExplainUserMessage_WithoutUsefulContext_IsSelectionOnly()
    {
        Assert.Equal("Lemonade", SelectionContextHelper.BuildExplainUserMessage("Lemonade", null));
        Assert.Equal("Lemonade", SelectionContextHelper.BuildExplainUserMessage("Lemonade", "Lemonade"));
    }
}
