using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Typr.Windows;

/// <summary>A quiet, non-activating companion to the user's current text field.</summary>
internal sealed class DictationBarWindow : Window
{
    internal const double RestingWidth = 96, RestingHeight = 22, ExpandedWidth = 280, ExpandedHeight = 44;
    private static readonly DependencyProperty SurfacePaletteProperty = DependencyProperty.Register("SurfacePalette", typeof(Brush), typeof(DictationBarWindow),
        new PropertyMetadata(Brushes.White, (owner, _) => ((DictationBarWindow)owner).ApplyBackground()));
    private readonly Border _shell;
    private readonly Grid _presentation = new(), _layout = new(), _main = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _tray = new();
    private readonly UniformGrid _actions = new() { Rows = 1, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly WrapPanel _contextPanel = new();
    private readonly TextBlock _caption = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40, Margin = new Thickness(6, 5, 6, 9) };
    private readonly FrameworkElement _rest;
    private readonly BarWaveform _processingWave = new() { Width = 26 };
    private readonly TextBlock _processingText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
    private readonly StackPanel _processing = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _primary;
    private readonly DispatcherTimer _collapse = new() { Interval = TimeSpan.FromMilliseconds(200) }, _expand = new();
    private readonly DispatcherTimer _transition = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _gestureWatch = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _hintDelay = new() { Interval = TimeSpan.FromMilliseconds(450) }, _hintDismiss = new() { Interval = TimeSpan.FromSeconds(5) };
    private Button? _hintTarget;
    private ToolTip? _openHint;
    private string[] _hoverActions = ["HoldToTalk", "PasteLast", "ToggleMicrophone", "ToggleCaptions"];
    private string[] _contextActions = ["OpenWorkspace", "ToggleMicrophone", "PasteLast", "ToggleCaptions", "HideBar"];
    private bool _captionsEnabled = true, _micReady, _recording, _busy, _expanded, _contextOpen, _pointerInside;
    private bool _pointerDown, _dragging, _holdActive, _positionSet, _closed, _actionsDirty, _primaryDirty;
    private bool _userPosition, _allowButtonRecording = true;
    private bool _layoutUpwards = true, _dragLayoutPinned;
    private double _barOpacity = .78, _barScale = 1;
    private int _hoverDelay, _physicalButton;
    private string? _pressedAction;
    private Point _anchor, _pointerStart, _dragOrigin;
    private Rect _gestureBounds, _transitionFrom, _transitionTo;
    private long _transitionStarted;
    private DpiScale _dragDpi = new(1, 1);
    private HwndSource? _source;

    public event Action<string>? ActionRequested;
    /// <summary>The resting pill's top-left; growth never changes this saved position.</summary>
    public event Action<Point>? PositionChanged;

