using System;

namespace DynamiteTts.Services;

/// <summary>
/// Helpers for deciding when Explain should request surrounding context and how to format it for the LLM.
/// </summary>
public static class SelectionContextHelper
{
    /// <summary>Selections at or below this length (after trim) are treated as short and may need context.</summary>
    public const int ShortSelectionMaxChars = 80;

    /// <summary>Hard cap on surrounding context sent to the chat model.</summary>
    public const int MaxContextChars = 1500;

    public static bool IsShortSelection(string? selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;
        return selectedText.Trim().Length <= ShortSelectionMaxChars;
    }

    /// <summary>
    /// Returns surrounding context only when it adds information beyond the selection itself.
    /// </summary>
    public static string? NormalizeContext(string selectedText, string? surroundingContext)
    {
        if (string.IsNullOrWhiteSpace(surroundingContext))
            return null;

        var selection = selectedText.Trim();
        var context = surroundingContext.Trim();
        if (context.Length == 0)
            return null;

        if (string.Equals(context, selection, StringComparison.OrdinalIgnoreCase))
            return null;

        // Context that is barely longer than the selection is usually not useful.
        if (context.Length <= selection.Length + 8)
            return null;

        if (context.Length > MaxContextChars)
            context = context[..MaxContextChars].TrimEnd() + "…";

        return context;
    }

    public static string BuildExplainUserMessage(string selectedText, string? surroundingContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedText);

        var selection = selectedText.Trim();
        var context = NormalizeContext(selection, surroundingContext);
        if (context == null)
            return selection;

        // Lead with the sentence/paragraph so small models ground the sense before seeing the bare term.
        return
            "Surrounding text:\n" +
            context +
            "\n\nWithin that text, explain only this highlighted word or phrase (not the whole passage):\n" +
            selection;
    }
}
