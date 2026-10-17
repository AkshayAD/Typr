using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Typr.Core;

namespace Typr.Windows;

internal sealed class CaptionOverlay : Window
{
    private readonly StackPanel _lines = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _meter;
    private readonly Border _badge;
    private readonly TextBlock _state = new() { Text = "Listening", FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private AppSettings _settings = new();
    private bool _active;
    private nint _target;
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromSeconds(3) };
    private nint _handle;

    public CaptionOverlay()
    {
        Width = 640; Height = 216; Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ShowInTaskbar = false; ShowActivated = false;
        Topmost = true; ResizeMode = ResizeMode.NoResize; Focusable = false;
        var root = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20) };
        root.Children.Add(_lines);
        _meter = new Border { Width = 26, Height = 5, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(182, 170, 241)), VerticalAlignment = VerticalAlignment.Center };
        var indicator = new StackPanel { Orientation = Orientation.Horizontal };
        indicator.Children.Add(_meter); indicator.Children.Add(_state);
        _badge = new Border { Child = indicator, Background = new SolidColorBrush(Color.FromRgb(36, 39, 37)), CornerRadius = new CornerRadius(20), Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        root.Children.Add(_badge); Content = root;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExstyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExstyle, (nint)(style | NativeMethods.WsExNoactivate | NativeMethods.WsExToolwindow | NativeMethods.WsExTopmost | NativeMethods.WsExTransparent));
        };
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); _active = false; Hide(); };
    }

    public void Configure(AppSettings settings)
    {
        _settings = settings;
        _badge.Visibility = settings.BarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.LiveCaptionsEnabled) _lines.Children.Clear();
        if (!settings.BarVisible && !settings.LiveCaptionsEnabled) Hide();
        else if (_active) { Show(); Position(_target); }
    }

    public void Begin(nint target)
    {
        _dismiss.Stop(); _active = true; _target = target; _lines.Children.Clear(); _meter.Background = new SolidColorBrush(Color.FromRgb(182, 170, 241));
        _state.Text = "Opening microphone…";
        _meter.Width = 26;
        if (_settings.BarVisible || _settings.LiveCaptionsEnabled) { Show(); Position(target); }
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
        if (!_settings.LiveCaptionsEnabled) return;
        var visible = lines.TakeLast(_settings.CaptionMaxLines).ToArray();
        _lines.Children.Clear();
        for (int i = 0; i < visible.Length; i++)
        {
            var text = new TextBlock { Text = visible[i], FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            if (SystemParameters.HighContrast) text.Foreground = SystemColors.WindowTextBrush;
            _lines.Children.Add(new Border { Child = text, CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 3, 0, 0), Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromArgb((byte)(Math.Max(.6, _settings.CaptionOpacity) * 255), 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 600, Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = .4 } });
        }
    }

    public void Listening() => _state.Text = "Listening · Esc cancels";
    public void Level(float value) => _meter.Width = 26 + Math.Clamp(value, 0, 1) * 34;
    public void Processing() { _state.Text = "Finishing your words…"; _meter.Background = new SolidColorBrush(Color.FromRgb(245, 188, 79)); _meter.Width = 40; }
    public void Complete(string text) { _state.Text = "Words ready"; _meter.Background = new SolidColorBrush(Color.FromRgb(145, 207, 172)); SetLines(new[] { "✓ " + (text.Length > 60 ? text[..60] + "…" : text) }); _dismiss.Start(); }
    public void Error(string message) { _state.Text = "Check Typr"; SetLines(new[] { message }); _meter.Background = Brushes.OrangeRed; if (_settings.LiveCaptionsEnabled || _settings.BarVisible) { Show(); Position(_target); } _dismiss.Start(); }
    public void Cancel() { _dismiss.Stop(); _active = false; Hide(); }
}
