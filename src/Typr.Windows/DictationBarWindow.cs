using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Typr.Windows;

/// <summary>A mouse-operated, non-activating companion to the user's current text field.</summary>
internal sealed class DictationBarWindow : Window
{
    private const double BarWidth = 340, CompactHeight = 66;
    private static readonly DependencyProperty SurfacePaletteProperty = DependencyProperty.Register("SurfacePalette", typeof(Brush), typeof(DictationBarWindow),
        new PropertyMetadata(Brushes.White, (owner, _) => ((DictationBarWindow)owner).ApplyBackground()));
    private readonly Border _shell;
    private readonly Grid _presentation = new() { Width = BarWidth };
    private readonly Grid _layout = new();
    private readonly Grid _main = new() { Height = 44 };
    private readonly StackPanel _tray = new() { Margin = new Thickness(2, 2, 2, 8) };
    private readonly UniformGrid _actions = new() { Columns = 3 };
    private readonly TextBlock _trayTitle = new() { FontSize = 10, Margin = new Thickness(5, 2, 5, 6), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _caption = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40, Margin = new Thickness(8, 5, 8, 9) };
    private readonly TextBlock _status = new() { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, MaxWidth = 122 };
    private readonly Button _primary, _hold;
    private readonly DispatcherTimer _collapse = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _expand = new();
    private string[] _hoverActions = ["PasteLast", "ToggleMicrophone", "ToggleCaptions", "OpenWorkspace", "Cancel", "HideBar"];
    private string[] _contextActions = ["ToggleRecording", "HoldToTalk", "Cancel", "PasteLast", "ToggleMicrophone", "ToggleCaptions", "OpenWorkspace", "HideBar"];
    private bool _captionsEnabled = true, _micReady, _recording, _busy, _expanded, _contextOpen, _pointerInside;
    private bool _pointerDown, _dragging, _holdActive, _positionSet, _closed;
    private bool _actionsDirty, _primaryDirty;
    private bool _userPosition, _allowButtonRecording = true;
    private double _barOpacity = .78, _barScale = 1;
    private int _hoverDelay;
    private string? _pressedAction;
    private Point _anchor, _pointerStart, _dragOrigin;
    private DpiScale _dragDpi = new(1, 1);
    private HwndSource? _source;

    public event Action<string>? ActionRequested;
    /// <summary>The compact bar's top-left, unaffected by hover expansion.</summary>
    public event Action<Point>? PositionChanged;

