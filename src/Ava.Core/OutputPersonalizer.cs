using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ava.Core;

/// <summary>Literal, single-pass corrections to completed recognition; never rewrites Raw output.</summary>
public static class OutputPersonalizer
{
    public const int MaxRules = 200;
    public const int MaxPhraseLength = 200;
    public const int MaxReplacementLength = 10_000;
    // Marks and connector punctuation belong to words too, including decomposed Unicode text.
    private const string WordCharacter = @"[\p{L}\p{M}\p{N}\p{Pc}\u200C\u200D]";

    public static string Transform(string raw, bool clean, IReadOnlyList<TextReplacement> vocabulary,
        IReadOnlyList<TextReplacement> snippets)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!clean) return raw.Trim();

        string text = DeterministicCleanup.Clean(raw);
        // Snippets come first so they win identical triggers. Sorting then prefers the longest match.
        var rules = NormalizeRules(snippets).Concat(NormalizeRules(vocabulary))
            .DistinctBy(rule => rule.Phrase, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(rule => rule.Phrase.Length).ToArray();
        if (rules.Length == 0 || text.Length == 0) return text;

        string alternatives = string.Join("|", rules.Select((rule, index) =>
        {
            // A configured "5" must not change part of a decimal, time, date, or fraction.
            string before = char.IsNumber(rule.Phrase, 0) ? @"(?<!\p{N}[.,:/])" : "";
            string after = char.IsNumber(rule.Phrase, rule.Phrase.Length - 1) ? @"(?![.,:/]\p{N})" : "";
            return $"{before}(?<r{index}>{Regex.Escape(rule.Phrase)}){after}";
        }));
        var expression = new Regex($"(?<!{WordCharacter})(?:{alternatives})(?!{WordCharacter})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromMilliseconds(100));
        try
        {
            return expression.Replace(text, match =>
            {
                // .NET regex character classes use UTF-16 code units. Check full runes too,
                // so a supplementary Unicode letter cannot be mistaken for a word boundary.
                if (Rune.DecodeLastFromUtf16(text.AsSpan(0, match.Index), out var previous, out _) == OperationStatus.Done && IsWordRune(previous) ||
                    Rune.DecodeFromUtf16(text.AsSpan(match.Index + match.Length), out var next, out _) == OperationStatus.Done && IsWordRune(next))
                    return match.Value;
                for (int index = 0; index < rules.Length; index++)
                    if (match.Groups[index + 1].Success) return rules[index].Replacement;
                return match.Value;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // Preserve the completed transcript if a very large input exceeds the matching budget.
            return text;
        }
    }

    private static bool IsWordRune(Rune rune) => Rune.IsLetterOrDigit(rune) || Rune.IsNumber(rune) ||
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation || rune.Value is 0x200C or 0x200D;

    internal static TextReplacement[] NormalizeRules(IEnumerable<TextReplacement>? rules)
    {
        if (rules == null) return [];
        var normalized = new List<TextReplacement>();
        var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule == null) continue;
            string? phrase = rule.Phrase?.Trim();
            string? replacement = rule.Replacement?.Trim();
            if (string.IsNullOrEmpty(phrase) || string.IsNullOrEmpty(replacement) ||
                phrase.Length > MaxPhraseLength || replacement.Length > MaxReplacementLength ||
                !phrases.Add(phrase)) continue;
            normalized.Add(new TextReplacement(phrase, replacement));
            if (normalized.Count == MaxRules) break;
        }
        return normalized.Count == 0 ? [] : normalized.ToArray();
    }
}
