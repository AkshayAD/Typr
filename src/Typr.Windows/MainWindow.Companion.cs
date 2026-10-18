using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Typr.Core;
using Typr.Infrastructure;

namespace Typr.Windows;

public partial class MainWindow
{
    private readonly DictationBarWindow _dictationBar = new();
    private readonly ExternalTargetTracker _externalTargets = new();
    private readonly CommandShortcuts _commandShortcuts = new();
    private readonly DispatcherTimer _targetTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, (CheckBox Hover, CheckBox Context)> _barOptions = [];
    private bool _desktopIntegration, _companionInitialized, _companionInitializing, _barHoldActive, _restoreDeliveryFocus;
    private int _barHoldGeneration;
    private string _liveCaption = "";
    private string _processingPhase = "Finishing your words…";

    private void InitializeCompanion(bool desktopIntegration)
    {
        _desktopIntegration = desktopIntegration; _companionInitializing = true;
        AutoPasteBox.IsChecked = _settings.AutoPasteEnabled;
        AiPolishBox.IsChecked = _settings.AiPolishEnabled;
        CommandShortcutsBox.IsChecked = _settings.CommandShortcutsEnabled;
        BarOpacitySlider.Value = _settings.BarOpacity;
        BarScaleBox.SelectedIndex = Array.IndexOf(new[] { .85, 1, 1.15 }, _settings.BarScale);
        BarHoverDelayBox.SelectedIndex = Array.IndexOf(new[] { 0, 150, 300, 500 }, _settings.BarHoverDelay);
        AllowButtonRecordingBox.IsChecked = _settings.AllowButtonRecording;
        MicrophoneShortcutBox.ItemsSource = new[] { "Ctrl+Shift+M", "Ctrl+Shift+F8", "F9" };
        HandsFreeShortcutBox.ItemsSource = new[] { "Ctrl+Shift+Space", "Ctrl+Shift+R", "F8" };
        PasteShortcutBox.ItemsSource = new[] { "Ctrl+Shift+V", "Ctrl+Shift+F9", "F10" };
        MicrophoneShortcutBox.SelectedItem = _settings.MicrophoneShortcut;
        HandsFreeShortcutBox.SelectedItem = _settings.HandsFreeShortcut;
        PasteShortcutBox.SelectedItem = _settings.PasteShortcut;
        ProviderPriorityBox.SelectedIndex = Array.IndexOf(new[] { "ChatGPT only", "Claude only", "ChatGPT first", "Claude first" }, _settings.ProviderPriority);
        ProviderModelBox.Text = PrimaryProvider == PolishProvider.ChatGpt ? _settings.ChatGptModel : _settings.ClaudeModel;
        ReasoningBox.ItemsSource = new[] { "none", "low", "medium", "high", "xhigh", "max" };
        ReasoningBox.SelectedItem = _settings.PolishReasoning;
        RetranscriptionModelBox.ItemsSource = WhisperRetranscriber.Models;
        RetranscriptionModelBox.SelectedValue = _settings.RetranscriptionModel;
        foreach (var (action, label) in new[] { ("ToggleRecording", "Start / finish"), ("HoldToTalk", "Hold to talk"), ("Cancel", "Cancel dictation"), ("PasteLast", "Paste last transcript"), ("ToggleMicrophone", "Prepare / unload speech"), ("ToggleCaptions", "Show / hide live text"), ("OpenWorkspace", "Open workspace"), ("HideBar", "Hide bar") })
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var hover = new CheckBox { Content = "", IsChecked = _settings.BarHoverActions.Contains(action), Margin = new Thickness(20, 0, 24, 0) };
            var context = new CheckBox { Content = "", IsChecked = _settings.BarContextActions.Contains(action), Margin = new Thickness(20, 0, 24, 0) };
            System.Windows.Automation.AutomationProperties.SetName(hover, label + " on hover");
            System.Windows.Automation.AutomationProperties.SetName(context, label + " on right-click");
            Grid.SetColumn(hover, 1); Grid.SetColumn(context, 2);
            row.Children.Add(title); row.Children.Add(hover); row.Children.Add(context); BarOptionsPanel.Children.Add(row);
            _barOptions[action] = (hover, context);
            hover.Click += BarOptions_Click; context.Click += BarOptions_Click;
        }
        _dictationBar.ActionRequested += async action => await CompanionActionAsync(action, fromBar: true);
        _dictationBar.PositionChanged += point => { if (!_closing) PersistSettings(_settings with { BarLeft = point.X, BarTop = point.Y, BarDockEdge = _dictationBar.DockEdge.ToString() }); };
        _commandShortcuts.Requested += async action => await CompanionActionAsync(action);
        _targetTimer.Tick += async (_, _) =>
        {
            try { await _externalTargets.RefreshAsync(_lifetime.Token); }
            catch (OperationCanceledException) { }
        };
        _companionInitializing = false; _companionInitialized = true;
        ConfigureCompanion(); RefreshHistorySummary();
        bool startupSpeechChecked = false;
        if (desktopIntegration) Loaded += async (_, _) =>
        {
            ShortcutConfigurationStatus.Text = _commandShortcuts.Configure(this, EffectiveShortcutSettings);
            ConfigureDictationHook();
            if (!SpeechPausedForGaming) _targetTimer.Start(); ConfigureCompanion();
            if (!startupSpeechChecked)
            {
                startupSpeechChecked = true;
                if (AnySpeechModelReady(ModelPathBox.Text.Trim()))
                {
                    if (SpeechPausedForGaming) _resumeSpeechAfterGaming = true;
                    else await LoadSpeechAsync(focusPractice: false);
                }
            }
            if (!SpeechPausedForGaming) await RefreshProvidersAsync();
        };
    }

    private bool DictationBarActive => _recordingStart != null || _recording || _inputActive || HasPendingDictationOutput;
    private bool ShouldShowDictationBar => DictationBarActive || _settings.BarVisible && !SpeechPausedForGaming;

    private void ConfigureCaptionOverlay() => _overlay.Configure(_settings with
    {
        BarVisible = false,
        LiveCaptionsEnabled = _settings.LiveCaptionsEnabled && !(_desktopIntegration && ShouldShowDictationBar)
    });
    private void ConfigureCompanion()
    {
        if (!_companionInitialized) return;
        if (_desktopIntegration) _dictationBar.Configure(ShouldShowDictationBar, _settings.LiveCaptionsEnabled,
            _settings.BarHoverActions, _settings.BarContextActions, _settings.BarLeft, _settings.BarTop,
            _settings.BarOpacity, _settings.BarScale, _settings.BarHoverDelay, _settings.AllowButtonRecording, _settings.BarDockEdge);
        RefreshCompanionState();
    }
    private void RefreshCompanionState()
    {
        if (!_companionInitialized) return;
        ConfigureCaptionOverlay();
        if (!_desktopIntegration) return;
        bool finishing = HasPendingDictationOutput && !_recording;
        _dictationBar.UpdateState(_speech != null && !_speechFaulted && !_loading && !SpeechPausedForGaming && !_resourceTransitioning, _recording, _busy || finishing || _loading || _recordingStart != null,
            _settings.LiveCaptionsEnabled && (_recording || _busy || finishing) ? _liveCaption : null,
            _loading || _recordingStart != null ? "Preparing speech…" : _recording ? (_readyInputGeneration == _speechGeneration ? "Listening · Esc cancels" : "Opening microphone…") : _busy || finishing ? _processingPhase : _inputActive ? "Closing microphone…" : _speechFaulted && _speech != null ? "Speech needs attention" : _speech != null ? "Ready · microphone off" : "Microphone off",
            inputActive: _readyInputGeneration == _speechGeneration,
            canStartWhileBusy: HasPendingDictationOutput && !_busy && !_loading && !SpeechPausedForGaming && !_resourceTransitioning && _recordingStart == null);
    }
    private void UpdateLiveBarCaption(string text) { _liveCaption = text; RefreshCompanionState(); }
    private async Task CloseCompanionAsync()
    {
        _targetTimer.Stop(); _externalTargets.Clear(); _commandShortcuts.Dispose(); _dictationBar.Close();
        _providerLogin?.Cancel(); _providerComparison?.Cancel(); _historyOperation?.Cancel(); _historyPlayer?.Stop(); _historyPlayer?.Dispose();
        if (_ai != null) await _ai.DisposeAsync();
    }
    private void BarOptions_Click(object sender, RoutedEventArgs e)
    {
        PersistSettings(_settings with
        {
            BarHoverActions = _barOptions.Where(pair => pair.Value.Hover.IsChecked == true).Select(pair => pair.Key).ToArray(),
            BarContextActions = _barOptions.Where(pair => pair.Value.Context.IsChecked == true).Select(pair => pair.Key).ToArray()
        });
        ConfigureCompanion();
    }
    private void CompanionPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (_companionInitializing || !_companionInitialized) return;
        PersistSettings(_settings with { AutoPasteEnabled = AutoPasteBox.IsChecked == true, AiPolishEnabled = AiPolishBox.IsChecked == true, CommandShortcutsEnabled = CommandShortcutsBox.IsChecked == true });
        if (_desktopIntegration && IsLoaded) { ShortcutConfigurationStatus.Text = _commandShortcuts.Configure(this, EffectiveShortcutSettings); ConfigureDictationHook(); }
        ConfigureCompanion();
    }
    private void Shortcut_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_companionInitializing || !_companionInitialized) return;
        PersistSettings(_settings with { MicrophoneShortcut = (string)MicrophoneShortcutBox.SelectedItem, HandsFreeShortcut = (string)HandsFreeShortcutBox.SelectedItem, PasteShortcut = (string)PasteShortcutBox.SelectedItem });
        if (_desktopIntegration && IsLoaded) ShortcutConfigurationStatus.Text = _commandShortcuts.Configure(this, EffectiveShortcutSettings);
    }
    private void ResetBarPosition_Click(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        var position = DictationBarWindow.EdgePosition(work, new Size(DictationBarWindow.RestingWidth * _settings.BarScale, DictationBarWindow.RestingHeight * _settings.BarScale),
            new Point(work.Left + work.Width / 2, work.Bottom), BarDockEdge.Bottom);
        PersistSettings(_settings with { BarLeft = position.X, BarTop = position.Y, BarDockEdge = "Bottom" }); ConfigureCompanion();
    }

    private void BarPresentation_Changed(object sender, RoutedEventArgs e)
    {
        if (_companionInitializing || !_companionInitialized) return;
        PersistSettings(_settings with {
            BarOpacity = BarOpacitySlider.Value,
            BarScale = BarScaleBox.SelectedIndex switch { 0 => .85, 2 => 1.15, _ => 1 },
            BarHoverDelay = BarHoverDelayBox.SelectedIndex switch { 0 => 0, 2 => 300, 3 => 500, _ => 150 }
        });
        ConfigureCompanion();
    }

    private void BarButtonRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_companionInitializing || !_companionInitialized) return;
        PersistSettings(_settings with { AllowButtonRecording = AllowButtonRecordingBox.IsChecked == true });
        ConfigureCompanion();
        RefreshReadiness();
    }

    private void ResetBarAppearance_Click(object sender, RoutedEventArgs e)
    {
        _companionInitializing = true;
        BarOpacitySlider.Value = .78; BarScaleBox.SelectedIndex = 1; BarHoverDelayBox.SelectedIndex = 1;
        _companionInitializing = false;
        PersistSettings(_settings with { BarOpacity = .78, BarScale = 1, BarHoverDelay = 150 });
        ConfigureCompanion();
    }

    private async Task CompanionActionAsync(string action, bool fromBar = false)
    {
        if (_closing) return;
        if (action == "HoldFinish")
        {
            bool held = _barHoldActive; _barHoldActive = false; ++_barHoldGeneration;
            if (held) { CancelPendingRecordingStart(); if (_recording) await FinishCaptureAsync(); RefreshReadiness(); }
            return;
        }
        if (action == "Cancel") { CancelCapture(); return; }
        if (action == "HideBar") { PersistSettings(_settings with { BarVisible = false }); BarVisibleBox.IsChecked = false; ConfigureCompanion(); return; }
        if (action == "ToggleCaptions") { PersistSettings(_settings with { LiveCaptionsEnabled = !_settings.LiveCaptionsEnabled }); CaptionsBox.IsChecked = _settings.LiveCaptionsEnabled; ConfigureCompanion(); return; }
        if (action == "OpenWorkspace") { Show(); WindowState = WindowState.Normal; Activate(); Navigate("Workspace"); return; }
        if (action == "ToggleRecording" && _recordingStart != null) { CancelPendingRecordingStart(); RefreshReadiness(); return; }
        if (action == "ToggleRecording" && _recording) { await FinishCaptureAsync(); return; }
        if (fromBar && !_settings.AllowButtonRecording && action is "HoldStart" or "ToggleRecording")
        { StatusText.Text = "Use your recording shortcut. Button recording can be enabled in Settings."; return; }
        try
        {
            if (action == "ToggleMicrophone")
            {
                if (_speech != null) Pause_Click(this, new RoutedEventArgs()); else await LoadSpeechAsync(focusPractice: false);
                return;
            }
            if (action == "PasteLast")
            {
                if (IsActive)
                {
                    if (PracticeBox.IsKeyboardFocusWithin && _latest.Length > 0 && !_recording && !_busy) PracticeBox.SelectedText = _latest;
                    else StatusText.Text = "Click a writable field before choosing Paste last.";
                    return;
                }
                // A keyboard command targets the field focused now. The tracker
                // can return a cached element while a periodic refresh is pending.
                if (!fromBar) { await PasteLastAsync(NativeMethods.GetForegroundWindow()); return; }
                var target = await _externalTargets.RefreshAsync(_lifetime.Token);
                if (target == null) { StatusText.Text = "Click a writable field in another app, then choose Paste last."; return; }
                await PasteLastAsync(target.Window, target, restoreFocus: true); return;
            }
            if (action is "ToggleRecording" or "HoldStart")
            {
                if (_busy || _recordingStart != null || _historyOperation != null) return;
                bool practice = IsActive && PracticeBox.IsKeyboardFocusWithin;
                if (IsActive && !practice) { StatusText.Text = "Click the practice field or a writable field in another app before dictating."; return; }
                if (action == "HoldStart") { _barHoldActive = true; ++_barHoldGeneration; }
                await StartCaptureAsync(DictationMode.Polish, NativeMethods.GetForegroundWindow(), practice, restoreFocus: !practice);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "Couldn't complete that action: " + ex.Message; }
    }
}
