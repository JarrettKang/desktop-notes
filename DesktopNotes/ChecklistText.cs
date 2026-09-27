using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopNotes;

// Checklist markers live in the text, so copying, undo and the existing file format
// preserve both the content and its checked state without separate line indexes.
public static class ChecklistText
{
    public const char Unchecked = '\u2610';
    public const char Checked = '\u2611';

    public readonly record struct Edit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength);

    public static bool HasBox(string text, int lineStart) =>
        lineStart >= 0 && lineStart + 1 < text.Length &&
        (lineStart == 0 || text[lineStart - 1] is '\r' or '\n') &&
        text[lineStart] is Unchecked or Checked && text[lineStart + 1] == ' ';

    public static IEnumerable<int> LineStarts(string text)
    {
        yield return 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                yield return i + 1;
            }
            else if (text[i] == '\n') yield return i + 1;
        }
    }

    public static int LineStartAt(string text, int position)
    {
        var result = 0;
        foreach (var start in LineStarts(text))
        {
            if (start > position) break;
            result = start;
        }
        return result;
    }

    public static Edit ToggleBoxes(string text, int selectionStart, int selectionLength)
    {
        var first = LineStartAt(text, selectionStart);
        var lastPosition = selectionLength == 0 ? selectionStart : selectionStart + selectionLength - 1;
        var last = LineStartAt(text, lastPosition);
        var starts = new List<int>();
        var allHaveBoxes = true;
        foreach (var start in LineStarts(text))
            if (start >= first && start <= last)
            {
                starts.Add(start);
                allHaveBoxes &= HasBox(text, start);
            }
        var end = last;
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        var replacement = new StringBuilder();
        var cursor = first;
        var changes = new List<(int Position, int Delta)>();
        foreach (var start in starts)
        {
            replacement.Append(text, cursor, start - cursor);
            cursor = start;
            if (allHaveBoxes)
            {
                cursor += 2;
                changes.Add((start, -2));
            }
            else if (!HasBox(text, start))
            {
                replacement.Append(Unchecked).Append(' ');
                changes.Add((start, 2));
            }
        }
        replacement.Append(text, cursor, end - cursor);
        int Map(int position)
        {
            var mapped = position;
            foreach (var (at, delta) in changes)
            {
                if (at > position) break;
                mapped += delta > 0 ? delta : -Math.Min(2, position - at);
            }
            return mapped;
        }
        var newStart = Map(selectionStart);
        var newEnd = Map(selectionStart + selectionLength);
        return new Edit(first, end - first, replacement.ToString(), newStart, newEnd - newStart);
    }
}
