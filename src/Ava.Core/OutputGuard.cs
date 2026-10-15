using System.Text.RegularExpressions;

namespace Ava.Core;

public static class OutputGuard
{
    private static readonly Regex Preamble = new(
        @"^(?:sure|here(?:'s| is) (?:the |your )?(?:(?:cleaned|polished|revised|rewritten|edited|formatted|requested) )?(?:text|version|rewrite|response|email|message))\s*:\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MetaTail = new(
        @"^(?:Hope this helps[.!]?|Let me know if you (?:want|need|would like) (?:any |more |further )?(?:changes|edits|adjustments|revisions|help|anything else)[.!]?|Feel free to (?:ask|reach out) if you (?:need|want) (?:anything else|more help|any changes)[.!]?|Note:\s*I(?:'ve| have)? (?:kept|preserved|removed|corrected|formatted|cleaned|polished|rewritten) [^\n]+)[\s]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Clean(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = result.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        // Only whole, allowlisted wrapper lines are removed. Inline content is retained.
        for (int pass = 0; pass < 3; pass++)
        {
            string old = text;
            var lines = text.Split('\n');
            if (lines.Length >= 3 && Regex.IsMatch(lines[0], @"^```[\w-]*\s*$") && lines[^1].Trim() == "```")
                text = string.Join("\n", lines[1..^1]).Trim();
            int firstBreak = text.IndexOf('\n');
            if (firstBreak >= 0 && Preamble.IsMatch(text[..firstBreak].Trim().Replace('’', '\'')))
                text = text[(firstBreak + 1)..].Trim();
            int paragraph = text.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (paragraph > 0 && MetaTail.IsMatch(text[(paragraph + 2)..].Trim().Replace('’', '\'')))
                text = text[..paragraph].TrimEnd();
            if (text.Length > 1 && IsQuotePair(text[0], text[^1]))
                text = text[1..^1].Trim();
            if (text == old) break;
        }
        return text;
    }

    private static bool IsQuotePair(char first, char last) => (first, last) is
        ('"', '"') or ('\'', '\'') or ('“', '”') or ('‘', '’');
}
