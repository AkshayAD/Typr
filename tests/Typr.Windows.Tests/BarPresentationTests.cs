using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Typr.Core;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// These checks never call Show, EnsureHandle, native input, or desktop integration.
[Collection("WPF")]
public sealed class BarPresentationTests(WpfFixture fixture)
{
    [Fact] public void BackgroundAlphaChangesWhileForegroundAndWindowStayOpaqueAcrossThemes() => fixture.Run(() => {
        ThemeManager.Apply("Light", "Iris");
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast"], ["HideBar"], 100, 400, opacity: .2);
            bar.UpdateState(true, false, false, "Synthetic live words", "Ready");
            var shell = Shell(bar);
            Assert.Equal(SystemParameters.HighContrast ? 255 : 51, ((SolidColorBrush)shell.Background).Color.A);
            Assert.Equal(1, bar.Opacity); Assert.Equal(1, shell.Opacity);
            foreach (var text in Descendants<TextBlock>(bar)) Assert.Equal(1, text.Opacity);
            ThemeManager.Apply("Dark", "Ocean"); WpfFixture.Pump();
            var color = ((SolidColorBrush)shell.Background).Color;
            Assert.Equal(SystemParameters.HighContrast ? SystemColors.WindowColor.R : 36, color.R);
            Assert.Equal(SystemParameters.HighContrast ? 255 : 51, color.A);
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Theory]
    [InlineData(.85)] [InlineData(1)] [InlineData(1.15)]
    public void ScalingIncludesCaptionTrayAndStableCompactAnchor(double scale) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["ToggleRecording", "HoldToTalk", "Cancel", "PasteLast", "ToggleMicrophone", "ToggleCaptions", "OpenWorkspace", "HideBar"], [], 100, 500, scale: scale);
            bar.UpdateState(true, false, false, "A synthetic caption", "Ready");
            var anchor = bar.CompactPosition;
            double bottom = bar.Top + bar.Height;
            bar.SetPointerInside(true);
            Assert.Equal(340 * scale, bar.Width, 5);
            Assert.Equal((66 + 34 + 3 * 40 + 54) * scale, bar.Height, 5);
            Assert.Equal(anchor, bar.CompactPosition);
            Assert.Equal(bottom, bar.Top + bar.Height, 5);
            var size = bar.CompactSize;
            Rect[] work = [new(-1920, -100, 1920, 1000), new(0, 0, 1920, 1040)];
            var clamped = DictationBarWindow.ClampToWorkAreas(new Point(-5000, -500), size, work);
            Assert.Equal(new Point(-1920, -100), clamped);
            Assert.Contains(work, area => area.Contains(new Rect(clamped, size)));
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Fact] public void HoverDelayActuallyDefersExpansionAndReentryCancelsCollapse() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast"], [], 100, 500, hoverDelay: 150);
            bar.SetPointerInside(true); Assert.False(bar.IsExpanded);
            PumpFor(220); Assert.True(bar.IsExpanded);
            bar.SetPointerInside(false); Assert.True(bar.IsExpanded);
            bar.SetPointerInside(true); PumpFor(240); Assert.True(bar.IsExpanded);
            bar.SetPointerInside(false); PumpFor(260); Assert.False(bar.IsExpanded);
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Fact] public void ShortcutOnlyModeDisablesStartsWhileKeepingFinishCancelAndPreparationAvailable() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var requested = new List<string>(); bar.ActionRequested += requested.Add;
            bar.Configure(false, true, [], [], 100, 300, allowButtonRecording: false);
            Assert.True(ButtonNamed(bar, "Prepare speech").IsEnabled);
            bar.UpdateState(true, false, false, null, "Speech ready");
            Assert.False(ButtonNamed(bar, "Start dictation").IsEnabled);
            Assert.False(ButtonNamed(bar, "Hold to talk").IsEnabled);
            bar.BeginPointer("HoldToTalk", new Point(100, 100)); bar.EndPointer(new Point(100, 100));
            Assert.Empty(requested);
            bar.UpdateState(true, true, false, null, "Listening");
            var finish = ButtonNamed(bar, "Finish dictation"); Assert.True(finish.IsEnabled);
            finish.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            bar.UpdateState(true, false, true, null, "Finishing");
            var cancel = ButtonNamed(bar, "Cancel"); Assert.True(cancel.IsEnabled);
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new[] { "ToggleRecording", "Cancel" }, requested);
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PendingCaptureCanBeCancelledBeforeOrAfterSpeechPreparationInShortcutOnlyMode(bool prepared) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var requested = new List<string>(); bar.ActionRequested += requested.Add;
            bar.Configure(false, true, [], [], 100, 300, allowButtonRecording: false);
            // MainWindow maps a pending recording start to busy, including when
            // the first shortcut is still loading the local speech model.
            bar.UpdateState(prepared, false, true, null, "Preparing speech");
            var cancel = ButtonNamed(bar, "Cancel");
            Assert.True(cancel.IsEnabled);
            Assert.False(ButtonNamed(bar, "Hold to talk").IsEnabled);
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new[] { "Cancel" }, requested);
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Fact] public void AppearanceControlsPersistAndResetWithoutChangingCaptionOrRecordingConsent() => fixture.Run(() => {
        var directory = Path.Combine(Path.GetTempPath(), "typr-bar-style-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, SettingsMigrator.Save(new AppSettings { CaptionOpacity = .42 }));
        try {
            WithWindow(window => {
                Assert.False(Control<CheckBox>(window, "AllowButtonRecordingBox").IsChecked);
                Control<Slider>(window, "BarOpacitySlider").Value = .63;
                Control<ComboBox>(window, "BarScaleBox").SelectedIndex = 2;
                Control<ComboBox>(window, "BarHoverDelayBox").SelectedIndex = 3;
                var allow = Control<CheckBox>(window, "AllowButtonRecordingBox");
                allow.IsChecked = true; allow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var saved = SettingsMigrator.Load(File.ReadAllText(settingsPath));
                Assert.Equal(.63, saved.BarOpacity); Assert.Equal(1.15, saved.BarScale); Assert.Equal(500, saved.BarHoverDelay);
                Assert.True(saved.AllowButtonRecording); Assert.Equal(.42, saved.CaptionOpacity);
            });
            WithWindow(window => {
                Assert.Equal(.63, Control<Slider>(window, "BarOpacitySlider").Value);
                Assert.Equal(2, Control<ComboBox>(window, "BarScaleBox").SelectedIndex);
                Assert.Equal(3, Control<ComboBox>(window, "BarHoverDelayBox").SelectedIndex);
                var reset = Descendants<Button>(window).Single(button => Equals(button.Content, "Reset bar appearance"));
                reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var saved = SettingsMigrator.Load(File.ReadAllText(settingsPath));
                Assert.Equal(.78, saved.BarOpacity); Assert.Equal(1, saved.BarScale); Assert.Equal(150, saved.BarHoverDelay);
                Assert.True(saved.AllowButtonRecording); Assert.Equal(.42, saved.CaptionOpacity);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }

        void WithWindow(Action<MainWindow> check)
        {
            var window = new MainWindow(directory, Path.Combine(directory, "local"), desktopIntegration: false);
            try { check(window); Assert.False(window.IsVisible); }
            finally {
                bool closed = false; window.Closed += (_, _) => closed = true;
                window.Close(); WpfFixture.Pump(); Assert.True(closed);
            }
        }
    });

    private static Border Shell(DictationBarWindow bar) => Assert.IsType<Border>(Assert.Single(Assert.IsType<Grid>(Assert.IsType<Viewbox>(bar.Content).Child).Children));
    private static Button ButtonNamed(DependencyObject root, string name) => Descendants<Button>(root).Single(button => AutomationProperties.GetName(button) == name);
    private static T Control<T>(MainWindow window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
