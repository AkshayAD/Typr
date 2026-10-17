using Ava.Core;
using Xunit;

namespace Ava.Core.Tests;

public sealed class OutputPersonalizerTests
{
    [Fact] public void RawOnlyTrimsOuterWhitespaceAndBypassesEveryTransformation()
    {
        const string raw = "  Monday, no wait, Tuesday.\r\n type  er my signature  ";
        Assert.Equal(raw.Trim(), OutputPersonalizer.Transform(raw, false,
            [new("type er", "Typr")], [new("my signature", "Example")]));
    }

    [Fact] public void CleanAppliesConservativeCleanupBeforePersonalization()
        => Assert.Equal("Tuesday Typr", OutputPersonalizer.Transform(" Monday, no wait, Tuesday   type  er ", true,
            [new("type er", "Typr")], []));

    [Fact] public void LongestPhraseWinsAcrossVocabularyAndSnippets()
        => Assert.Equal("Long result and short result.", OutputPersonalizer.Transform("my long signature and my signature.", true,
            [new("my long signature", "Long result")], [new("my signature", "short result")]));

    [Fact] public void SnippetWinsCaseInsensitiveCollisionWithVocabulary()
        => Assert.Equal("Snippet", OutputPersonalizer.Transform("TYPE ER", true,
            [new("type er", "Vocabulary")], [new("Type Er", "Snippet")]));

    [Fact] public void InsertedTextIsNeverMatchedAgain()
        => Assert.Equal("type er Typr", OutputPersonalizer.Transform("my signature type er", true,
            [new("type er", "Typr")], [new("my signature", "type er")]));

    [Theory]
    [InlineData("cat catapult bobcat cat2 2cat _cat cat_", "dog catapult bobcat cat2 2cat _cat cat_")]
    [InlineData("(CAT), cat!", "(dog), dog!")]
    [InlineData("écat caté cat\u0301 \u0301cat", "écat caté cat\u0301 \u0301cat")]
    [InlineData("हcat catह cat\u200D", "हcat catह cat\u200D")]
    [InlineData("𝔄cat cat𝔄 😀cat cat😀", "𝔄cat cat𝔄 😀dog dog😀")]
    public void PhraseBoundariesPreserveUnicodeWordsNumbersAndConnectors(string input, string expected)
        => Assert.Equal(expected, OutputPersonalizer.Transform(input, true, [new("cat", "dog")], []));

    [Fact] public void NumericTriggersDoNotMatchInsideLongerNumbers()
        => Assert.Equal("15 51 five ٥5 5٥", OutputPersonalizer.Transform("15 51 5 ٥5 5٥", true,
            [new("5", "five")], []));

    [Fact] public void NumericTriggersDoNotMatchComponentsOfNumericExpressions()
        => Assert.Equal("15.5 5.2 5,000 2/5 5:30 1.5.2 five.",
            OutputPersonalizer.Transform("15.5 5.2 5,000 2/5 5:30 1.5.2 5.", true, [new("5", "five")], []));

    [Fact] public void TriggersAreLiteralAndMayContainPunctuation()
        => Assert.Equal("Use C# and .NET (literal); do not match cccc.",
            OutputPersonalizer.Transform("Use c++ and dot net [test]; do not match cccc.", true,
                [new("c++", "C#"), new("dot net [test]", ".NET (literal)")], []));

    [Fact] public void ExpansionsPreserveLiteralReplacementSymbolsAndNewlines()
        => Assert.Equal("$1 ${name} $$ \\path\nSecond line.",
            OutputPersonalizer.Transform("my signature", true, [],
                [new("my signature", "$1 ${name} $$ \\path\nSecond line.")]));

    [Fact] public void MatchingIsCaseInsensitiveWhileOutputCapitalizationIsExplicit()
        => Assert.Equal("Typr Typr Typr", OutputPersonalizer.Transform("type er TYPE ER Type Er", true,
            [new("type er", "Typr")], []));

    [Fact] public void InvalidProgrammaticRulesCannotCreateEmptyOrUnboundedMatches()
        => Assert.Equal("Typr", OutputPersonalizer.Transform(" type er ", true,
            [null!, new(null!, "x"), new("", "x"), new("type er", "Typr"), new("TYPE ER", "ignored")], []));

    [Theory]
    [InlineData("", "")]
    [InlineData(" \r\n ", "")]
    [InlineData("I do not owe 15 dollars.", "I do not owe 15 dollars.")]
    public void EmptyOrUnmatchedTextIsPreserved(string input, string expected)
        => Assert.Equal(expected, OutputPersonalizer.Transform(input, true, [], []));
}
