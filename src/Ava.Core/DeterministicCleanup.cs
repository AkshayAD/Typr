using System.Text.RegularExpressions;

namespace Ava.Core;

public static class DeterministicCleanup
{
    private const string Days = "Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday";
    private const string Months = "January|February|March|April|May|June|July|August|September|October|November|December";
    private const string Correction = @"\s*,?\s*(?:no\s*,?\s+wait|sorry\s*,?\s+I\s+mean|I\s+mean)\s*,?\s*";
    private static readonly Regex DayCorrection = CreateCorrection(Days);
    private static readonly Regex MonthCorrection = CreateCorrection(Months);

    /// <summary>Only unmistakable single-token calendar corrections are collapsed.
    /// Arbitrary false starts, names, numbers, and negations are left intact.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string cleaned = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        cleaned = Regex.Replace(cleaned, @"[^\S\n]+", " ");
        // Several explicit corrections can form a chain, e.g. Monday -> Tuesday -> Friday.
        string previous;
        do
        {
            previous = cleaned;
            cleaned = DayCorrection.Replace(cleaned, "${replacement}");
            cleaned = MonthCorrection.Replace(cleaned, "${replacement}");
        } while (cleaned != previous);
        return cleaned;
    }

    private static Regex CreateCorrection(string words) => new(
        $@"\b(?:{words})\b{Correction}\b(?<replacement>{words})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
