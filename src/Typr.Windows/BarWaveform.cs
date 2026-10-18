using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Typr.Windows;

internal enum BarActivity { Idle, Listening, Finishing }

/// <summary>Small audio-driven marks; the animation clock runs only for visible active work.</summary>
internal sealed class BarWaveform : FrameworkElement
{
    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly long started = Stopwatch.GetTimestamp();
    BarActivity activity;
    double targetLevel, level;
    internal bool IsAnimating => clock.IsEnabled;
    internal BarActivity Activity => activity;

    public BarWaveform()
    {
        Width = 34; Height = 14; IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) => RefreshClock();
        clock.Tick += (_, _) => {
            if (!ShouldAnimate) { RefreshClock(); return; }
            level += (targetLevel - level) * .4;
            InvalidateVisual();
        };
    }

    internal void SetActivity(BarActivity value)
    {
        activity = value;
        if (value != BarActivity.Listening) targetLevel = level = 0;
        RefreshClock(); InvalidateVisual();
    }

    internal void SetLevel(float value)
    {
        targetLevel = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        if (!ShouldAnimate) { level = targetLevel; InvalidateVisual(); }
    }

    bool ShouldAnimate => IsVisible && activity != BarActivity.Idle && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    void RefreshClock() { if (ShouldAnimate) clock.Start(); else clock.Stop(); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var brush = TryFindResource(activity == BarActivity.Listening ? "AccentBrush" : activity == BarActivity.Finishing ? "TextBrush" : "MutedBrush") as Brush ?? SystemColors.WindowTextBrush;
        double phase = ShouldAnimate ? Stopwatch.GetElapsedTime(started).TotalSeconds : 0;
        if (activity == BarActivity.Finishing) {
            for (int i = 0; i < 3; i++) {
                double radius = 1.5 + (ShouldAnimate ? (1 + Math.Sin(phase * 5 - i * .9)) * .45 : 0);
                drawing.DrawEllipse(brush, null, new Point(ActualWidth / 2 + (i - 1) * 7, ActualHeight / 2), radius, radius);
            }
            return;
        }
        for (int i = 0; i < 9; i++) {
            double envelope = 1 - Math.Abs(i - 4) / 5d;
            double height = activity == BarActivity.Listening
                ? 2 + level * (ActualHeight - 2) * (.5 + .5 * Math.Sin(phase * 6 + i * .8))
                : 2 + envelope * 5;
            height = Math.Min(ActualHeight, height);
            double spacing = Math.Min(3.3, Math.Max(0, (ActualWidth - 1.8) / 8));
            drawing.DrawRoundedRectangle(brush, null, new Rect(ActualWidth / 2 + (i - 4) * spacing - .9, (ActualHeight - height) / 2, 1.8, height), .9, .9);
        }
    }
}
