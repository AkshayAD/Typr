using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ava.Core;
using Ava.Infrastructure;

namespace Ava.Windows;

public partial class MainWindow : Window
{
    private SpeechSession? _speech;
    private GlobalKeyboardHook? _hook;
    private readonly CaptionOverlay _overlay = new();
    private readonly ClipboardDelivery _delivery = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _download;
    private Task<DeliveryTarget>? _target;
    private Task<DeliveryResult>? _pendingDelivery;
    private bool _recording, _busy, _loading, _closing;
    private bool _allowClose;
    private DictationMode _mode;
    private string _latest = "";
    private AppSettings _settings = new();
    private bool _settingsWritable = true;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ava", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        try { if (File.Exists(_settingsPath)) _settings = SettingsMigrator.Load(File.ReadAllText(_settingsPath)); }
        catch (Exception ex)
        {
            _settingsWritable = false;
            StatusText.Text = "Existing settings could not be read and will be preserved: " + ex.Message;
        }
        ModelPathBox.Text = _settings.SpeechModelPath ?? ModelDownload.DefaultDirectory;
        ThreadCountBox.SelectedIndex = _settings.CpuThreads == 4 ? 1 : 0;
        ModelStatus.Text = ModelDownload.IsReady(ModelPathBox.Text) ? "Speech model found. Load it to enable the microphone and hotkeys." : "Speech model is not downloaded.";
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true; _lifetime.Cancel(); _operation?.Cancel(); _download?.Cancel();
            _hook?.Dispose(); _hook = null;
            // Let an already-confirmed paste restore the previous clipboard before exit.
            if (_pendingDelivery != null) { try { await _pendingDelivery; } catch { } }
            var speech = _speech; _speech = null;
            if (speech != null) { try { await Task.Run(speech.Dispose); } catch { } }
            _overlay.Close();
            _allowClose = true;
            Close();
        };
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_download != null) { _download.Cancel(); return; }
        if (_loading || _speech != null) return;
        _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        DownloadButton.Content = "Cancel download"; LoadButton.IsEnabled = false; ModelPathBox.IsEnabled = false;
        ModelStatus.Text = "Downloading the pinned speech model. Partial downloads can resume.";
        var progress = new Progress<double>(value => { DownloadProgress.Value = value; ModelStatus.Text = $"Downloading speech model: {value:P0}"; });
        try
        {
            await ModelDownload.DownloadAsync(ModelPathBox.Text, progress, _download.Token);
            ModelStatus.Text = "Speech model downloaded and verified. Load it when you're ready.";
        }
        catch (OperationCanceledException) { ModelStatus.Text = "Download paused. Choose Download to resume."; }
        catch (Exception ex) { ModelStatus.Text = "Download failed: " + ex.Message; }
        finally
        {
            _download.Dispose(); _download = null;
            DownloadButton.Content = "Download speech model"; LoadButton.IsEnabled = true; ModelPathBox.IsEnabled = true;
        }
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _speech != null || _download != null) return;
        _loading = true; SetSetupEnabled(false);
        var directory = ModelPathBox.Text.Trim();
        var threads = ThreadCountBox.SelectedIndex == 1 ? 4 : 2;
        ModelStatus.Text = "Loading local speech model…";
        SpeechSession? loaded = null;
        try
        {
            loaded = await Task.Run(() => new SpeechSession(directory, threads), _lifetime.Token);
            if (_closing) { loaded.Dispose(); return; }
            _speech = loaded;
            loaded.Caption += text => Dispatcher.BeginInvoke(() =>
            {
                if (!_closing && (_recording || _busy)) _overlay.SetLines(CaptionWrapper.Wrap(text).Select(line => line.Text));
            });
            loaded.Level += level => Dispatcher.BeginInvoke(() =>
            {
                if (_closing) return;
                MicLevel.Value = level;
                if (_recording) _overlay.Level(level);
            });
            loaded.Error += message => Dispatcher.BeginInvoke(() =>
            {
                if (_closing) return;
                CancelCapture(); StatusText.Text = "Microphone or speech error: " + message;
                _overlay.Error(message);
            });
            loaded.StartMicrophone();
            _hook = new GlobalKeyboardHook();
            _hook.Updated += (update, foreground) => Dispatcher.BeginInvoke(() => HandleHotkey(update, foreground));
            ModelStatus.Text = $"Local speech ready · {threads} CPU threads · microphone open for pre-roll.";
            StatusText.Text = "Click a text field and hold Ctrl + Alt. Close mic to release the microphone and global shortcuts.";
            PauseButton.IsEnabled = true;
            SaveSettings(directory, threads);
            PracticeBox.Focus();
        }
        catch (OperationCanceledException) { loaded?.Dispose(); _speech = null; }
        catch (Exception ex)
        {
            _hook?.Dispose(); _hook = null; loaded?.Dispose(); _speech = null;
            ModelStatus.Text = "Couldn't load speech or microphone: " + ex.Message;
            SetSetupEnabled(true);
        }
        finally { _loading = false; if (_speech == null) SetSetupEnabled(true); }
    }

    private void SetSetupEnabled(bool enabled)
    {
        DownloadButton.IsEnabled = enabled; LoadButton.IsEnabled = enabled;
        ModelPathBox.IsEnabled = enabled; ThreadCountBox.IsEnabled = enabled;
    }

    private void SaveSettings(string modelDirectory, int threads)
    {
        if (!_settingsWritable) return;
        try
        {
            _settings = _settings with { SpeechModelPath = modelDirectory, CpuThreads = threads };
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, SettingsMigrator.Save(_settings));
            File.Move(temporary, _settingsPath, true);
        }
        catch (Exception ex) { StatusText.Text = "Speech is ready, but settings couldn't be saved: " + ex.Message; }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        _hook?.Dispose(); _hook = null;
        CancelCapture();
        var speech = _speech; _speech = null;
        PauseButton.IsEnabled = false;
        ModelStatus.Text = "Closing microphone…";
        if (speech != null) await Task.Run(speech.Dispose);
        if (_closing) return;
        MicLevel.Value = 0; SetSetupEnabled(true);
        ModelStatus.Text = "Microphone closed. Hotkeys are released.";
    }

    private void HandleHotkey(HotkeyUpdate update, nint foreground)
    {
        if (_closing || _speech == null) return;
        foreach (var action in update.Actions)
        {
            switch (action)
            {
                case HotkeyAction.Started: BeginCapture(update.Mode, foreground); break;
                case HotkeyAction.ModeChanged:
                    _mode = update.Mode;
                    if (_mode == DictationMode.VoiceEdit)
                    {
                        CancelCapture();
                        StatusText.Text = "Voice edit is a later phase. Nothing was pasted.";
                    }
                    else ShortcutStatus.Text = "Raw mode · release Ctrl or Alt to finish.";
                    break;
                case HotkeyAction.HandsFreeEnabled:
                    ShortcutStatus.Text = "Hands-free recording · Ctrl + Alt finishes; Esc cancels."; break;
                case HotkeyAction.Stopped: _ = FinishCaptureAsync(); break;
                case HotkeyAction.Cancelled: CancelCapture(); StatusText.Text = "Recording cancelled."; break;
                case HotkeyAction.PasteLast: _ = PasteLastAsync(foreground); break;
            }
        }
    }

    private void BeginCapture(DictationMode mode, nint foreground)
    {
        if (_busy || _recording)
        {
            StatusText.Text = "Still finishing the previous dictation. Release the keys and try again.";
            return;
        }
        if (mode == DictationMode.VoiceEdit)
        {
            StatusText.Text = "Voice edit is a later phase. Nothing was recorded or pasted.";
            return;
        }
        try
        {
            _operation?.Dispose();
            _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _target = ClipboardDelivery.CaptureTargetAsync(foreground, _operation.Token);
            _mode = mode; _recording = true;
            _speech!.StartRecording();
            _overlay.Begin(foreground);
            ShortcutStatus.Text = mode == DictationMode.Raw ? "Raw recording · release Ctrl or Alt to finish." : "Recording · deterministic cleanup in this pilot. Release Ctrl or Alt to finish.";
        }
        catch (Exception ex) { CancelCapture(); StatusText.Text = "Couldn't record: " + ex.Message; }
    }

    private async Task FinishCaptureAsync()
    {
        if (!_recording || _speech == null || _operation == null || _target == null) return;
        _recording = false; _busy = true;
        var speech = _speech; var operation = _operation; var target = _target;
        var stopwatch = Stopwatch.StartNew();
        _overlay.Processing(); ShortcutStatus.Text = "Finishing the last word…";
        try
        {
            var raw = await speech.StopRecordingAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _latest = DeterministicCleanup.Clean(raw).TrimEnd('\r', '\n');
            ResultBox.Text = _latest;
            var finalizationMs = stopwatch.Elapsed.TotalMilliseconds;
            if (string.IsNullOrWhiteSpace(_latest)) { StatusText.Text = "No speech was detected."; _overlay.Cancel(); return; }
            var capturedTarget = await target;
            _pendingDelivery = _delivery.DeliverAsync(_latest, capturedTarget, operation.Token);
            var result = await _pendingDelivery;
            if (_closing) return;
            StatusText.Text = $"{result.Message} Speech finalization: {finalizationMs:F0} ms (includes 300 ms audio tail).";
            if (result.Confirmed) _overlay.Complete(_latest); else _overlay.Error(result.Message);
        }
        catch (OperationCanceledException) { TryCancel(speech); if (!_closing) _overlay.Cancel(); }
        catch (Exception ex) { TryCancel(speech); if (!_closing) { StatusText.Text = "Dictation failed: " + ex.Message; _overlay.Error("Dictation failed. Check Ava."); } }
        finally
        {
            _busy = false;
            ShortcutStatus.Text = "Click a text field, then hold Ctrl + Alt and speak.";
        }
    }

    private void CancelCapture()
    {
        _recording = false; _operation?.Cancel();
        if (_speech != null) TryCancel(_speech);
        _overlay.Cancel();
        ShortcutStatus.Text = "Click a text field, then hold Ctrl + Alt and speak.";
    }

    private static void TryCancel(SpeechSession speech)
    {
        try { speech.CancelRecording(); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
    }

    private async Task PasteLastAsync(nint foreground)
    {
        if (_busy || _recording || string.IsNullOrEmpty(_latest)) return;
        _busy = true;
        _operation?.Dispose();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var operation = _operation;
        try
        {
            var target = await ClipboardDelivery.CaptureTargetAsync(foreground, operation.Token);
            _pendingDelivery = _delivery.DeliverAsync(_latest, target, operation.Token);
            var result = await _pendingDelivery;
            if (_closing) return;
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "Couldn't paste: " + ex.Message; }
        finally { _busy = false; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_latest)) return;
        try { Clipboard.SetText(_latest); StatusText.Text = "Copied."; }
        catch (Exception ex) { StatusText.Text = "Couldn't copy: " + ex.Message; }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { PracticeBox.Clear(); PracticeBox.Focus(); }
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ava", "logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
}
