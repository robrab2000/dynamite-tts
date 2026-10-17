using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamiteTts.Services;

public static class SentenceChunker
{
    private const int MaxChunkSize = 500;
    private static readonly Regex SentenceSplitter = new(@"(?<=[.!?])\s+|\n+", RegexOptions.Compiled);

    public static IReadOnlyList<string> SplitIntoChunks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var trimmed = text.Trim();
        if (trimmed.Length <= MaxChunkSize)
        {
            return new[] { trimmed };
        }

        var sentences = SentenceSplitter.Split(trimmed);
        var chunks = new List<string>();
        var currentChunk = new StringBuilder();

        foreach (var sentence in sentences)
        {
            var part = sentence.Trim();
            if (string.IsNullOrEmpty(part)) continue;

            if (part.Length > MaxChunkSize)
            {
                // If a single sentence exceeds MaxChunkSize, split by punctuation or words
                if (currentChunk.Length > 0)
                {
                    chunks.Add(currentChunk.ToString().Trim());
                    currentChunk.Clear();
                }

                var subChunks = SplitLargeSentence(part, MaxChunkSize);
                chunks.AddRange(subChunks);
                continue;
            }

            if (currentChunk.Length + part.Length + 1 > MaxChunkSize)
            {
                if (currentChunk.Length > 0)
                {
                    chunks.Add(currentChunk.ToString().Trim());
                    currentChunk.Clear();
                }
            }

            if (currentChunk.Length > 0)
                currentChunk.Append(' ');

            currentChunk.Append(part);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString().Trim());
        }

        return chunks;
    }

    private static IEnumerable<string> SplitLargeSentence(string sentence, int limit)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();

        foreach (var word in words)
        {
            if (sb.Length + word.Length + 1 > limit)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                }
            }

            if (sb.Length > 0)
                sb.Append(' ');

            sb.Append(word);
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString().Trim();
        }
    }
}
