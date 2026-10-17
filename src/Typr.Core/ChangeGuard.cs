using System.Text.RegularExpressions;

namespace Typr.Core;

public sealed record ChangeGuardResult(bool Accepted, string Text, string? Reason, double ContentChangeRatio);

public static class ChangeGuard
{
    private static readonly Regex Words = new(@"[\p{L}\p{M}]+(?:'[\p{L}\p{M}]+)?", RegexOptions.CultureInvariant);
    private static readonly Regex Numbers = new(@"(?:[$€£¥]\s*)?[+\-−]?\d+(?:[.,:/\-]\d+)*(?:\s?%)?", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    { "a", "an", "the", "to", "of", "in", "on", "at", "and", "or", "is", "are", "was", "were", "be", "been", "am", "will", "um", "uh" };
    private static readonly HashSet<string> NegativeWords = new(StringComparer.Ordinal)
    { "no", "not", "never", "neither", "nor", "without", "hardly", "barely", "rarely", "unless", "except" };
    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.Ordinal)
    {
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4",
        ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
        ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12", ["thirteen"] = "13", ["fourteen"] = "14",
        ["fifteen"] = "15", ["sixteen"] = "16", ["seventeen"] = "17", ["eighteen"] = "18", ["nineteen"] = "19",
        ["twenty"] = "20", ["thirty"] = "30", ["forty"] = "40", ["fifty"] = "50", ["sixty"] = "60",
        ["seventy"] = "70", ["eighty"] = "80", ["ninety"] = "90", ["hundred"] = "hundred",
        ["thousand"] = "thousand", ["million"] = "million", ["billion"] = "billion"
    };

    /// <summary>Apply to polished dictation, not intentional voice-edit rewrites.
    /// Unsafe results return the exact cleaned input. This is a heuristic, not semantic proof.</summary>
    public static ChangeGuardResult Evaluate(string cleanedInput, string candidate, double maxChangeRatio = 0.2)
    {
        ArgumentNullException.ThrowIfNull(cleanedInput);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!double.IsFinite(maxChangeRatio) || maxChangeRatio < 0 || maxChangeRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(maxChangeRatio));
        if (string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(cleanedInput))
            return Reject("Empty result");
        if (!NumericTokens(cleanedInput).SequenceEqual(NumericTokens(candidate)))
            return Reject("Numeric content changed");
        var sourceWords = Tokenize(cleanedInput);
        var outputWords = Tokenize(candidate);
        if (!Negations(sourceWords).SequenceEqual(Negations(outputWords)))
            return Reject("Negation changed");
        var source = ContentWords(sourceWords);
        var output = ContentWords(outputWords);
        // Ordered overlap catches substantial reordering as well as omission/invention.
        int overlap = LongestCommonSubsequence(source, output);
        double ratio = (source.Length + output.Length - 2d * overlap) / Math.Max(1, source.Length);
        return ratio > maxChangeRatio
            ? new(false, cleanedInput, "Too many content words changed", ratio)
            : new(true, candidate, null, ratio);

        ChangeGuardResult Reject(string reason) => new(false, cleanedInput, reason, 1);
    }

    private static string[] Tokenize(string text) => Words.Matches(text.Replace('’', '\'').ToLowerInvariant())
        .Select(m => m.Value).ToArray();
    private static string[] ContentWords(string[] words) => words
        .Where(w => !FunctionWords.Contains(w) && !NumberWords.ContainsKey(w))
        .Select(w => w switch { "can't" or "cannot" => "can", "won't" => "will", _ when w.EndsWith("n't", StringComparison.Ordinal) => w[..^3], _ => w })
        .Where(w => !FunctionWords.Contains(w) && !NegativeWords.Contains(w)).ToArray();
    private static IEnumerable<string> Negations(string[] words) => words
        .Where(w => NegativeWords.Contains(w) || w == "cannot" || w.EndsWith("n't", StringComparison.Ordinal))
        .Select(w => w == "cannot" || w.EndsWith("n't", StringComparison.Ordinal) ? "not" : w);
    private static IEnumerable<string> NumericTokens(string text)
    {
        var tokens = new List<(int Index, string Value)>();
        foreach (Match match in Numbers.Matches(text))
            tokens.Add((match.Index, Regex.Replace(match.Value, @"\s", "")));
        foreach (Match match in Words.Matches(text.ToLowerInvariant()))
            if (NumberWords.TryGetValue(match.Value, out var value)) tokens.Add((match.Index, value));
        return tokens.OrderBy(t => t.Index).Select(t => t.Value);
    }
    private static int LongestCommonSubsequence(string[] left, string[] right)
    {
        // Bound quadratic work for unexpectedly enormous provider responses: reject
        // non-identical long text conservatively instead of freezing the UI thread.
        if ((long)left.Length * right.Length > 4_000_000)
            return left.SequenceEqual(right) ? left.Length : 0;
        var row = new int[right.Length + 1];
        for (int i = 0; i < left.Length; i++)
        {
            int diagonal = 0;
            for (int j = 0; j < right.Length; j++)
            {
                int old = row[j + 1];
                row[j + 1] = left[i] == right[j] ? diagonal + 1 : Math.Max(row[j], old);
                diagonal = old;
            }
        }
        return row[^1];
    }
}