    public DictationBarWindow()
    {
        Title = "Typr dictation bar";
        Width = BarWidth; Height = CompactHeight;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(this, "Typr floating dictation toolbar");

        _shell = new Border { CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1), Padding = new Thickness(6), Margin = new Thickness(4), Child = _layout,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = .18 } };
        _shell.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _presentation.Children.Add(_shell);
        Content = new Viewbox { Child = _presentation, Stretch = Stretch.Fill };
        SetResourceReference(SurfacePaletteProperty, "SurfaceBrush");
        ApplyBackground();
        for (int row = 0; row < 3; row++) _layout.RowDefinitions.Add(new RowDefinition());
        _trayTitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(_caption, "Live dictation caption");
        AutomationProperties.SetLiveSetting(_caption, AutomationLiveSetting.Polite);
        _tray.Children.Add(_trayTitle); _tray.Children.Add(_actions);
        _layout.Children.Add(_tray); _layout.Children.Add(_caption); _layout.Children.Add(_main);

        foreach (var width in new[] { new GridLength(13), new GridLength(1, GridUnitType.Star), new GridLength(44), new GridLength(42), new GridLength(32), new GridLength(28) })
            _main.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var grip = new TextBlock { Text = "⋮", FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll, ToolTip = "Drag anywhere on the bar to move it" };
        grip.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AddMain(grip, 0);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 6, 0) };
        identity.Children.Add(new TextBlock { Text = "typr", FontSize = 15, FontWeight = FontWeights.SemiBold });
        identity.Children.Add(_status); AddMain(identity, 1);
        _primary = CreateButton("ToggleMicrophone", compact: true);
        _primary.SetResourceReference(BackgroundProperty, "AccentBrush");
        _primary.SetResourceReference(ForegroundProperty, "AccentTextBrush");
        AddMain(_primary, 2);
        _hold = CreateButton("HoldToTalk", compact: true); AddMain(_hold, 3);
        AddMain(CreateButton("OpenWorkspace", compact: true), 4);
        AddMain(CreateButton("ShowMenu", compact: true), 5);

        SourceInitialized += (_, _) => {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            var style = NativeMethods.GetWindowLongPtr(_source.Handle, NativeMethods.GwlExstyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_source.Handle, NativeMethods.GwlExstyle,
                (nint)(style | NativeMethods.WsExNoactivate | NativeMethods.WsExToolwindow));
            _source.AddHook(WindowMessage);
        };
        PreviewMouseLeftButtonDown += PointerDown;
        PreviewMouseMove += PointerMove;
        PreviewMouseLeftButtonUp += PointerUp;
        PreviewMouseRightButtonUp += (_, e) => { if (!_pointerDown) ToggleMenu(); e.Handled = true; };
        LostMouseCapture += (_, _) => { if (_pointerDown) CancelPointer(); };
        MouseEnter += (_, _) => SetPointerInside(true);
        MouseLeave += (_, _) => SetPointerInside(false);
        _collapse.Tick += (_, _) => {
            _collapse.Stop();
            if (!_pointerInside && !_pointerDown) { _contextOpen = false; SetExpanded(false); }
        };
        _expand.Tick += (_, _) => {
            _expand.Stop();
            if (_pointerInside && !_pointerDown) SetExpanded(true);
        };
        Closed += (_, _) => {
            _closed = true; _collapse.Stop(); _expand.Stop(); CancelPointer();
            _source?.RemoveHook(WindowMessage);
        };
        RebuildActions();
        UpdateState(false, false, false, null, "Microphone closed");
    }

    public void Configure(bool visible, bool captions, string[] hoverActions, string[] contextActions, double? left, double? top,
        double opacity = .78, double scale = 1, int hoverDelay = 0, bool allowButtonRecording = true)
    {
        var hover = FilterActions(hoverActions); var context = FilterActions(contextActions);
        bool actionsChanged = _captionsEnabled != captions || _allowButtonRecording != allowButtonRecording || !_hoverActions.SequenceEqual(hover) || !_contextActions.SequenceEqual(context);
        bool scaleChanged = _barScale != scale;
        _barOpacity = double.IsFinite(opacity) ? Math.Clamp(opacity, .2, 1) : .78;
        _barScale = scale is .85 or 1 or 1.15 ? scale : 1;
        _hoverDelay = hoverDelay is 0 or 150 or 300 or 500 ? hoverDelay : 150;
        _allowButtonRecording = allowButtonRecording;
        _captionsEnabled = captions; _hoverActions = hover; _contextActions = context;
        if (!_pointerDown && left is { } x && top is { } y && double.IsFinite(x) && double.IsFinite(y)) {
            _anchor = ClampToWorkAreas(new Point(x, y), CompactSize, WorkAreas());
            _positionSet = _userPosition = true;
        }
        else if (scaleChanged && !_userPosition) _positionSet = false;
        if (!_pointerDown) _anchor = ClampToWorkAreas(_anchor, CompactSize, WorkAreas());
        ApplyBackground();
        if (actionsChanged) RebuildActions();
        UpdatePrimary(); _hold.IsEnabled = _holdActive || CanExecute("HoldToTalk");
        ApplyLayout();
        if (visible) ShowBar(); else HideBar();
    }

    public void UpdateState(bool micReady, bool recording, bool busy, string? caption, string status)
    {
        bool actionsChanged = _micReady != micReady || _recording != recording || _busy != busy;
        _micReady = micReady; _recording = recording; _busy = busy;
        _status.Text = status;
        _status.ToolTip = status;
        _caption.Text = caption ?? "";
        if (actionsChanged) UpdatePrimary();
        _hold.IsEnabled = _holdActive || CanExecute("HoldToTalk");
        if (actionsChanged) RebuildActions();
        ApplyLayout();
    }

    public void ShowBar()
    {
        if (_closed) return;
        EnsurePosition(); ApplyLayout();
        if (!IsVisible) Show();
    }

    public void HideBar()
    {
        _collapse.Stop(); _expand.Stop(); CancelPointer();
        _pointerInside = _contextOpen = false; SetExpanded(false);
        if (IsVisible) Hide();
    }

    private void AddMain(UIElement child, int column) { Grid.SetColumn(child, column); _main.Children.Add(child); }

    private Button CreateButton(string action, bool compact)
    {
        var button = new Button { Focusable = false, IsTabStop = false, Padding = new Thickness(4), Margin = new Thickness(2), MinHeight = 0, Height = 36, Cursor = Cursors.Hand };
        SetButtonContent(button, action, compact);
        button.Click += (_, _) => ExecuteAction((string)button.Tag);
        return button;
    }

    private void SetButtonContent(Button button, string action, bool compact)
    {
        var (symbol, label, tip) = Describe(action);
        button.Tag = action;
        button.IsEnabled = CanExecute(action);
        button.ToolTip = tip;
        AutomationProperties.SetName(button, label);
        if (compact) button.Content = new TextBlock { Text = action == "HoldToTalk" ? "Hold" : symbol, FontSize = action == "HoldToTalk" ? 10 : 17, FontFamily = new FontFamily(action == "HoldToTalk" ? "Segoe UI" : "Segoe Fluent Icons, Segoe MDL2 Assets"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        else button.Content = new TextBlock { Text = label, FontSize = 10, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetInitialShowDelay(button, 350);
        ToolTipService.SetShowDuration(button, 10_000);
    }

    private (string Symbol, string Label, string Tip) Describe(string action) => action switch
    {
        "ToggleRecording" => (_recording ? "\uE71A" : "\uE720", _recording ? "Finish dictation" : "Start dictation", _allowButtonRecording || _recording ? "Start or finish dictation · Ctrl + Alt; add Space for hands-free" : "Shortcut-only mode · enable button recording in Appearance to start here"),
        "HoldToTalk" => ("\uE8D4", "Hold to talk", _allowButtonRecording ? "Hold this control while speaking, then release to finish · Ctrl + Alt" : "Shortcut-only mode · hold Ctrl + Alt to talk, or enable button recording in Appearance"),
        "Cancel" => ("\uE711", "Cancel", "Cancel pending dictation · Esc; inserted text remains"),
        "PasteLast" => ("\uE77F", "Paste last", "Paste the latest completed result · choose your shortcut in Settings"),
        "ToggleMicrophone" => (_micReady ? "\uE74F" : "\uE720", _micReady ? "Unload speech" : "Prepare speech", "Prepare or unload local speech. The microphone opens only for a dictation shortcut."),
        "ToggleCaptions" => ("\uE8F2", _captionsEnabled ? "Hide captions" : "Show captions", "Show or hide live captions"),
        "OpenWorkspace" => ("\uE80F", "Open Typr", "Open the Typr workspace"),
        "HideBar" => ("\uE76C", "Hide bar", "Hide this bar; show it again in Typr Appearance"),
        _ => ("\uE712", "Bar actions", "Show your right-click actions; drag any control to move the bar")
    };

    private bool CanExecute(string action) => action switch
    {
        "HoldToTalk" => _allowButtonRecording && _micReady && !_recording && !_busy,
        "ToggleRecording" => _recording || _allowButtonRecording && _micReady && !_busy,
        "Cancel" => _recording || _busy,
        "PasteLast" => !_recording && !_busy,
        _ => true
    };

    internal static string[] FilterActions(IEnumerable<string>? actions) => (actions ?? [])
        .Where(action => action is "ToggleRecording" or "HoldToTalk" or "Cancel" or "PasteLast" or "ToggleMicrophone" or "ToggleCaptions" or "OpenWorkspace" or "HideBar")
        .Distinct(StringComparer.Ordinal).Take(9).ToArray();

    private void RebuildActions()
    {
        if (_pointerDown) { _actionsDirty = true; return; }
        _actionsDirty = false;
        _actions.Children.Clear();
        foreach (string action in _contextOpen ? _contextActions : _hoverActions) _actions.Children.Add(CreateButton(action, compact: false));
        _trayTitle.Text = _contextOpen ? "YOUR BAR ACTIONS" : "QUICK ACTIONS";
    }

    private void UpdatePrimary()
    {
        if (_pointerDown) { _primaryDirty = true; return; }
        _primaryDirty = false;
        SetButtonContent(_primary, _busy ? "Cancel" : _micReady || _recording ? "ToggleRecording" : "ToggleMicrophone", compact: true);
    }

    private void RefreshDeferredControls()
    {
        if (_actionsDirty) RebuildActions();
        if (_primaryDirty) UpdatePrimary();
    }

    internal void SetPointerInside(bool inside)
    {
        _pointerInside = inside;
        if (inside) { _collapse.Stop(); StartHoverExpansion(); }
        else { _expand.Stop(); if (!_pointerDown) _collapse.Start(); }
    }

    private void StartHoverExpansion()
    {
        _expand.Stop();
        if (_expanded || _pointerDown) return;
        if (_hoverDelay == 0) SetExpanded(true);
        else { _expand.Interval = TimeSpan.FromMilliseconds(_hoverDelay); _expand.Start(); }
    }

    private void ToggleMenu()
    {
        _collapse.Stop(); _expand.Stop(); _contextOpen = !_contextOpen;
        RebuildActions(); SetExpanded(_contextOpen || _pointerInside);
    }

    private void SetExpanded(bool expanded) { _expanded = expanded; ApplyLayout(); }

    private void EnsurePosition()
    {
        if (_positionSet) return;
        var work = SystemParameters.WorkArea;
        _anchor = new Point(work.Left + (work.Width - CompactSize.Width) / 2, work.Bottom - CompactSize.Height - 18);
        _positionSet = true;
    }

    private void ApplyLayout()
    {
        EnsurePosition();
        int count = _actions.Children.Count;
        double trayHeight = _expanded && count > 0 ? 34 + Math.Ceiling(count / 3d) * 40 : 0;
        double captionHeight = _captionsEnabled && !string.IsNullOrWhiteSpace(_caption.Text) ? 54 : 0;
        double extra = trayHeight + captionHeight;
        var area = NearestWorkArea(_anchor, CompactSize, WorkAreas());
        bool upwards = _anchor.Y - extra * _barScale >= area.Top;
        _layout.RowDefinitions[0].Height = new GridLength(upwards ? trayHeight : 44);
        _layout.RowDefinitions[1].Height = new GridLength(captionHeight);
        _layout.RowDefinitions[2].Height = new GridLength(upwards ? 44 : trayHeight);
        Grid.SetRow(_main, upwards ? 2 : 0); Grid.SetRow(_tray, upwards ? 0 : 2); Grid.SetRow(_caption, 1);
        _tray.Visibility = trayHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        _caption.Visibility = captionHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        _presentation.Height = CompactHeight + extra;
        Width = CompactSize.Width; Height = (CompactHeight + extra) * _barScale;
        Left = _anchor.X; Top = _anchor.Y - (upwards ? extra * _barScale : 0);
    }

    private void ApplyBackground()
    {
        if (_shell == null) return;
        var color = (GetValue(SurfacePaletteProperty) as SolidColorBrush)?.Color ?? Colors.White;
        if (SystemParameters.HighContrast) color = SystemColors.WindowColor;
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * (SystemParameters.HighContrast ? 1 : _barOpacity)), color.R, color.G, color.B));
        brush.Freeze(); _shell.Background = brush;
    }

    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        string? action = null;
        for (var source = e.OriginalSource as DependencyObject; source != null && source != this; source = VisualTreeHelper.GetParent(source))
            if (source is Button button) { action = button.IsEnabled ? button.Tag as string : null; break; }
        BeginPointer(action, CursorPosition());
        Mouse.Capture(this, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown) return;
        MovePointer(CursorPosition()); e.Handled = true;
    }

    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown) return;
        EndPointer(CursorPosition());
        if (Mouse.Captured == this) Mouse.Capture(null);
        e.Handled = true;
    }

    internal void BeginPointer(string? action, Point screenPoint)
    {
        if (_pointerDown) CancelPointer();
        _collapse.Stop(); _expand.Stop(); EnsurePosition();
        _pointerDown = true; _dragging = false; _pressedAction = action;
        _pointerStart = screenPoint; _dragOrigin = _anchor; _dragDpi = VisualTreeHelper.GetDpi(this);
        if (action == "HoldToTalk" && CanExecute(action)) {
            _holdActive = true;
            ActionRequested?.Invoke("HoldStart");
        }
    }

    internal void MovePointer(Point screenPoint)
    {
        if (!_pointerDown) return;
        var delta = new Vector((screenPoint.X - _pointerStart.X) / _dragDpi.DpiScaleX, (screenPoint.Y - _pointerStart.Y) / _dragDpi.DpiScaleY);
        if (!_dragging && (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance)) {
            _dragging = true;
            _userPosition = true;
            if (_holdActive) { _holdActive = false; ActionRequested?.Invoke("Cancel"); }
        }
        if (_dragging) {
            _anchor = ClampToWorkAreas(_dragOrigin + delta, CompactSize, WorkAreas());
            ApplyLayout();
        }
    }

    internal void EndPointer(Point screenPoint)
    {
        if (!_pointerDown) return;
        MovePointer(screenPoint);
        bool dragged = _dragging, held = _holdActive;
        string? action = _pressedAction;
        _pointerDown = _dragging = _holdActive = false; _pressedAction = null;
        RefreshDeferredControls();
        if (dragged) PositionChanged?.Invoke(_anchor);
        else if (held) ActionRequested?.Invoke("HoldFinish");
        else if (action != "HoldToTalk" && action != null) ExecuteAction(action);
        if (!_pointerInside) _collapse.Start(); else StartHoverExpansion();
    }

    private void CancelPointer()
    {
        bool held = _holdActive;
        _pointerDown = _dragging = _holdActive = false; _pressedAction = null;
        RefreshDeferredControls();
        if (Mouse.Captured == this) Mouse.Capture(null);
        if (held) ActionRequested?.Invoke("Cancel");
    }

    private void ExecuteAction(string action)
    {
        if (!CanExecute(action)) return;
        if (action == "ShowMenu") { ToggleMenu(); return; }
        // A hold control has no synthetic-click equivalent: invoking it must not
        // leave an unpaired recording running when there is no matching release.
        if (action == "HoldToTalk") return;
        if (action == "HideBar") HideBar();
        ActionRequested?.Invoke(action);
    }

    internal Point CompactPosition => _anchor;
    internal bool IsExpanded => _expanded;
    internal Size CompactSize => new(BarWidth * _barScale, CompactHeight * _barScale);

    internal static Point ClampToWorkAreas(Point position, Size size, IReadOnlyList<Rect> areas)
    {
        var work = NearestWorkArea(position, size, areas);
        return new Point(Math.Clamp(position.X, work.Left, Math.Max(work.Left, work.Right - size.Width)),
            Math.Clamp(position.Y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));
    }

    private static Rect NearestWorkArea(Point position, Size size, IReadOnlyList<Rect> areas)
    {
        if (areas.Count == 0) return SystemParameters.WorkArea;
        var center = position + new Vector(size.Width / 2, size.Height / 2);
        return areas.OrderBy(area => {
            double x = Math.Clamp(center.X, area.Left, area.Right), y = Math.Clamp(center.Y, area.Top, area.Bottom);
            return (center.X - x) * (center.X - x) + (center.Y - y) * (center.Y - y);
        }).First();
    }

    private IReadOnlyList<Rect> WorkAreas()
    {
        var result = new List<Rect>();
        var dpi = VisualTreeHelper.GetDpi(this);
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref NativeMethods.Rect bounds, nint data) => {
            var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
                result.Add(new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
                    (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY));
            return true;
        }, 0);
        return result;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x84 && IsVisible) { // WM_NCHITTEST: shadow margins must not intercept the underlying app.
            var point = _shell.PointFromScreen(new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF)));
            if (!new Rect(_shell.RenderSize).Contains(point)) { handled = true; return -1; }
        }
        return 0;
    }

    private static Point CursorPosition() { GetCursorPos(out var point); return new Point(point.X, point.Y); }
    private delegate bool MonitorCallback(nint monitor, nint device, ref NativeMethods.Rect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint device, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativeMethods.Point point);
}
