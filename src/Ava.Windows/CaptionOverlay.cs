using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Ava.Windows;

internal sealed class CaptionOverlay : Window
{
    private readonly StackPanel _lines = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _meter;
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromSeconds(3) };
    private nint _handle;

    public CaptionOverlay()
    {
        Width = 640; Height = 170; Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ShowInTaskbar = false; ShowActivated = false;
        Topmost = true; ResizeMode = ResizeMode.NoResize; Focusable = false;
        var root = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20) };
        root.Children.Add(_lines);
        _meter = new Border { Width = 34, Height = 7, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(71, 224, 167)), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        root.Children.Add(_meter); Content = root;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExstyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExstyle, (nint)(style | NativeMethods.WsExNoactivate | NativeMethods.WsExToolwindow | NativeMethods.WsExTopmost | NativeMethods.WsExTransparent));
        };
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); Hide(); };
    }

    public void Begin(nint target)
    {
        _dismiss.Stop(); _lines.Children.Clear(); _meter.Background = new SolidColorBrush(Color.FromRgb(71, 224, 167));
        _meter.Width = 34; Show(); Position(target);
    }

    private void Position(nint target)
    {
        var monitor = NativeMethods.MonitorFromWindow(target, 2);
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        Left = topLeft.X + Math.Max(0, (bottomRight.X - topLeft.X - Width) / 2);
        Top = Math.Max(topLeft.Y, bottomRight.Y - Height - 18);
    }

    public void SetLines(IEnumerable<string> lines)
    {
        var visible = lines.TakeLast(2).ToArray();
        _lines.Children.Clear();
        for (int i = 0; i < visible.Length; i++)
        {
            var text = new TextBlock { Text = visible[i], FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            _lines.Children.Add(new Border { Child = text, CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 3, 0, 0), Background = new SolidColorBrush(Color.FromArgb(38, 0, 0, 0)), Opacity = i == visible.Length - 1 ? 1 : 0.7, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 600, Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = .4 } });
        }
    }

    public void Level(float value) => _meter.Width = 34 + Math.Clamp(value, 0, 1) * 60;
    public void Processing() { _meter.Background = new SolidColorBrush(Color.FromRgb(245, 188, 79)); _meter.Width = 55; }
    public void Complete(string text) { SetLines(new[] { "✓ " + (text.Length > 60 ? text[..60] + "…" : text) }); _dismiss.Start(); }
    public void Error(string message) { SetLines(new[] { message }); _meter.Background = Brushes.OrangeRed; _dismiss.Start(); }
    public void Cancel() { _dismiss.Stop(); Hide(); }
}
