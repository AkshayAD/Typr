using Typr.Core;
using Xunit;

namespace Typr.Core.Tests;

public sealed class CaptionAndDeliveryTests
{
    [Fact] public void CaptionsKeepNewestLinesAtBottom()
    {
        var lines = CaptionWrapper.Wrap("one two three four five six", 9, 2);
        Assert.Equal(["four five", "six"], lines.Select(l => l.Text));
        Assert.Equal([.7, 1d], lines.Select(l => l.Opacity));
    }
    [Fact] public void CaptionsHonorExplicitBreaksAndIgnoreEmptyStrips()
    {
        var lines = CaptionWrapper.Wrap("alpha\r\n\r\nbeta\ngamma", 30, 3);
        Assert.Equal(["alpha", "beta", "gamma"], lines.Select(l => l.Text));
    }
    [Fact] public void LongWordsAreSplitWithoutBreakingUnicodeGraphemes()
    {
        var lines = CaptionWrapper.Wrap("😀😀😀", 2, 3);
        Assert.Equal(["😀😀", "😀"], lines.Select(l => l.Text));
        Assert.Equal(["e\u0301e\u0301", "e\u0301"], CaptionWrapper.Wrap("e\u0301e\u0301e\u0301", 2, 3).Select(l => l.Text));
    }
    [Fact] public void AppendingWordsOnlyScrollsEarlierLinesUp()
    {
        Assert.Equal(["one two", "three"], CaptionWrapper.Wrap("one two three", 7).Select(l => l.Text));
        Assert.Equal(["three", "four"], CaptionWrapper.Wrap("one two three four", 7).Select(l => l.Text));
    }
    [Fact] public void EmptyCaptionHasNoStrip() => Assert.Empty(CaptionWrapper.Wrap(" \n  "));
    [Theory]
    [InlineData(0, 2)] [InlineData(60, 0)] [InlineData(60, 4)]
    public void InvalidCaptionLimitsThrow(int width, int lines)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CaptionWrapper.Wrap("hello", width, lines));

    [Fact] public void DeliveryPairsEveryUtf16CodeUnitIncludingSurrogates()
    {
        var plan = DeliveryInputPlan.Create("A😀");
        Assert.Equal(6, plan.Strokes.Count);
        Assert.Equal(new InputStroke(InputStrokeKind.Unicode, 'A', false), plan.Strokes[0]);
        Assert.Equal(new InputStroke(InputStrokeKind.Unicode, 'A', true), plan.Strokes[1]);
        Assert.Equal((ushort)'\ud83d', plan.Strokes[2].Code);
        Assert.Equal((ushort)'\ude00', plan.Strokes[4].Code);
        Assert.All(plan.Strokes.Where((_, i) => i % 2 == 1), s => Assert.True(s.KeyUp));
    }
    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u2028b")]
    public void EveryLineBreakIsShiftEnter(string input)
    {
        var plan = DeliveryInputPlan.Create(input);
        Assert.Equal("a\nb", plan.Text);
        Assert.Equal(new InputStroke(InputStrokeKind.VirtualKey, 0x10, false), plan.Strokes[2]);
        Assert.Equal(new InputStroke(InputStrokeKind.VirtualKey, 0x0D, false), plan.Strokes[3]);
        Assert.Equal(new InputStroke(InputStrokeKind.VirtualKey, 0x0D, true), plan.Strokes[4]);
        Assert.Equal(new InputStroke(InputStrokeKind.VirtualKey, 0x10, true), plan.Strokes[5]);
        Assert.Equal(8, plan.Strokes.Count);
    }
    [Theory]
    [InlineData("hello\r\n\n", "hello")]
    [InlineData("\n\n", "")]
    [InlineData("hello  ", "hello  ")]
    public void TrailingLineBreaksNeverReachTarget(string input, string expected)
        => Assert.Equal(expected, DeliveryInputPlan.Create(input).Text);
    [Fact] public void BlankLineUsesTwoSafeBreaks()
        => Assert.Equal(12, DeliveryInputPlan.Create("a\n\nb").Strokes.Count);
}
