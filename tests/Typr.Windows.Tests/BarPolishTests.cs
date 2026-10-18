using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

[Collection("WPF")]
public sealed class BarPolishTests(WpfFixture fixture)
{
    [Fact] public void HoverHintsUseProductSurfaceAndVectorIconsInsteadOfPlatformGlyphs() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast", "ToggleCaptions"], [], 100, 500);
            bar.SetPointerInside(true);
            var actions = Descendants<UniformGrid>(bar).Single();
            foreach (Button button in actions.Children) {
                Assert.IsType<Viewbox>(button.Content);
                var hint = Assert.IsType<ToolTip>(button.ToolTip);
                Assert.NotNull(hint.Style);
                Assert.IsType<StackPanel>(hint.Content);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(button)));
            }
            Assert.Null(Descendants<Border>(bar).First().ToolTip);
        } finally { bar.Close(); }
    });

    [Fact] public void FinishingKeepsHoverControlsAndUsesWaveformWithoutVisibleStatusText() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast"], [], 100, 500);
            bar.UpdateState(true, false, false, null, "Ready"); bar.SetPointerInside(true);
            var actions = Descendants<UniformGrid>(bar).Single(); var original = actions.Children[0];
            var frame = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            bar.BeginPointer("PasteLast", new Point(100, 100));
            bar.UpdateState(true, false, true, "Already captured words", "Finishing your words");
            Assert.Equal(Visibility.Visible, actions.Visibility); Assert.Same(original, actions.Children[0]);
            bar.EndPointer(new Point(100, 100));
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.False(Assert.IsType<Button>(actions.Children[0]).IsEnabled);
            Assert.Equal(frame, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            Assert.Contains(Descendants<BarWaveform>(bar), wave => wave.Activity == BarActivity.Finishing);
            Assert.DoesNotContain(Descendants<TextBlock>(bar), text => !string.IsNullOrWhiteSpace(text.Text));
            Assert.Contains(Descendants<Button>(bar), button => AutomationProperties.GetName(button) == "Cancel" && button.IsEnabled);
            Assert.Equal("Finishing your words", AutomationProperties.GetHelpText(Descendants<Border>(bar).First()));
            bar.UpdateState(true, false, false, null, "Ready");
            Assert.Equal(Visibility.Visible, actions.Visibility);
            Assert.True(Assert.IsType<Button>(actions.Children[0]).IsEnabled);
        } finally { bar.Close(); }
    });

    [Fact] public void NarrowAudioWaveChangesWithLevelWithoutRunningAHiddenClock() => fixture.Run(() => {
        var wave = new BarWaveform { Width = 22, Height = 14 };
        wave.SetActivity(BarActivity.Listening);
        wave.SetLevel(0); var silent = Render(wave);
        wave.SetLevel(1); var speaking = Render(wave);
        Assert.False(silent.SequenceEqual(speaking));
        Assert.False(wave.IsAnimating);
        wave.SetLevel(float.NaN); Assert.Equal(silent, Render(wave));
        wave.SetActivity(BarActivity.Finishing); var finishing = Render(wave);
        Assert.False(speaking.SequenceEqual(finishing)); Assert.False(wave.IsAnimating);
        wave.SetActivity(BarActivity.Idle); var idle = Render(wave);
        Assert.False(finishing.SequenceEqual(idle)); Assert.False(wave.IsAnimating);
    });

    static byte[] Render(FrameworkElement element)
    {
        element.Measure(new Size(22, 14)); element.Arrange(new Rect(0, 0, 22, 14)); element.UpdateLayout();
        var image = new RenderTargetBitmap(22, 14, 96, 96, PixelFormats.Pbgra32); image.Render(element);
        var bytes = new byte[22 * 14 * 4]; image.CopyPixels(bytes, 22 * 4, 0); return bytes;
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) {
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
}
