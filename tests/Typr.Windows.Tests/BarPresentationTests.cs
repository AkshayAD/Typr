using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            bar.Configure(false, true, ["PasteLast"], ["HideBar"], 100, 400, opacity: .4);
            Assert.Equal(SystemParameters.HighContrast ? 255 : 102, ((SolidColorBrush)shell.Background).Color.A);
            bar.Configure(false, true, ["PasteLast"], ["HideBar"], 100, 400, opacity: .2);
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
    public void ScalingKeepsCaptionsOutsideHoverFootprintAndStableCompactAnchor(double scale) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["ToggleRecording", "HoldToTalk", "Cancel", "PasteLast", "ToggleMicrophone", "ToggleCaptions", "OpenWorkspace", "HideBar"], [], 100, 500, scale: scale);
            bar.UpdateState(true, false, false, "A synthetic caption", "Ready");
            Assert.Equal(new Size(96 * scale, 16 * scale), new Size(bar.Width, bar.Height));
            Assert.DoesNotContain(Descendants<TextBlock>(bar), text => text.Text == "A synthetic caption");
            var caption = PopupChild(bar, "_captionPopup");
            Assert.Contains(Descendants<TextBlock>(caption), text => text.Text == "A synthetic caption" && AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
            var anchor = bar.CompactPosition;
            double bottom = bar.Top + bar.Height;
            bar.SetPointerInside(true);
            Assert.Equal(192 * scale, bar.Width, 5);
            Assert.Equal(32 * scale, bar.Height, 5);
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

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LeavingContextModeRestoresIndependentHoverActions(bool hide) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast"], ["HideBar"], 100, 500);
            bar.SetPointerInside(true);
            var frame = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            ButtonNamed(bar, "Bar actions").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(bar.IsExpanded);
            Assert.Equal(frame, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            Assert.Contains(Descendants<Button>(PopupChild(bar, "_contextPopup")), button => AutomationProperties.GetName(button) == "Hide bar");
            Assert.DoesNotContain(Descendants<Button>(bar), button => AutomationProperties.GetName(button) == "Hide bar");
            if (hide) bar.HideBar();
            else { bar.SetPointerInside(false); PumpFor(260); }
            Assert.False(bar.IsExpanded);
            bar.SetPointerInside(true);
            Assert.True(bar.IsExpanded);
            Assert.Contains(Descendants<Button>(bar), button => AutomationProperties.GetName(button) == "Paste last");
            Assert.DoesNotContain(Descendants<Button>(bar), button => AutomationProperties.GetName(button) == "Hide bar");
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Fact] public void MenuPointerGestureOpensOnFirstReleaseAndClosesOnSecondRelease() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var requested = new List<string>(); bar.ActionRequested += requested.Add;
            bar.Configure(false, true, ["PasteLast"], ["HideBar"], 400, 500);
            bar.SetPointerInside(true);
            var frame = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            var point = new Point(500, 500);
            bar.BeginPointer("ShowMenu", point);
            Assert.False(ContextOpen());
            bar.EndPointer(point);
            Assert.True(ContextOpen());
            Assert.Contains(Descendants<Button>(PopupChild(bar, "_contextPopup")), button => (string)button.Tag == "HideBar");
            bar.BeginPointer("ShowMenu", point);
            Assert.True(ContextOpen());
            bar.EndPointer(point);
            Assert.False(ContextOpen());
            Assert.Empty(Descendants<Button>(PopupChild(bar, "_contextPopup")));
            Assert.Equal(frame, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            Assert.Empty(requested); Assert.False(bar.IsVisible);

            bool ContextOpen() => Assert.IsType<bool>(typeof(DictationBarWindow).GetField("_contextOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar));
        }
        finally { bar.Close(); }
    });

    [Theory]
    [InlineData(.85)] [InlineData(1)] [InlineData(1.15)]
    public void RecordingFinishingAndCaptionsPreserveRestingFrameUntilHover(double scale) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast"], ["HideBar"], 400, 500, scale: scale);
            var frame = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            var anchor = bar.CompactPosition;
            foreach (var (recording, busy, status) in new[] {
                (false, false, "Ready"), (true, false, "Listening"),
                (false, true, "Recognizing captured audio"), (false, true, "Polishing captured words") }) {
                bar.UpdateState(true, recording, busy, "A live caption that stays outside the pill", status);
                Assert.False(bar.IsExpanded);
                Assert.Equal(frame, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
                Assert.Equal(anchor, bar.CompactPosition);
                Assert.Contains(Descendants<BarWaveform>(bar), wave => wave.Activity == (recording ? BarActivity.Listening : busy ? BarActivity.Finishing : BarActivity.Idle));
                Assert.DoesNotContain(Descendants<TextBlock>(bar), text => !string.IsNullOrWhiteSpace(text.Text));
            }
            bar.SetPointerInside(true);
            Assert.Equal(192 * scale, bar.Width, 5); Assert.Equal(32 * scale, bar.Height, 5);
            bar.SetPointerInside(false); PumpFor(260);
            Assert.Equal(frame, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Fact] public void HoverActionOverflowPreservesConfiguredOrderAndRightClickActionsOutsideBar() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            string[] hover = ["ToggleRecording", "HoldToTalk", "Cancel", "PasteLast", "ToggleMicrophone", "ToggleCaptions", "OpenWorkspace", "HideBar"];
            string[] context = ["HideBar", "PasteLast"];
            bar.Configure(false, true, hover, context, 400, 500);
            bar.SetPointerInside(true);
            var actions = Assert.Single(Descendants<UniformGrid>(bar));
            Assert.Equal(hover.Take(4), actions.Children.OfType<Button>().Select(button => (string)button.Tag));
            ButtonNamed(bar, "Bar actions").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var popupActions = Descendants<Button>(PopupChild(bar, "_contextPopup")).Select(button => (string)button.Tag).ToArray();
            Assert.Equal(hover.Skip(4).Concat(context), popupActions);
            Assert.Equal(new Size(192, 32), new Size(bar.Width, bar.Height));
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Fact] public void ShortcutOnlyModeDisablesStartsWhileKeepingFinishCancelAndPreparationAvailable() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var requested = new List<string>(); bar.ActionRequested += requested.Add;
            bar.Configure(false, true, ["HoldToTalk"], [], 100, 300, allowButtonRecording: false);
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
            bar.Configure(false, true, ["HoldToTalk"], [], 100, 300, allowButtonRecording: false);
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

    [Theory]
    [InlineData(.85)] [InlineData(1)] [InlineData(1.15)]
    public void RestingPillGrowsAroundItsCenterAndBottomWithoutChangingSavedAnchor(double scale) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var positions = new List<Point>(); bar.PositionChanged += positions.Add;
            bar.Configure(false, true, ["HoldToTalk", "PasteLast", "ToggleMicrophone", "ToggleCaptions"], ["HideBar"], 400, 500, scale: scale);
            Assert.Equal(96 * scale, bar.Width, 5); Assert.Equal(16 * scale, bar.Height, 5);
            var resting = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            bar.SetPointerInside(true);
            Assert.Equal(192 * scale, bar.Width, 5); Assert.Equal(32 * scale, bar.Height, 5);
            Assert.Equal(resting.Left + resting.Width / 2, bar.Left + bar.Width / 2, 5);
            Assert.Equal(resting.Bottom, bar.Top + bar.Height, 5);
            Assert.True(new Rect(bar.Left, bar.Top, bar.Width, bar.Height).Contains(resting));
            Assert.Equal(new Point(400, 500), bar.CompactPosition); Assert.Empty(positions);
            Assert.False(bar.IsTransitioning); // Hidden checks never animate or create an HWND.
            bar.SetPointerInside(false); PumpFor(260);
            Assert.Equal(resting, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
        }
        finally { bar.Close(); }
    });

    [Fact] public void StateAndCaptionChangesCannotMoveOrReplacePressedControls() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast", "Cancel"], [], 400, 500);
            bar.SetPointerInside(true);
            var tray = Assert.Single(Descendants<UniformGrid>(bar)); var original = tray.Children[0];
            var before = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            bar.BeginPointer("PasteLast", new Point(500, 500));
            bar.UpdateState(true, false, true, "New words during a press", "Processing");
            Assert.Same(original, tray.Children[0]); Assert.Equal(before, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            bar.MovePointer(new Point(500, 100));
            Assert.Equal(before.Size, new Size(bar.Width, bar.Height));
            bar.EndPointer(new Point(500, 100));
            Assert.NotSame(original, tray.Children[0]); Assert.Equal(32, bar.Height);
        }
        finally { bar.Close(); }
    });

    [Theory]
    [InlineData("PasteLast")] [InlineData("HoldToTalk")]
    public void MissedReleaseNeverClicksOrFinishesAHold(string action) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var requested = new List<string>(); bar.ActionRequested += requested.Add;
            bar.UpdateState(true, false, false, null, "Ready");
            bar.BeginPointer(action, new Point(500, 500));
            bar.PollPointer(new Point(500, 500), pressed: false);
            bar.EndPointer(new Point(500, 500));
            Assert.False(bar.IsGestureActive);
            Assert.Equal(action == "HoldToTalk" ? new[] { "HoldStart", "Cancel" } : Array.Empty<string>(), requested);
        }
        finally { bar.Close(); }
    });

    [Fact] public void WatchdogDragPersistsOnceAndLostCursorCancelsWithoutDelivery() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var requested = new List<string>(); var positions = new List<Point>();
            bar.ActionRequested += requested.Add; bar.PositionChanged += positions.Add;
            bar.Configure(false, true, [], [], 400, 500);
            var dpi = VisualTreeHelper.GetDpi(bar);
            bar.BeginPointer("PasteLast", new Point(500, 500));
            bar.PollPointer(new Point(550, 450), pressed: true);
            Assert.Equal(new Point(400 + 50 / dpi.DpiScaleX, 500 - 50 / dpi.DpiScaleY), bar.CompactPosition);
            bar.PollPointer(new Point(570, 430), pressed: false);
            Assert.Single(positions); Assert.Empty(requested); Assert.False(bar.IsGestureActive);
            bar.PollPointer(new Point(570, 430), pressed: false); Assert.Single(positions);
            bar.UpdateState(true, false, false, null, "Ready");
            bar.BeginPointer("HoldToTalk", new Point(500, 500)); bar.PollPointer(null, pressed: true);
            Assert.Equal(new[] { "HoldStart", "Cancel" }, requested); Assert.False(bar.IsGestureActive);
        }
        finally { bar.Close(); }
    });

    [Fact] public void DraggingExpandedSurfaceToAnEdgeKeepsWholeFrameVisibleWithoutReleaseJump() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast"], [], 400, 500); bar.SetPointerInside(true);
            bar.BeginPointer("PasteLast", new Point(500, 500));
            bar.MovePointer(new Point(100000, 500));
            var before = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            bar.EndPointer(new Point(100000, 500));
            Assert.Equal(before, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            Assert.Equal(bar.Left + bar.Width / 2, bar.CompactPosition.X + bar.CompactSize.Width / 2);
        }
        finally { bar.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DraggingDownFromTopKeepsTheChosenGrowthDirectionOnRelease(bool captionAndContext) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast"], ["OpenWorkspace", "HideBar"], 400, -100000);
            bar.SetPointerInside(true);
            if (captionAndContext) {
                ButtonNamed(bar, "Bar actions").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                bar.UpdateState(true, false, false, "A caption above the controls", "Ready");
            }
            Assert.Equal(bar.CompactPosition.Y, bar.Top);
            bar.BeginPointer("PasteLast", new Point(500, 500)); bar.MovePointer(new Point(500, 700));
            var before = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            bar.EndPointer(new Point(500, 700));
            Assert.Equal(before, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            Assert.Equal(bar.CompactPosition.Y, bar.Top);
        }
        finally { bar.Close(); }
    });

    public static IEnumerable<object[]> ClampedDragOrigins()
    {
        foreach (var (x, y) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (-1, 1), (1, -1), (1, 1) })
            for (int content = 0; content < 4; content++) yield return [x, y, content];
    }

    [Theory]
    [MemberData(nameof(ClampedDragOrigins))]
    public void InwardDragFromAnyClampedOriginPreservesFrameOnReleaseAndRestingCenter(int x, int y, int content) => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var actions = new List<string>(); var positions = new List<Point>();
            bar.ActionRequested += actions.Add; bar.PositionChanged += positions.Add;
            bar.Configure(false, true, ["PasteLast"], ["OpenWorkspace", "HideBar"], x == 0 ? 400 : x * 100000, y == 0 ? 500 : y * 100000);
            bar.SetPointerInside(true);
            if ((content & 1) != 0) bar.UpdateState(true, false, false, "A caption while moving the bar", "Ready");
            if ((content & 2) != 0) ButtonNamed(bar, "Bar actions").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var originalAnchor = bar.CompactPosition;
            var initial = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            var start = new Point(500, 500);
            var dpi = VisualTreeHelper.GetDpi(bar);
            var finish = start + new Vector(-x * 120 * dpi.DpiScaleX, -y * 120 * dpi.DpiScaleY);
            bar.BeginPointer("PasteLast", start);
            Assert.Equal(originalAnchor, bar.CompactPosition); // Presses alone never normalize a saved position.
            bar.MovePointer(finish);
            var before = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            Assert.Equal(initial.Left - x * 120, before.Left, 5);
            Assert.Equal(initial.Top - y * 120, before.Top, 5);
            bar.EndPointer(finish);
            Assert.Equal(before, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            Assert.Empty(actions); Assert.Equal(new[] { bar.CompactPosition }, positions);
            Assert.Equal(before.Left + before.Width / 2, bar.CompactPosition.X + bar.CompactSize.Width / 2, 5);
            bar.UpdateState(true, false, false, null, "Ready");
            bar.SetPointerInside(false); PumpFor(260);
            Assert.Equal(96, bar.Width); Assert.Equal(16, bar.Height);
            Assert.Equal(before.Left + before.Width / 2, bar.Left + bar.Width / 2, 5);
            Assert.False(bar.IsVisible);
        }
        finally { bar.Close(); }
    });

    [Fact] public void StationaryPressAtClampedOriginNeverChangesSavedRestingPosition() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var positions = new List<Point>(); bar.PositionChanged += positions.Add;
            bar.Configure(false, true, [], [], -100000, -100000); bar.SetPointerInside(true);
            var originalAnchor = bar.CompactPosition;
            var originalFrame = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            bar.BeginPointer(null, new Point(500, 500)); bar.EndPointer(new Point(500, 500));
            Assert.Equal(originalAnchor, bar.CompactPosition);
            Assert.Equal(originalFrame, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            Assert.Empty(positions);
        }
        finally { bar.Close(); }
    });

    [Fact] public void RenderProductResourcePreviewsWithoutShowingAWindow() => fixture.Run(() => {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Typr.Windows"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "artifacts", "bar-rendered-previews"); Directory.CreateDirectory(directory);
        foreach (string theme in new[] { "Light", "Dark" }) {
            ThemeManager.Apply(theme, "Iris");
            var bar = new DictationBarWindow();
            try {
                bar.Configure(false, true, ["HoldToTalk", "PasteLast", "ToggleMicrophone", "ToggleCaptions"], ["OpenWorkspace", "ToggleMicrophone", "PasteLast", "ToggleCaptions", "HideBar"], 400, 500, allowButtonRecording: false);
                Render("idle"); bar.SetPointerInside(true); Render("hover");
                ButtonNamed(bar, "Bar actions").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Render("context");
                bar.UpdateState(true, true, false, "A small thought, ready to become words.", "Listening"); Render("recording");
                Assert.False(bar.IsVisible);
            }
            finally { bar.Close(); }

            void Render(string state)
            {
                var visual = Assert.IsType<Viewbox>(bar.Content);
                visual.Measure(new Size(bar.Width, bar.Height)); visual.Arrange(new Rect(0, 0, bar.Width, bar.Height)); visual.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bar.Width * 3), (int)Math.Ceiling(bar.Height * 3), 288, 288, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"{theme.ToLowerInvariant()}-{state}.rendered.png")); encoder.Save(file);
                Assert.True(file.Length > 100);
            }
        }
    });

    private static Border Shell(DictationBarWindow bar) => Assert.IsType<Border>(Assert.Single(Assert.IsType<Grid>(Assert.IsType<Viewbox>(bar.Content).Child).Children));
    private static DependencyObject PopupChild(DictationBarWindow bar, string field)
    {
        var popup = Assert.IsType<Popup>(typeof(DictationBarWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar));
        Assert.False(popup.IsOpen); // Hidden checks never create a popup HWND.
        Assert.False(popup.Focusable);
        return Assert.IsAssignableFrom<DependencyObject>(popup.Child);
    }
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
