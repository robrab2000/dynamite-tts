using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class SentenceChunkerTests
{
    [Fact]
    public void SplitIntoChunks_EmptyText_ReturnsEmpty()
    {
        var result = SentenceChunker.SplitIntoChunks("");
        Assert.Empty(result);
    }

    [Fact]
    public void SplitIntoChunks_ShortText_ReturnsSingleChunk()
    {
        var text = "Hello world! This is a simple test sentence.";
        var result = SentenceChunker.SplitIntoChunks(text);

        Assert.Single(result);
        Assert.Equal(text, result[0]);
    }

    [Fact]
    public void SplitIntoChunks_LongText_SplitsOnSentenceBoundaries()
    {
        var sentence = "This is a meaningful sentence that provides clear and concise information to the reader. ";
        var longText = string.Concat(Enumerable.Repeat(sentence, 12));

        var result = SentenceChunker.SplitIntoChunks(longText);

        Assert.True(result.Count > 1);
        foreach (var chunk in result)
        {
            Assert.True(chunk.Length <= 500, $"Chunk length {chunk.Length} exceeded 500 characters.");
            Assert.False(string.IsNullOrWhiteSpace(chunk));
        }
    }
}
