using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// Synthetic work areas and hidden windows: no input, microphone, or desktop changes.
[Collection("WPF")]
public sealed class BarDockingTests(WpfFixture fixture)
{
    private static readonly Rect Work = new(0, 40, 1200, 820);

    [Theory]
    [InlineData(250, 100, "Top", 202, 58)]
    [InlineData(840, 800, "Bottom", 792, 826)]
    [InlineData(30, 320, "Left", 18, 272)]
    [InlineData(1170, 600, "Right", 1166, 552)]
    [InlineData(430, 800, "Bottom", 382, 826)]
    [InlineData(30, 540, "Left", 18, 492)]
    public void ProjectionUsesTheNearestWallAndPreservesPositionAlongIt(double x, double y, string expectedEdge, double expectedX, double expectedY)
    {
        var center = new Point(x, y); var edge = DictationBarWindow.NearestEdge(center, Work);
        Assert.Equal(expectedEdge, edge.ToString());
        var size = edge is BarDockEdge.Left or BarDockEdge.Right ? new Size(16, 96) : new Size(96, 16);
        Assert.Equal(new Point(expectedX, expectedY), DictationBarWindow.EdgePosition(Work, size, center, edge));
    }

    [Fact] public void LegacyPositionsUseNearestMonitorIncludingNegativeOriginsAndTaskbarInsets() => fixture.Run(() => {
        Rect[] areas = [new(-1600, -300, 1600, 960), Work]; var bar = new DictationBarWindow(() => areas);
        try {
            bar.Configure(false, true, [], [], -1500, -250);
            Assert.Equal(BarDockEdge.Top, bar.DockEdge); Assert.Equal(new Point(-1500, -282), bar.CompactPosition);
            bar.Configure(false, true, [], [], -100, 100);
            Assert.Equal(BarDockEdge.Right, bar.DockEdge); Assert.Equal(new Point(-34, 60), bar.CompactPosition);
            Assert.True(areas[0].Contains(new Rect(bar.CompactPosition, bar.CompactSize)));
        } finally { bar.Close(); }
    });

    [Theory]
    [InlineData(.85)] [InlineData(1)] [InlineData(1.15)]
    public void DefaultAndScaleChangesStayBottomCenteredAndMonitorChangesPreserveTheWall(double scale) => fixture.Run(() => {
        Rect work = Work; var bar = new DictationBarWindow(() => [work]);
        try {
            bar.Configure(false, true, [], [], null, null, scale: scale);
            Assert.Equal(BarDockEdge.Bottom, bar.DockEdge);
            Assert.Equal(work.Left + work.Width / 2, bar.Left + bar.Width / 2, 5);
            Assert.Equal(work.Bottom - 18, bar.Top + bar.Height, 5);
            var saved = bar.CompactPosition;
            bar.Configure(false, true, [], [], saved.X, saved.Y, scale: 1.15, dockEdge: "Bottom");
            Assert.Equal(work.Left + work.Width / 2, bar.Left + bar.Width / 2, 5);
            work = new Rect(-1400, -200, 1400, 900); bar.UpdateState(true, false, false, null, "Ready");
            Assert.Equal(BarDockEdge.Bottom, bar.DockEdge); Assert.Equal(work.Bottom - 18, bar.Top + bar.Height, 5);
            Assert.True(work.Contains(new Rect(bar.CompactPosition, bar.CompactSize)));
        } finally { bar.Close(); }
    });

