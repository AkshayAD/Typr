using Ava.Core;
using Xunit;

namespace Ava.Core.Tests;

public sealed class TextGuardTests
{
    [Theory]
    [InlineData("Meet Tuesday, no wait, Wednesday at noon.", "Meet Wednesday at noon.")]
    [InlineData("Monday, I mean Tuesday, I mean Friday", "Friday")]
    [InlineData("Send it in January, sorry, I mean February.", "Send it in February.")]
    [InlineData("  Keep   my words.\r\nNext line.  ", "Keep my words.\nNext line.")]
    [InlineData("I do not agree. Tuesday or Wednesday?", "I do not agree. Tuesday or Wednesday?")]
    [InlineData("Pay 15, no wait, 50 dollars.", "Pay 15, no wait, 50 dollars.")]
    [InlineData("Ask Sarah, no wait, Sara.", "Ask Sarah, no wait, Sara.")]
    public void CleanupIsDeliberatelyNarrow(string input, string expected)
        => Assert.Equal(expected, DeterministicCleanup.Clean(input));

    [Theory]
    [InlineData("Sure:\nThe meeting is tomorrow.", "The meeting is tomorrow.")]
    [InlineData("Here's the cleaned text:\n\nHello.", "Hello.")]
    [InlineData("```text\nHello.\n```", "Hello.")]
    [InlineData("“Hello.”", "Hello.")]
    [InlineData("\"Hello.\"", "Hello.")]
    [InlineData("Hello.\n\nHope this helps!", "Hello.")]
    [InlineData("Hello.\n\nLet me know if you would like any changes.", "Hello.")]
    [InlineData("Hello.\n\nNote: I preserved your original tone.", "Hello.")]
    [InlineData("Sure:\n```text\n\"Hello.\"\n```", "Hello.")]
    public void OutputGuardRemovesRecognizedWrappers(string input, string expected)
        => Assert.Equal(expected, OutputGuard.Clean(input));

    [Theory]
    [InlineData("Sure: I can attend.")]
    [InlineData("Here's my idea:\nWe leave early.")]
    [InlineData("Hello.\n\nNote: Bring your passport.")]
    [InlineData("Hello.\n\nLet me know the flight number.")]
    [InlineData("Hello.\n\nFeel free to use the spare room.")]
    [InlineData("Sure:")]
    [InlineData("The document contains ``` inline fences.")]
    [InlineData("```text\nAn unfinished fence")]
    [InlineData("He said \"Hello.\"")]
    public void OutputGuardPreservesRealContent(string input)
        => Assert.Equal(input, OutputGuard.Clean(input));

    [Theory]
    [InlineData("i will call Sarah tomorrow", "I will call Sarah tomorrow.")]
    [InlineData("I can't attend tomorrow", "I cannot attend tomorrow.")]
    [InlineData("I won't attend tomorrow", "I will not attend tomorrow.")]
    [InlineData("Buy two apples", "Buy 2 apples.")]
    [InlineData("It costs $15 today", "It costs $15 today.")]
    [InlineData("", "")]
    public void ChangeGuardAllowsSafeFormatting(string source, string candidate)
    {
        var result = ChangeGuard.Evaluate(source, candidate);
        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(candidate, result.Text);
    }
    [Theory]
    [InlineData("Pay Sarah $15 today", "Pay Sarah $50 today", "Numeric content changed")]
    [InlineData("Pay Sarah $15 today", "Pay Sarah €15 today", "Numeric content changed")]
    [InlineData("The value is -5", "The value is 5", "Numeric content changed")]
    [InlineData("The rate is 5%", "The rate is 5", "Numeric content changed")]
    [InlineData("Meet at 3:30", "Meet at 3:00", "Numeric content changed")]
    [InlineData("Buy two apples today please", "Buy three apples today please", "Numeric content changed")]
    [InlineData("I do not approve the plan", "I do approve the plan", "Negation changed")]
    [InlineData("I can't approve this", "I can approve this", "Negation changed")]
    [InlineData("Never send it without approval", "Send it with approval", "Negation changed")]
    [InlineData("Please call Sarah tomorrow", "Please email Sarah tomorrow", "Too many content words changed")]
    [InlineData("Please call Sarah tomorrow", "", "Empty result")]
    [InlineData("First book hotel then buy tickets", "First buy tickets then book hotel", "Too many content words changed")]
    public void ChangeGuardFallsBackToExactInput(string source, string candidate, string reason)
    {
        var result = ChangeGuard.Evaluate(source, candidate);
        Assert.False(result.Accepted);
        Assert.Equal(source, result.Text);
        Assert.Equal(reason, result.Reason);
    }
    [Fact] public void SmallMeaningChangesAreNotPromisedToBeDetected()
    {
        // The guard is a retention heuristic. It cannot prove semantic equivalence.
        const string input = "Please send Sarah the final draft tomorrow morning before our regular planning meeting";
        Assert.True(ChangeGuard.Evaluate(input, input.Replace("Sarah", "Sara")).Accepted);
    }
    [Fact] public void HugeDifferentOutputIsRejectedWithoutQuadraticAllocation()
    {
        var source = string.Join(' ', Enumerable.Repeat("apple", 2500));
        var candidate = string.Join(' ', Enumerable.Repeat("orange", 2500));
        Assert.False(ChangeGuard.Evaluate(source, candidate).Accepted);
    }
    [Theory]
    [InlineData(-1)] [InlineData(2)] [InlineData(double.NaN)]
    public void InvalidThresholdIsRejected(double threshold)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ChangeGuard.Evaluate("a", "a", threshold));
}
