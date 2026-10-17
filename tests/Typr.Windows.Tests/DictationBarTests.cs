using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

[Collection("WPF")]
public sealed class DictationBarTests(WpfFixture fixture)
{
    [Fact] public void FiltersUnknownAndDuplicateActionsWithoutInventingDefaults()
    {
        Assert.Equal(new[] { "PasteLast", "HideBar" }, DictationBarWindow.FilterActions(["PasteLast", "unknown", "PasteLast", "HideBar"]));
        Assert.Empty(DictationBarWindow.FilterActions([]));
    }

    [Fact] public void ClampPreservesNegativeMonitorCoordinatesAndAvoidsDesktopGaps()
    {
        Rect[] monitors = [new(-1920, -100, 1920, 1080), new(0, 0, 1920, 1040)];
        var size = new Size(340, 66);
        Assert.Equal(new Point(-1800, -50), DictationBarWindow.ClampToWorkAreas(new Point(-1800, -50), size, monitors));
        Assert.Equal(new Point(-1920, -100), DictationBarWindow.ClampToWorkAreas(new Point(-2500, -500), size, monitors));
        Assert.Equal(new Point(1580, 974), DictationBarWindow.ClampToWorkAreas(new Point(2200, 1200), size, monitors));
        Rect[] separated = [new(0, 0, 1000, 800), new(1300, 0, 1000, 800)];
        var clamped = DictationBarWindow.ClampToWorkAreas(new Point(1050, 100), size, separated);
        Assert.Contains(separated, area => area.Contains(new Rect(clamped, size)));
    }

    [Fact] public void ClickExecutesOnReleaseAndDraggingAnActionNeverExecutesIt() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var actions = new List<string>(); var positions = new List<Point>();
            bar.ActionRequested += actions.Add; bar.PositionChanged += positions.Add;
            bar.Configure(false, true, ["PasteLast"], ["HideBar"], 100, 200);
            bar.BeginPointer("PasteLast", new Point(100, 100));
            Assert.Empty(actions);
            bar.EndPointer(new Point(100, 100));
            Assert.Equal(new[] { "PasteLast" }, actions);
            Assert.Empty(positions);
            bar.BeginPointer("PasteLast", new Point(100, 100));
            bar.MovePointer(new Point(180, 160));
            Assert.Single(actions);
            Assert.Empty(positions);
            bar.EndPointer(new Point(200, 180));
            Assert.Single(actions);
            Assert.Single(positions);
        }
        finally { bar.Close(); }
    });

    [Fact] public void HoldFinishesOnceAndDraggingAHoldCancelsWithoutFinishing() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var actions = new List<string>(); bar.ActionRequested += actions.Add;
            bar.UpdateState(true, false, false, null, "Ready");
            bar.BeginPointer("HoldToTalk", new Point(100, 100));
            Assert.Equal(new[] { "HoldStart" }, actions);
            bar.EndPointer(new Point(100, 100));
            Assert.Equal(new[] { "HoldStart", "HoldFinish" }, actions);
            actions.Clear();
            bar.BeginPointer("HoldToTalk", new Point(100, 100));
            bar.MovePointer(new Point(200, 180));
            bar.EndPointer(new Point(200, 180));
            Assert.Equal(new[] { "HoldStart", "Cancel" }, actions);
        }
        finally { bar.Close(); }
    });

    [Fact] public void HideDuringHoldCancelsAndLateReleaseDoesNothing() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            var actions = new List<string>(); bar.ActionRequested += actions.Add;
            bar.UpdateState(true, false, false, null, "Ready");
            bar.BeginPointer("HoldToTalk", new Point(100, 100));
            bar.HideBar(); bar.EndPointer(new Point(100, 100));
            Assert.Equal(new[] { "HoldStart", "Cancel" }, actions);
        }
        finally { bar.Close(); }
    });

    [Fact] public void ExpandingKeepsCompactControlsAnchoredAndHonorsConfiguredActionLists() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast", "ToggleCaptions"], ["HideBar"], 100, 500);
            var anchor = bar.CompactPosition;
            double compactBottom = bar.Top + bar.Height;
            bar.SetPointerInside(true);
            Assert.True(bar.IsExpanded);
            Assert.Equal(anchor, bar.CompactPosition);
            Assert.Equal(compactBottom, bar.Top + bar.Height);
            var labels = Descendants<Button>(bar).Select(AutomationProperties.GetName).ToArray();
            Assert.Contains("Paste last", labels);
            Assert.Contains("Hide captions", labels);
            Assert.DoesNotContain("Hide bar", labels);
        }
        finally { bar.Close(); }
    });

    [Fact] public void NativeWindowIsNonActivatingWithoutDisplayingOrFocusingIt() => fixture.Run(() => {
        var foreground = NativeMethods.GetForegroundWindow();
        var bar = new DictationBarWindow();
        try {
            var handle = new WindowInteropHelper(bar).EnsureHandle();
            long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExstyle).ToInt64();
            Assert.NotEqual(0, style & NativeMethods.WsExNoactivate);
            Assert.NotEqual(0, style & NativeMethods.WsExToolwindow);
            Assert.False(bar.ShowActivated);
            Assert.False(bar.Focusable);
            Assert.False(bar.IsVisible);
            Assert.Equal(foreground, NativeMethods.GetForegroundWindow());
        }
        finally { bar.Close(); }
    });

    [Fact] public void CaptionUpdatesKeepControlsStableAndStateChangesWaitForPointerRelease() => fixture.Run(() => {
        var bar = new DictationBarWindow();
        try {
            bar.Configure(false, true, ["PasteLast", "Cancel"], ["HideBar"], 100, 200);
            bar.UpdateState(true, false, false, null, "Ready");
            var tray = Assert.Single(Descendants<System.Windows.Controls.Primitives.UniformGrid>(bar));
            var original = tray.Children[0];
            bar.UpdateState(true, false, false, "A partial caption", "Ready");
            Assert.Same(original, tray.Children[0]);
            bar.BeginPointer(null, new Point(100, 100));
            bar.UpdateState(true, false, true, "More words", "Finishing");
            Assert.Same(original, tray.Children[0]);
            bar.EndPointer(new Point(100, 100));
            Assert.NotSame(original, tray.Children[0]);
            Assert.False(Assert.IsType<Button>(tray.Children[0]).IsEnabled);
        }
        finally { bar.Close(); }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
}
