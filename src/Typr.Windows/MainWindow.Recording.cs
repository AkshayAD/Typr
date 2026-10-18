using System.Windows;
using System.Windows.Controls;
using Typr.Core;
using Typr.Infrastructure;

namespace Typr.Windows;

public partial class MainWindow
{
    private Task? _speechLoadingTask;
    private CancellationTokenSource? _recordingStart;
    private DictationMode _startingMode;
    private bool _hookRecordingEnabled;
    private bool _initializingInput;

    // A keyboard listener needs no microphone or speech model. Keep it available
    // while speech is unloaded so the first deliberate shortcut can prepare it.
    private void ConfigureDictationHook()
    {
        if (!_desktopIntegration || !IsLoaded || _closing) return;
        if (_hook != null && _hookRecordingEnabled == _settings.CommandShortcutsEnabled) return;
        if (!_settings.CommandShortcutsEnabled && (_recordingStart != null || _recording || _busy)) CancelCapture();
        _hook?.Dispose(); _hook = null;
        try
        {
            _hookRecordingEnabled = _settings.CommandShortcutsEnabled;
            _hook = new GlobalKeyboardHook(_hookRecordingEnabled);
            _hook.Updated += (update, foreground) => Dispatcher.BeginInvoke(() => HandleHotkey(update, foreground));
            _hook.EscapePressed += () => Dispatcher.BeginInvoke(() =>
            {
                if (_recordingStart != null || _recording || _busy) CancelCapture();
            });
            RefreshReadiness();
        }
        catch (Exception ex) { ShortcutConfigurationStatus.Text += " Hold shortcut unavailable: " + ex.Message; }
    }

    private void CancelPendingRecordingStart()
    {
        var pending = _recordingStart; _recordingStart = null;
        pending?.Cancel(); pending?.Dispose();
    }

    private async Task StartCaptureAsync(DictationMode mode, nint foreground, bool practice, bool restoreFocus = false)
    {
        if (_closing || _recording || _busy || _recordingStart != null || _historyOperation != null) return;
        if (mode == DictationMode.VoiceEdit) { StatusText.Text = "Voice editing isn't available. Nothing was recorded."; return; }
        if (_speechFaulted && _speech != null) { StatusText.Text = "Unload speech in Settings, check the selected input, then try again."; return; }
        var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = pending.Token;
        _recordingStart = pending; _startingMode = mode;
        RefreshReadiness();
        try
        {
            // Snapshot the destination at the shortcut, before model preparation
            // or another window can move the user's text cursor.
            var target = practice ? null : ClipboardDelivery.CaptureTargetAsync(foreground, token);
            await LoadSpeechAsync(focusPractice: false);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_recordingStart, pending) || _closing || _speech == null || _speechFaulted) return;
            _recordingStart = null;
            // UIA may take hundreds of milliseconds. Preserve the original
            // capture task for delivery without delaying a prepared microphone.
            BeginCapture(_startingMode, foreground, practice, target, restoreFocus);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) StatusText.Text = "Couldn't start dictation: " + ex.Message; }
        finally
        {
            if (ReferenceEquals(_recordingStart, pending)) _recordingStart = null;
            pending.Dispose();
            if (!_closing) RefreshReadiness();
        }
    }

    private void InitializeInputChoice()
    {
        _initializingInput = true;
        var choices = new List<MicrophoneInput> { new("", "Use system default input") };
        if (_settings.InputDeviceId is { } selected) choices.Add(new(selected, "Saved input (refresh to inspect)"));
        InputDeviceBox.ItemsSource = choices;
        InputDeviceBox.SelectedValue = _settings.InputDeviceId ?? "";
        _initializingInput = false;
    }

    private void RefreshInputs_Click(object sender, RoutedEventArgs e)
    {
        // Device enumeration happens only when requested. This never starts a stream.
        try
        {
            var inputs = SpeechSession.EnumerateMicrophones();
            _initializingInput = true;
            var choices = new List<MicrophoneInput> { new("", "Use system default input") };
            choices.AddRange(inputs);
            if (_settings.InputDeviceId is { } selected && inputs.All(input => input.Id != selected))
                choices.Add(new(selected, "Saved input (currently unavailable)"));
            InputDeviceBox.ItemsSource = choices;
            InputDeviceBox.SelectedValue = _settings.InputDeviceId ?? "";
            InputDeviceStatus.Text = "Inputs refreshed. No microphone was opened.";
        }
        catch (Exception ex) { InputDeviceStatus.Text = "Couldn't list inputs: " + ex.Message; }
        finally { _initializingInput = false; }
    }

    private void InputDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingInput || !_initialized || InputDeviceBox.SelectedValue is not string selected) return;
        PersistSettings(_settings with { InputDeviceId = string.IsNullOrWhiteSpace(selected) ? null : selected });
        InputDeviceStatus.Text = _speech != null
            ? "Saved. Unload and prepare speech to use this input. Windows defaults stay unchanged."
            : "Saved for your next dictation. Windows defaults stay unchanged.";
    }
}
