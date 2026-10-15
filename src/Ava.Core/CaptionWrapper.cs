using System.Globalization;
using System.Text.RegularExpressions;

namespace Ava.Core;

public sealed record CaptionLine(string Text, double Opacity);

public static class CaptionWrapper
{
    /// <summary>Deterministic word wrapping, measured in grapheme clusters rather
    /// than pixels. The view may choose a character budget after measuring its font.
    /// Only the newest one to three lines are retained, with the newest at the bottom.</summary>
    public static IReadOnlyList<CaptionLine> Wrap(string text, int maxCharactersPerLine = 60, int maxLines = 2)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxCharactersPerLine < 1) throw new ArgumentOutOfRangeException(nameof(maxCharactersPerLine));
        if (maxLines is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(maxLines));
        var lines = new List<string>();
        string current = "";
        int currentLength = 0;
        void Flush()
        {
            if (current.Length > 0) lines.Add(current);
            current = "";
            currentLength = 0;
        }
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            foreach (Match wordMatch in Regex.Matches(paragraph, @"\S+"))
            {
                string word = wordMatch.Value;
                var elements = StringInfo.ParseCombiningCharacters(word);
                if (currentLength > 0 && currentLength + 1 + elements.Length > maxCharactersPerLine) Flush();
                if (elements.Length <= maxCharactersPerLine)
                {
                    if (currentLength > 0) { current += " "; currentLength++; }
                    current += word;
                    currentLength += elements.Length;
                    continue;
                }
                // A long token cannot overflow the caption area; never cut a surrogate
                // pair, combining mark sequence, or emoji ZWJ grapheme in half.
                for (int start = 0; start < elements.Length; start += maxCharactersPerLine)
                {
                    int end = Math.Min(start + maxCharactersPerLine, elements.Length);
                    current = word[elements[start]..(end == elements.Length ? word.Length : elements[end])];
                    currentLength = end - start;
                    if (end < elements.Length) Flush();
                }
            }
            Flush();
        }
        var visible = lines.TakeLast(maxLines).ToArray();
        return visible.Select((line, index) => new CaptionLine(line, index == visible.Length - 1 ? 1 : .7)).ToArray();
    }
}
