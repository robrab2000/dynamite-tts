using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace DynamiteTts.Services;

/// <summary>
/// Reads the focused control's text selection and expands it to a paragraph/nearby lines via UI Automation.
/// Fails soft when the target app does not expose TextPattern (common in some browsers/Electron apps).
/// Note: the managed TextUnit enum has no Sentence; paragraph/line are the available enclosing units.
/// </summary>
public static class UiAutomationSelectionContext
{
    public static Task<string?> TryGetSurroundingContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return TryGetSurroundingContextCore();
            }
            catch (Exception ex)
            {
                AppLog.Warn("UI Automation surrounding context failed", ex);
                return null;
            }
        }, cancellationToken);
    }

    internal static string? TryGetSurroundingContextCore()
    {
        var textPattern = FindTextPattern(AutomationElement.FocusedElement);
        if (textPattern == null)
            return null;

        var selections = textPattern.GetSelection();
        if (selections == null || selections.Length == 0)
            return null;

        var selectedRange = selections[0];
        var selected = SafeGetText(selectedRange.Clone());
        if (string.IsNullOrWhiteSpace(selected))
            return null;

        // Prefer paragraph (managed TextUnit has no Sentence).
        var paragraphRange = selectedRange.Clone();
        paragraphRange.ExpandToEnclosingUnit(TextUnit.Paragraph);
        var paragraph = SafeGetText(paragraphRange)?.Trim();
        if (SelectionContextHelper.NormalizeContext(selected, paragraph) != null)
            return paragraph;

        // Fall back to the selected line plus one line of padding on each side when available.
        var lineRange = selectedRange.Clone();
        lineRange.ExpandToEnclosingUnit(TextUnit.Line);
        try
        {
            lineRange.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Line, -1);
            lineRange.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Line, 1);
        }
        catch
        {
            // Some providers reject MoveEndpointByUnit; keep the single line.
        }

        return SelectionContextHelper.NormalizeContext(selected, SafeGetText(lineRange));
    }

    private static TextPattern? FindTextPattern(AutomationElement? start)
    {
        var current = start;
        for (var depth = 0; current != null && depth < 12; depth++)
        {
            try
            {
                if (current.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
                    patternObj is TextPattern textPattern)
                {
                    return textPattern;
                }
            }
            catch
            {
                // Element may have gone stale; keep walking.
            }

            try
            {
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? SafeGetText(TextPatternRange range)
    {
        try
        {
            return range.GetText(SelectionContextHelper.MaxContextChars + 64);
        }
        catch
        {
            return null;
        }
    }
}