    [Theory]
    [InlineData("Left")] [InlineData("Right")] [InlineData("Top")] [InlineData("Bottom")]
    public void HoverGrowsInwardAndCollapseRestoresTheSameAlongEdgePosition(string edgeName) => fixture.Run(() => {
        var bar = new DictationBarWindow(() => [Work]); var edge = Enum.Parse<BarDockEdge>(edgeName);
        try {
            bar.Configure(false, true, ["PasteLast", "ToggleCaptions"], [], 500, 350, dockEdge: edgeName);
            var rest = new Rect(bar.Left, bar.Top, bar.Width, bar.Height); var anchor = bar.CompactPosition;
            bar.SetPointerInside(true); var hover = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            Assert.Equal(bar.IsVertical ? new Size(32, 192) : new Size(192, 32), hover.Size);
            Assert.True(hover.Contains(rest)); Assert.True(Work.Contains(hover));
            Assert.Equal(edge == BarDockEdge.Right ? rest.Right : edge == BarDockEdge.Left ? rest.Left : edge == BarDockEdge.Top ? rest.Top : rest.Bottom,
                edge == BarDockEdge.Right ? hover.Right : edge == BarDockEdge.Left ? hover.Left : edge == BarDockEdge.Top ? hover.Top : hover.Bottom);
            Assert.Equal(bar.IsVertical ? rest.Top + rest.Height / 2 : rest.Left + rest.Width / 2,
                bar.IsVertical ? hover.Top + hover.Height / 2 : hover.Left + hover.Width / 2);
            if (bar.IsVertical) {
                var actions = Descendants<UniformGrid>(bar).Single(); Assert.Equal(1, actions.Columns); Assert.Equal(0, actions.Rows);
                foreach (var button in Descendants<Button>(bar)) Assert.IsNotType<RotateTransform>(button.LayoutTransform);
            }
            bar.UpdateState(true, true, false, null, "Listening", inputActive: true);
            Assert.Equal(hover, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            bar.SetPointerInside(false); PumpFor(260);
            Assert.Equal(rest, new Rect(bar.Left, bar.Top, bar.Width, bar.Height)); Assert.Equal(anchor, bar.CompactPosition);
            Assert.False(bar.IsTransitioning);
        } finally { bar.Close(); }
    });

    [Fact] public void DragChangesOrientationOnlyOnReleaseAndPersistsTheVerticalWallPosition() => fixture.Run(() => {
        Point saved; string edge; var bar = new DictationBarWindow(() => [Work]);
        try {
            var positions = new List<Point>(); var actions = new List<string>(); bar.PositionChanged += positions.Add; bar.ActionRequested += actions.Add;
            bar.Configure(false, true, ["PasteLast"], [], 450, 58, dockEdge: "Top"); bar.SetPointerInside(true);
            var dpi = VisualTreeHelper.GetDpi(bar); var start = new Point(100, 100);
            var release = start + new Vector(-470 * dpi.DpiScaleX, 290 * dpi.DpiScaleY);
            bar.BeginPointer("PasteLast", start); bar.MovePointer(release);
            Assert.False(bar.IsVertical); Assert.Equal(new Size(192, 32), new Size(bar.Width, bar.Height)); Assert.Empty(positions); Assert.Empty(actions);
            bar.EndPointer(release); bar.EndPointer(release); saved = Assert.Single(positions); edge = bar.DockEdge.ToString();
            Assert.Equal("Left", edge); Assert.Equal(18, saved.X); Assert.Equal(new Size(32, 192), new Size(bar.Width, bar.Height)); Assert.Empty(actions);
        } finally { bar.Close(); }
        var restored = new DictationBarWindow(() => [Work]);
        try { restored.Configure(false, true, [], [], saved.X, saved.Y, dockEdge: edge); Assert.Equal(saved, restored.CompactPosition); Assert.True(restored.IsVertical); }
        finally { restored.Close(); }
    });

    [Theory]
    [InlineData("Left", .85)] [InlineData("Right", 1)] [InlineData("Left", 1.15)]
    public void VerticalStateAndScaleChangesPreserveTheWallAndAlongCenter(string edgeName, double scale) => fixture.Run(() => {
        var bar = new DictationBarWindow(() => [Work]);
        try {
            bar.Configure(false, true, [], [], 500, 350, scale: scale, dockEdge: edgeName);
            Assert.Equal(new Size(16 * scale, 96 * scale), bar.CompactSize);
            var rest = new Rect(bar.Left, bar.Top, bar.Width, bar.Height);
            bar.UpdateState(true, true, false, "Live words", "Listening", inputActive: true);
            Assert.Equal(rest, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            bar.UpdateState(true, false, true, "Live words", "Finishing");
            Assert.Equal(rest, new Rect(bar.Left, bar.Top, bar.Width, bar.Height));
            bar.Configure(false, true, [], [], rest.Left, rest.Top, scale: 1.15, dockEdge: edgeName);
            Assert.Equal(rest.Top + rest.Height / 2, bar.Top + bar.Height / 2, 5);
            Assert.Equal(edgeName, bar.DockEdge.ToString()); Assert.False(bar.IsExpanded);
        } finally { bar.Close(); }
    });

    [Theory]
    [InlineData("Left")] [InlineData("Top")]
    public void ExplicitEdgeDisambiguatesTheSameSavedPositionNearACorner(string edgeName) => fixture.Run(() => {
        var bar = new DictationBarWindow(() => [Work]);
        try {
            bar.Configure(false, true, [], [], 18, 58, dockEdge: edgeName);
            Assert.Equal(new Point(18, 58), bar.CompactPosition);
            Assert.Equal(edgeName, bar.DockEdge.ToString()); Assert.Equal(edgeName == "Left", bar.IsVertical);
        } finally { bar.Close(); }
    });

    [Theory]
    [InlineData("Left")] [InlineData("Right")] [InlineData("Top")] [InlineData("Bottom")]
    public void PopupsOpenInwardAndClampAtWorkAreaEnds(string edgeName)
    {
        var edge = Enum.Parse<BarDockEdge>(edgeName); var size = new Size(180, 120);
        var barSize = edge is BarDockEdge.Left or BarDockEdge.Right ? new Size(32, 192) : new Size(192, 32);
        var bar = new Rect(DictationBarWindow.EdgePosition(Work, barSize, new Point(600, 450), edge), barSize);
        var position = DictationBarWindow.PopupPosition(Work, bar, size, edge); Assert.True(Work.Contains(new Rect(position, size)));
        Assert.True(edge switch { BarDockEdge.Left => position.X >= bar.Right, BarDockEdge.Right => position.X + size.Width <= bar.Left,
            BarDockEdge.Top => position.Y >= bar.Bottom, _ => position.Y + size.Height <= bar.Top });
        Assert.True(Work.Contains(new Rect(DictationBarWindow.PopupPosition(Work, new Rect(Work.TopLeft, barSize), size, edge), size)));
    }

    [Fact] public void SmallAreasReduceInsetsAndClampWithoutInvalidCoordinates()
    {
        var work = new Rect(-110, -40, 110, 40); var compact = new Size(96, 16);
        Assert.Equal(new Point(-103, -28), DictationBarWindow.EdgePosition(work, compact, new Point(-55, -20), BarDockEdge.Bottom));
        Assert.Equal(work.TopLeft, DictationBarWindow.EdgePosition(work, new Size(192, 80), new Point(), BarDockEdge.Right));
    }

    [Theory]
    [InlineData(1, "Left", 37, 35)] [InlineData(1.5, "Left", 55.5, 52.5)]
    [InlineData(1, "Right", -187, 35)] [InlineData(1.5, "Right", -280.5, 52.5)]
    [InlineData(1, "Top", 5, 37)] [InlineData(1.5, "Top", 7.5, 55.5)]
    [InlineData(1, "Bottom", 5, -127)] [InlineData(1.5, "Bottom", 7.5, -190.5)]
    public void PopupCallbackConvertsPixelSizesAndOffsetsAtEachDpi(double scale, string edgeName, double expectedX, double expectedY)
    {
        var edge = Enum.Parse<BarDockEdge>(edgeName); var work = new Rect(0, 0, 1000, 700);
        var bar = edge switch { BarDockEdge.Left => new Rect(18, 200, 32, 192), BarDockEdge.Right => new Rect(950, 200, 32, 192),
            BarDockEdge.Top => new Rect(300, 18, 192, 32), _ => new Rect(300, 650, 192, 32) };
        var offset = DictationBarWindow.PopupCallbackOffset(work, bar, new Size(180 * scale, 120 * scale),
            new Point(1, 1), new DpiScale(scale, scale), edge);
        Assert.Equal(expectedX, offset.X, 5); Assert.Equal(expectedY, offset.Y, 5);
    }

    [Theory]
    [InlineData(1)] [InlineData(1.5)]
    public void PopupCallbackFollowsIntermediateAndExpandedFramesWithoutOverlappingTheBar(double scale)
    {
        var work = new Rect(-1000, 0, 1000, 700); var dpi = new DpiScale(scale, scale);
        foreach (var frame in new[] { new Rect(-982, 250, 16, 96), new Rect(-982, 226, 24, 144), new Rect(-982, 202, 32, 192) }) {
            var offset = DictationBarWindow.PopupCallbackOffset(work, frame, new Size(180 * scale, 120 * scale), new Point(1, 1), dpi, BarDockEdge.Left);
            double left = frame.Left + 1 + offset.X / scale;
            Assert.Equal(frame.Right + 6, left, 5);
            Assert.True(work.Contains(new Rect(left, frame.Top + 1 + offset.Y / scale, 180, 120)));
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
    }
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