    public DictationBarWindow()
    {
        Title = "Typr dictation bar"; Width = RestingWidth; Height = RestingHeight;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(ForegroundProperty, "TextBrush");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Typr;component/BarResources.xaml", UriKind.Relative) });
        AutomationProperties.SetName(this, "Typr floating dictation toolbar");
        _shell = new Border { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(2), Child = _layout,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = .12 } };
        _shell.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _presentation.Children.Add(_shell); Content = new Viewbox { Child = _presentation, Stretch = Stretch.Fill };
        SetResourceReference(SurfacePaletteProperty, "SurfaceBrush");
        for (int row = 0; row < 3; row++) _layout.RowDefinitions.Add(new RowDefinition());
        _rest = CreateWave();
        AutomationProperties.SetHelpText(_shell, "Hover for controls. Drag anywhere to move. Right-click for actions.");
        var title = new TextBlock { Text = "Quick actions", FontSize = 11, Margin = new Thickness(7, 4, 7, 5) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(_caption, "Live dictation caption");
        AutomationProperties.SetLiveSetting(_caption, AutomationLiveSetting.Polite);
        _tray.Children.Add(title); _tray.Children.Add(_contextPanel);
        _layout.Children.Add(_tray); _layout.Children.Add(_caption); _layout.Children.Add(_main); _layout.Children.Add(_rest);
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        _main.ColumnDefinitions.Add(new ColumnDefinition());
        _main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        _primary = CreateButton("ToggleMicrophone", true);
        _primary.SetResourceReference(ForegroundProperty, "TextBrush");
        _main.Children.Add(_primary);
        Grid.SetColumn(_actions, 1); _main.Children.Add(_actions);
        _processing.Children.Add(_processingWave); _processing.Children.Add(_processingText);
        AutomationProperties.SetLiveSetting(_processingText, AutomationLiveSetting.Polite);
        Grid.SetColumn(_processing, 1); _main.Children.Add(_processing);
        var menu = CreateButton("ShowMenu", true); Grid.SetColumn(menu, 2); _main.Children.Add(menu);
        SourceInitialized += (_, _) => {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            long style = NativeMethods.GetWindowLongPtr(_source.Handle, NativeMethods.GwlExstyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_source.Handle, NativeMethods.GwlExstyle, (nint)(style | NativeMethods.WsExNoactivate | NativeMethods.WsExToolwindow));
            _source.AddHook(WindowMessage);
        };
        PreviewMouseLeftButtonDown += PointerDown; PreviewMouseMove += PointerMove; PreviewMouseLeftButtonUp += PointerUp;
        PreviewMouseRightButtonUp += (_, e) => { if (!_pointerDown) ToggleMenu(); e.Handled = true; };
        LostMouseCapture += (_, _) => { if (_pointerDown) CancelPointer(); };
        MouseEnter += (_, _) => SetPointerInside(true); MouseLeave += (_, _) => SetPointerInside(false);
        _collapse.Tick += (_, _) => { _collapse.Stop(); if (!_pointerInside && !_pointerDown) { ResetContextActions(); SetExpanded(false); } };
        _expand.Tick += (_, _) => { _expand.Stop(); if (_pointerInside && !_pointerDown) SetExpanded(true); };
        _hintDelay.Tick += (_, _) => {
            _hintDelay.Stop();
            if (IsVisible && !_pointerDown && _hintTarget is { IsVisible: true } target &&
                HintButtonAt(_presentation.PointFromScreen(CursorPosition())) == target && target.ToolTip is ToolTip hint) {
                hint.PlacementTarget = _hintTarget; hint.IsOpen = true; _openHint = hint; _hintDismiss.Start();
            } else CloseHint();
        };
        _hintDismiss.Tick += (_, _) => CloseHint();
        _transition.Tick += (_, _) => {
            double progress = Math.Clamp(Stopwatch.GetElapsedTime(_transitionStarted).TotalMilliseconds / 220, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            ApplyFrame(new Rect(Lerp(_transitionFrom.X, _transitionTo.X, eased), Lerp(_transitionFrom.Y, _transitionTo.Y, eased),
                Lerp(_transitionFrom.Width, _transitionTo.Width, eased), Lerp(_transitionFrom.Height, _transitionTo.Height, eased)));
            if (progress >= 1) _transition.Stop();
        };
        _gestureWatch.Tick += (_, _) => {
            Point? point = GetCursorPos(out var cursor) ? new Point(cursor.X, cursor.Y) : null;
            PollPointer(point, (NativeMethods.GetAsyncKeyState(_physicalButton) & 0x8000) != 0);
        };
        Closed += (_, _) => { _closed = true; CloseHint(); _collapse.Stop(); _expand.Stop(); _transition.Stop(); CancelPointer(); _source?.RemoveHook(WindowMessage); };
        RebuildActions(); UpdatePrimary(); ApplyLayout();
    }

    public void Configure(bool visible, bool captions, string[] hoverActions, string[] contextActions, double? left, double? top,
        double opacity = .78, double scale = 1, int hoverDelay = 0, bool allowButtonRecording = true)
    {
        var hover = FilterActions(hoverActions); var context = FilterActions(contextActions);
        bool changed = _captionsEnabled != captions || _allowButtonRecording != allowButtonRecording || !_hoverActions.SequenceEqual(hover) || !_contextActions.SequenceEqual(context);
        bool scaleChanged = _barScale != scale;
        _barOpacity = double.IsFinite(opacity) ? Math.Clamp(opacity, .2, 1) : .78;
        _barScale = scale is .85 or 1 or 1.15 ? scale : 1; _hoverDelay = hoverDelay is 0 or 150 or 300 or 500 ? hoverDelay : 150;
        _allowButtonRecording = allowButtonRecording; _captionsEnabled = captions; _hoverActions = hover; _contextActions = context;
        if (!_pointerDown && left is { } x && top is { } y && double.IsFinite(x) && double.IsFinite(y)) {
            _anchor = ClampToWorkAreas(new Point(x, y), CompactSize, WorkAreas()); _positionSet = _userPosition = true;
        }
        else if (scaleChanged && !_userPosition) _positionSet = false;
        if (!_pointerDown) _anchor = ClampToWorkAreas(_anchor, CompactSize, WorkAreas());
        if (changed) RebuildActions();
        UpdatePrimary(); ApplyBackground(); ApplyLayout();
        if (visible) ShowBar(); else HideBar();
    }
    public void UpdateState(bool micReady, bool recording, bool busy, string? caption, string status)
    {
        bool changed = _micReady != micReady || _recording != recording || _busy != busy;
        _micReady = micReady; _recording = recording; _busy = busy;
        AutomationProperties.SetHelpText(_shell, status); _caption.Text = caption ?? "";
        _processingText.Text = status.StartsWith("Preparing", StringComparison.Ordinal) ? "Preparing" :
            status.StartsWith("Recognizing", StringComparison.Ordinal) ? "Recognizing" :
            status.StartsWith("Polishing", StringComparison.Ordinal) ? "Polishing" :
            status.StartsWith("Placing", StringComparison.Ordinal) ? "Placing your words" : "Finishing your words";
        if (changed) { UpdatePrimary(); RebuildActions(); }
        RefreshActivity(); ApplyLayout(true);
    }
    public void UpdateLevel(float level)
    {
        if (_rest is BarWaveform resting) resting.SetLevel(level);
        foreach (var wave in PrimaryWaves()) wave.SetLevel(level);
    }
    public void ShowBar() { if (_closed) return; EnsurePosition(); ApplyLayout(); if (!IsVisible) Show(); }
    public void HideBar()
    {
        CloseHint(); _collapse.Stop(); _expand.Stop(); _transition.Stop(); CancelPointer();
        _pointerInside = false; ResetContextActions(); _expanded = false; ApplyLayout(); if (IsVisible) Hide();
    }
    private static FrameworkElement CreateWave() => new BarWaveform { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Button CreateButton(string action, bool compact)
    {
        var button = new Button { Focusable = false, IsTabStop = false, Padding = new Thickness(1), Margin = new Thickness(1), MinHeight = 0, Height = compact ? 28 : 36, Cursor = Cursors.Hand };
        button.SetResourceReference(StyleProperty, "BarButton");
        if (!compact) button.Width = 84;
        button.MouseEnter += (_, _) => { AnimateGlyph(button, 1.08); QueueHint(button); };
        button.MouseLeave += (_, _) => { AnimateGlyph(button, 1); if (_hintTarget == button) CloseHint(); };
        SetButtonContent(button, action, compact); button.Click += (_, _) => ExecuteAction((string)button.Tag); return button;
    }
    private void AnimateGlyph(Button button, double scale)
    {
        if (_pointerDown || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || button.Content is not FrameworkElement content) return;
        if (content.RenderTransform is not ScaleTransform transform) { transform = new ScaleTransform(1, 1); content.RenderTransform = transform; content.RenderTransformOrigin = new Point(.5, .5); }
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(120)) { EasingFunction = easing });
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(120)) { EasingFunction = easing });
    }
    private void SetButtonContent(Button button, string action, bool compact)
    {
        var (_, label, tip) = Describe(action);
        button.Tag = action; button.IsEnabled = action == "HoldToTalk" && _holdActive || CanExecute(action);
        button.ToolTip = CreateHint(label, action); AutomationProperties.SetName(button, label); AutomationProperties.SetHelpText(button, tip);
        if (compact && button == _primary && !_busy && !_recording) button.Content = CreateWave();
        else if (compact && button == _primary && _recording) {
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new BarWaveform { Width = 22 });
            var finish = CreateIcon(action); finish.Width = finish.Height = 9; finish.Margin = new Thickness(3, 0, 0, 0); content.Children.Add(finish); button.Content = content;
        }
        else if (compact) button.Content = CreateIcon(action);
        else button.Content = new TextBlock { Text = label, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        // The bar never activates. Own hint lifetime instead of depending on the
        // foreground-window behavior of the platform ToolTipService.
        ToolTipService.SetIsEnabled(button, false);
    }
    private void QueueHint(Button? button)
    {
        if (button == _hintTarget) return;
        CloseHint();
        if (button == null || _pointerDown || !IsVisible) return;
        _hintTarget = button; _hintDelay.Start();
    }
    private void CloseHint()
    {
        _hintDelay.Stop(); _hintDismiss.Stop();
        if (_openHint != null) _openHint.IsOpen = false;
        _openHint = null; _hintTarget = null;
    }
    private ToolTip CreateHint(string label, string action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        string? key = action is "HoldToTalk" || action is "ToggleRecording" && !_allowButtonRecording && !_recording ? "Ctrl Alt" : action is "Cancel" ? "Esc" : null;
        if (key != null) {
            var keyText = new TextBlock { Text = key, FontSize = 11 }; keyText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(9, 0, 0, 0), Child = keyText };
            badge.SetResourceReference(Border.BackgroundProperty, "InsetBrush"); content.Children.Add(badge);
        }
        var hint = new ToolTip { Content = content, Style = (Style)FindResource("BarHint"), Placement = PlacementMode.Top, VerticalOffset = -7 };
        hint.Opened += (_, _) => {
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                hint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        };
        return hint;
    }
    private FrameworkElement CreateIcon(string action)
    {
        string geometry = action switch {
            "ToggleRecording" when _recording => "M8,6 L16,6 Q18,6 18,8 L18,16 Q18,18 16,18 L8,18 Q6,18 6,16 L6,8 Q6,6 8,6 Z",
            "ToggleRecording" or "HoldToTalk" => "M9,5 A3,3 0 0 1 15,5 L15,11 A3,3 0 0 1 9,11 Z M6,10 L6,11 A6,6 0 0 0 18,11 L18,10 M12,17 L12,21 M9,21 L15,21",
            "Cancel" => "M7,7 L17,17 M17,7 L7,17",
            "PasteLast" => "M8,5 L6,5 Q4,5 4,7 L4,20 Q4,21 5,21 L17,21 Q18,21 18,20 L18,7 Q18,5 16,5 L14,5 M8,3 L14,3 L14,7 L8,7 Z M8,12 L14,12 M8,16 L13,16",
            "ToggleMicrophone" => "M12,3 L12,11 M7,5 A8,8 0 1 0 17,5",
            "ToggleCaptions" => "M5,5 L19,5 Q21,5 21,7 L21,17 Q21,19 19,19 L5,19 Q3,19 3,17 L3,7 Q3,5 5,5 Z M7,10 L10,10 M7,14 L10,14 M14,10 L17,10 M14,14 L17,14",
            "OpenWorkspace" => "M13,4 L20,4 L20,11 M20,4 L11,13 M9,5 L5,5 Q3,5 3,7 L3,19 Q3,21 5,21 L17,21 Q19,21 19,19 L19,15",
            "HideBar" => "M6,12 L18,12",
            _ => "M5,12 L5.1,12 M12,12 L12.1,12 M19,12 L19.1,12"
        };
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextBrush");
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Width = 17, Height = 17, Child = canvas, Stretch = Stretch.Uniform };
    }
    private IEnumerable<BarWaveform> PrimaryWaves() => _primary.Content is BarWaveform wave ? [wave] : _primary.Content is StackPanel panel ? panel.Children.OfType<BarWaveform>() : [];
    private void RefreshActivity()
    {
        if (_pointerDown) { _primaryDirty = true; return; }
        if (_rest is BarWaveform rest) rest.SetActivity(BarActivity.Idle);
        foreach (var wave in PrimaryWaves()) wave.SetActivity(_recording ? BarActivity.Listening : BarActivity.Idle);
        _processingWave.SetActivity(_busy ? BarActivity.Finishing : BarActivity.Idle);
        _processing.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        _actions.Visibility = _busy ? Visibility.Hidden : Visibility.Visible;
    }
    private (string Symbol, string Label, string Tip) Describe(string action) => action switch
    {
        "ToggleRecording" => (_recording ? "\uE71A" : "\uE720", _recording ? "Finish dictation" : "Start dictation", _allowButtonRecording || _recording ? "Ctrl + Alt; add Space for hands-free" : "Shortcut-only · enable button recording in Appearance to start here"),
        "HoldToTalk" => ("\uE720", "Hold to talk", _allowButtonRecording ? "Hold while speaking; release to finish · Ctrl + Alt" : "Shortcut-only · hold Ctrl + Alt, or enable button recording in Appearance"),
        "Cancel" => ("\uE711", "Cancel", "Esc cancels pending dictation; inserted text remains"),
        "PasteLast" => ("\uE77F", "Paste last", "Choose your paste shortcut in Settings"),
        "ToggleMicrophone" => (_micReady ? "\uE8BB" : "\uE768", _micReady ? "Unload speech" : "Prepare speech", "Load or unload local speech; this doesn't open the microphone"),
        "ToggleCaptions" => ("\uE8F2", _captionsEnabled ? "Hide captions" : "Show captions", "Show or hide live text"),
        "OpenWorkspace" => ("\uE80F", "Open Typr", "Open the Typr workspace"),
        "HideBar" => ("\uE76C", "Hide bar", "Show it again in Typr Appearance"),
        _ => ("\uE712", "Bar actions", "Show your right-click actions; drag any control to move")
    };
    private bool CanExecute(string action) => action switch {
        "HoldToTalk" => _allowButtonRecording && _micReady && !_recording && !_busy,
        "ToggleRecording" => _recording || _allowButtonRecording && _micReady && !_busy,
        "Cancel" => _recording || _busy, "PasteLast" => !_recording && !_busy, _ => true
    };
    internal static string[] FilterActions(IEnumerable<string>? actions) => (actions ?? [])
        .Where(action => action is "ToggleRecording" or "HoldToTalk" or "Cancel" or "PasteLast" or "ToggleMicrophone" or "ToggleCaptions" or "OpenWorkspace" or "HideBar")
        .Distinct(StringComparer.Ordinal).Take(8).ToArray();
    private void RebuildActions()
    {
        if (_pointerDown) { _actionsDirty = true; return; }
        CloseHint();
        _actionsDirty = false; _actions.Children.Clear(); _contextPanel.Children.Clear();
        foreach (string action in _hoverActions) _actions.Children.Add(CreateButton(action, true));
        _main.Width = ExpandedWidth - 20; _actions.Width = _main.Width - 66;
        if (_contextOpen) foreach (string action in _contextActions) _contextPanel.Children.Add(CreateButton(action, false));
    }
    private void UpdatePrimary()
    {
        if (_pointerDown) { _primaryDirty = true; return; }
        _primaryDirty = false; SetButtonContent(_primary, _busy ? "Cancel" : _micReady || _recording ? "ToggleRecording" : "ToggleMicrophone", true); RefreshActivity();
    }
    private void RefreshDeferredControls() { if (_actionsDirty) RebuildActions(); if (_primaryDirty) UpdatePrimary(); }
    internal void SetPointerInside(bool inside)
    {
        _pointerInside = inside; if (!inside) CloseHint();
        if (inside) { _collapse.Stop(); StartHoverExpansion(); } else { _expand.Stop(); if (!_pointerDown) _collapse.Start(); }
    }
    private void StartHoverExpansion()
    {
        _expand.Stop(); if (_expanded || _pointerDown) return;
        if (_hoverDelay == 0) SetExpanded(true); else { _expand.Interval = TimeSpan.FromMilliseconds(_hoverDelay); _expand.Start(); }
    }
    private void ToggleMenu()
    {
        _collapse.Stop(); _expand.Stop(); _contextOpen = !_contextOpen; RebuildActions(); SetExpanded(_contextOpen || _pointerInside);
    }
    private void ResetContextActions() { if (!_contextOpen) return; _contextOpen = false; RebuildActions(); }
    private void SetExpanded(bool expanded) { _expanded = expanded; ApplyLayout(true); }
    private void EnsurePosition()
    {
        if (_positionSet) return;
        var work = SystemParameters.WorkArea;
        _anchor = new Point(work.Left + (work.Width - CompactSize.Width) / 2, work.Bottom - CompactSize.Height - 18); _positionSet = true;
    }
    private void ApplyLayout(bool animate = false)
    {
        if (_pointerDown) return; // Freeze controls and growth geometry throughout a gesture.
        EnsurePosition();
        bool wide = _expanded || _recording || _busy || _captionsEnabled && !string.IsNullOrWhiteSpace(_caption.Text);
        double trayHeight = _contextOpen && _contextActions.Length > 0 ? 28 + Math.Ceiling(_contextActions.Length / 3d) * 40 : 0;
        double captionHeight = _captionsEnabled && !string.IsNullOrWhiteSpace(_caption.Text) ? 54 : 0;
        double width = (wide ? ExpandedWidth : RestingWidth) * _barScale;
        double height = ((wide ? ExpandedHeight : RestingHeight) + trayHeight + captionHeight) * _barScale;
        var area = NearestWorkArea(_anchor, CompactSize, WorkAreas());
        if (!wide) _dragLayoutPinned = false;
        if (!_dragLayoutPinned) _layoutUpwards = _anchor.Y + CompactSize.Height - height >= area.Top;
        bool upwards = _layoutUpwards;
        var target = new Rect(Math.Clamp(_anchor.X + CompactSize.Width / 2 - width / 2, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(upwards ? _anchor.Y + CompactSize.Height - height : _anchor.Y, area.Top, Math.Max(area.Top, area.Bottom - height)), width, height);
        _layout.RowDefinitions[0].Height = new GridLength(upwards ? trayHeight : wide ? 30 : 8);
        _layout.RowDefinitions[1].Height = new GridLength(captionHeight);
        _layout.RowDefinitions[2].Height = new GridLength(upwards ? wide ? 30 : 8 : trayHeight);
        Grid.SetRow(_main, upwards ? 2 : 0); Grid.SetRow(_rest, upwards ? 2 : 0); Grid.SetRow(_tray, upwards ? 0 : 2); Grid.SetRow(_caption, 1);
        bool reveal = wide && _main.Visibility != Visibility.Visible;
        _main.Visibility = wide ? Visibility.Visible : Visibility.Collapsed; _rest.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        _tray.Visibility = trayHeight > 0 ? Visibility.Visible : Visibility.Collapsed; _caption.Visibility = captionHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
        _presentation.Width = width / _barScale; _presentation.Height = height / _barScale;
        _shell.CornerRadius = new CornerRadius(wide ? 20 : 9); ApplyBackground();
        // Caption/level/status refreshes must not cut an in-flight hover transition short.
        if (_transition.IsEnabled && target == _transitionTo) return;
        _transition.Stop();
        if (Math.Abs(Left - target.Left) < .01 && Math.Abs(Top - target.Top) < .01 &&
            Math.Abs(Width - target.Width) < .01 && Math.Abs(Height - target.Height) < .01) return;
        if (animate && IsVisible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast) {
            _transitionFrom = new Rect(Left, Top, Width, Height); _transitionTo = target; _transitionStarted = Stopwatch.GetTimestamp(); _transition.Start();
            if (reveal) {
                _main.Opacity = 0;
                _main.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(50) });
            }
        } else { _main.BeginAnimation(OpacityProperty, null); _main.Opacity = 1; ApplyFrame(target); }
    }
    private void ApplyFrame(Rect bounds) { Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top; }
    private static double Lerp(double start, double end, double amount) => start + (end - start) * amount;
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
        BeginPointer(action, CursorPosition()); Mouse.Capture(this, CaptureMode.SubTree);
        // Background NOACTIVATE windows cannot rely on foreground-only capture.
        // This watchdog is active only during a real pointer gesture, never idle.
        _physicalButton = SystemParameters.SwapButtons ? 0x02 : 0x01;
        if (_pointerDown) _gestureWatch.Start();
        e.Handled = true;
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown) {
            // Visual hit testing also reaches shortcut-only disabled buttons, so
            // their recording shortcut remains discoverable without enabling them.
            QueueHint(HintButtonAt(e.GetPosition(_presentation))); return;
        }
        MovePointer(CursorPosition()); e.Handled = true;
    }
    private Button? HintButtonAt(Point point)
    {
        var hit = VisualTreeHelper.HitTest(_presentation, point)?.VisualHit;
        for (var node = hit; node != null && node != this; node = VisualTreeHelper.GetParent(node))
            if (node is Button button) return button;
        return null;
    }
    private void PointerUp(object sender, MouseButtonEventArgs e) { if (!_pointerDown) return; EndPointer(CursorPosition()); ReleasePointerCapture(); e.Handled = true; }
    internal void BeginPointer(string? action, Point screenPoint)
    {
        if (_pointerDown) CancelPointer();
        CloseHint();
        _collapse.Stop(); _expand.Stop(); _transition.Stop(); EnsurePosition();
        _pointerDown = true; _dragging = false; _pressedAction = action;
        _dragLayoutPinned = _main.Visibility == Visibility.Visible;
        _pointerStart = screenPoint; _dragOrigin = _anchor; _dragDpi = VisualTreeHelper.GetDpi(this); _gestureBounds = new Rect(Left, Top, Width, Height);
        if (action == "HoldToTalk" && CanExecute(action)) { _holdActive = true; ActionRequested?.Invoke("HoldStart"); }
    }
    internal void MovePointer(Point screenPoint)
    {
        if (!_pointerDown) return;
        var delta = new Vector((screenPoint.X - _pointerStart.X) / _dragDpi.DpiScaleX, (screenPoint.Y - _pointerStart.Y) / _dragDpi.DpiScaleY);
        if (!_dragging && (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance)) {
            // A clamped expansion can be offset from its saved resting anchor.
            // Once this is a drag, derive that anchor from the visible frame so
            // release and later collapse use the same center and growth edge.
            _dragOrigin = new Point(_gestureBounds.Left + (_gestureBounds.Width - CompactSize.Width) / 2,
                _layoutUpwards ? _gestureBounds.Bottom - CompactSize.Height : _gestureBounds.Top);
            _dragging = true; _userPosition = true; if (_holdActive) { _holdActive = false; ActionRequested?.Invoke("Cancel"); }
        }
        if (_dragging) {
            var position = ClampToWorkAreas(_gestureBounds.TopLeft + delta, _gestureBounds.Size, WorkAreas());
            _anchor = _dragOrigin + (position - _gestureBounds.TopLeft);
            Left = position.X; Top = position.Y;
        }
    }
    internal void EndPointer(Point screenPoint)
    {
        if (!_pointerDown) return; MovePointer(screenPoint);
        bool dragged = _dragging, held = _holdActive; string? action = _pressedAction;
        _gestureWatch.Stop(); _pointerDown = _dragging = _holdActive = false; _pressedAction = null;
        RefreshDeferredControls(); ApplyLayout();
        if (dragged) PositionChanged?.Invoke(_anchor); else if (held) ActionRequested?.Invoke("HoldFinish"); else if (action != "HoldToTalk" && action != null) ExecuteAction(action);
        if (!_pointerInside) _collapse.Start(); else StartHoverExpansion();
    }
    // A missed release must never synthesize a click, paste, or hold completion.
    internal void PollPointer(Point? point, bool pressed)
    {
        if (!_pointerDown) { _gestureWatch.Stop(); return; }
        if (point == null) { CancelPointer(); return; }
        MovePointer(point.Value);
        if (!pressed) { if (_dragging) EndPointer(point.Value); else CancelPointer(); ReleasePointerCapture(); }
    }
    private void CancelPointer()
    {
        bool held = _holdActive;
        _gestureWatch.Stop(); _pointerDown = _dragging = _holdActive = false; _pressedAction = null;
        RefreshDeferredControls(); ReleasePointerCapture(); ApplyLayout(); if (held) ActionRequested?.Invoke("Cancel");
    }
    private void ReleasePointerCapture() { if (Mouse.Captured == this) Mouse.Capture(null); }
    private void ExecuteAction(string action)
    {
        if (!CanExecute(action)) return;
        if (action == "ShowMenu") { ToggleMenu(); return; }
        if (action == "HoldToTalk") return;
        if (action == "HideBar") HideBar(); ActionRequested?.Invoke(action);
    }
    internal Point CompactPosition => _anchor;
    internal bool IsExpanded => _expanded;
    internal Size CompactSize => new(RestingWidth * _barScale, RestingHeight * _barScale);
    internal bool IsGestureActive => _pointerDown;
    internal bool IsTransitioning => _transition.IsEnabled;
    internal static Point ClampToWorkAreas(Point position, Size size, IReadOnlyList<Rect> areas)
    {
        var work = NearestWorkArea(position, size, areas);
        return new Point(Math.Clamp(position.X, work.Left, Math.Max(work.Left, work.Right - size.Width)), Math.Clamp(position.Y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));
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
        var result = new List<Rect>(); var dpi = VisualTreeHelper.GetDpi(this);
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref NativeMethods.Rect bounds, nint data) => {
            var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info)) result.Add(new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
                (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY));
            return true;
        }, 0); return result;
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; }
        if (message == 0x84 && IsVisible) {
            var point = _shell.PointFromScreen(new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF)));
            if (!new Rect(_shell.RenderSize).Contains(point)) { handled = true; return -1; }
        } return 0;
    }
    private static Point CursorPosition() { GetCursorPos(out var point); return new Point(point.X, point.Y); }
    private delegate bool MonitorCallback(nint monitor, nint device, ref NativeMethods.Rect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint device, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativeMethods.Point point);
}
