using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Ava.Core;
using Ava.Infrastructure;
using Microsoft.Win32;

namespace Ava.Windows;

public partial class MainWindow : Window
{
    private SpeechSession? _speech;
    private GlobalKeyboardHook? _hook;
    private readonly CaptionOverlay _overlay = new();
    private readonly ClipboardDelivery _delivery = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _captureTime = new();
    private readonly TranscriptHistoryStore _history;
    private readonly string _settingsPath, _localDirectory;
    private CancellationTokenSource? _operation, _download;
    private Task<DeliveryTarget>? _target;
    private Task<DeliveryResult>? _pendingDelivery;
    private bool _recording, _busy, _loading, _closing, _allowClose, _initialized, _practiceCapture, _navigating, _speechFaulted;
    private bool _settingsWritable = true, _historyWritable = true;
    private int _speechGeneration;
    private DictationMode _mode;
    private string _latest = "";
    private AppSettings _settings = new(), _captureSettings = new();

    public MainWindow() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ava"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ava")) { }

    // Explicit directories keep presentation integration checks away from personal data.
    internal MainWindow(string settingsDirectory, string localDirectory)
    {
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        _localDirectory = localDirectory;
        _history = new TranscriptHistoryStore(Path.Combine(localDirectory, "history.json"));
        InitializeComponent();
        try { if (File.Exists(_settingsPath)) _settings = SettingsMigrator.Load(File.ReadAllText(_settingsPath)); }
        catch (Exception)
        {
            _settingsWritable = false;
            StatusText.Text = "Existing settings couldn't be read. The file is preserved; changes apply for this session only.";
        }
        try { _history.Load(); }
        catch (Exception)
        {
            _historyWritable = false;
            StatusText.Text = "History couldn't be read. The original file is preserved; new history saves are disabled.";
        }
        ModelPathBox.Text = _settings.SpeechModelPath ?? ModelDownload.DefaultDirectory;
        ThreadCountBox.SelectedIndex = _settings.CpuThreads == 4 ? 1 : 0;
        ThemeBox.SelectedIndex = Array.IndexOf(new[] { "System", "Light", "Dark" }, _settings.Theme);
        AccentBox.SelectedIndex = Array.IndexOf(new[] { "Iris", "Forest", "Ocean" }, _settings.Accent);
        HistoryEnabledBox.IsChecked = _settings.SaveHistory;
        CaptionsBox.IsChecked = _settings.LiveCaptionsEnabled;
        BarVisibleBox.IsChecked = _settings.BarVisible;
        CaptionLinesBox.SelectedIndex = _settings.CaptionMaxLines - 1;
        CaptionOpacityBox.SelectedIndex = _settings.CaptionOpacity < .75 ? 0 : _settings.CaptionOpacity < .95 ? 1 : 2;
        AudioRetentionBox.IsChecked = _settings.SavedAudioLimit > 0;
        OutputModeBox.SelectedIndex = _settings.PolishEnabled ? 0 : 1;
        ThemeManager.Apply(_settings.Theme, _settings.Accent);
        _overlay.Configure(_settings);
        RefreshRules(); RefreshHistory();
        _initialized = true;
        ModelStatus.Text = ModelDownload.IsReady(ModelPathBox.Text)
            ? "Speech model found. Enable the microphone when you're ready."
            : "Step 1: download the model. Step 2: enable the microphone. Step 3: try your voice.";
        RefreshReadiness();
        _clock.Tick += (_, _) => CaptureDuration.Text = $"Listening · {_captureTime.Elapsed:mm\\:ss}";
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        Closing += OnClosing;
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        => Dispatcher.BeginInvoke(() => { if (!_closing) ThemeManager.Apply(_settings.Theme, _settings.Accent); });

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _lifetime.Cancel(); _operation?.Cancel(); _download?.Cancel();
        _clock.Stop(); SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        _hook?.Dispose(); _hook = null;
        if (_pendingDelivery != null) { try { await _pendingDelivery; } catch { } }
        var speech = _speech; _speech = null;
        if (speech != null) { try { await Task.Run(speech.Dispose); } catch { } }
        _overlay.Close();
        _allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void Nav_Checked(object sender, RoutedEventArgs e) { if (_initialized && !_navigating) Navigate((string)((RadioButton)sender).Tag); }
    private void Navigate(string section)
    {
        _navigating = true;
        (string Name, FrameworkElement Page, RadioButton Nav)[] pages =
        [ ("Workspace", WorkspacePage, WorkspaceNav), ("History", HistoryPage, HistoryNav),
          ("Vocabulary", VocabularyPage, VocabularyNav), ("Snippets", SnippetsPage, SnippetsNav),
          ("Appearance", AppearancePage, AppearanceNav), ("Settings", SettingsPage, SettingsNav) ];
        foreach (var page in pages) { page.Page.Visibility = page.Name == section ? Visibility.Visible : Visibility.Collapsed; page.Nav.IsChecked = page.Name == section; }
        Breadcrumb.Text = section == "Workspace" ? "Your workspace  /  Dictation" : "Your workspace  /  " + section;
        PageScroll.ScrollToTop();
        _navigating = false;
    }
    private void Setup_Click(object sender, RoutedEventArgs e) => Navigate("Settings");
    private void Hero_Click(object sender, RoutedEventArgs e)
    {
        if (_speech == null || _speechFaulted) Navigate("Settings");
        else { Navigate("Workspace"); PracticeBox.Focus(); Practice_Click(sender, e); }
    }

    private bool PersistSettings(AppSettings settings)
    {
        if (!_settingsWritable)
        {
            _settings = settings;
            StatusText.Text = "Applied for this session. The unreadable settings file is preserved.";
            return true;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, SettingsMigrator.Save(settings));
            File.Move(temporary, _settingsPath, true);
            _settings = settings;
            StatusText.Text = "Preferences saved on this PC.";
            return true;
        }
        catch (Exception)
        {
            _settings = settings;
            StatusText.Text = "Applied for this session, but couldn't save. Check that your settings folder is writable.";
            return true;
        }
    }

    private static string SelectedText(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Content;
    private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (PersistSettings(_settings with { Theme = SelectedText(ThemeBox), Accent = SelectedText(AccentBox) }))
            ThemeManager.Apply(_settings.Theme, _settings.Accent);
    }
    private void Preferences_Click(object sender, RoutedEventArgs e) => ApplyPreferences(sender);
    private void Preferences_Changed(object sender, SelectionChangedEventArgs e) => ApplyPreferences(sender);
    private void ApplyPreferences(object sender)
    {
        if (!_initialized) return;
        var settings = _settings with
        {
            SaveHistory = HistoryEnabledBox.IsChecked == true,
            PolishEnabled = OutputModeBox.SelectedIndex == 0,
            LiveCaptionsEnabled = CaptionsBox.IsChecked == true,
            BarVisible = BarVisibleBox.IsChecked == true,
            CaptionMaxLines = CaptionLinesBox.SelectedIndex + 1
        };
        // Preserve existing precise values unless their own control was changed.
        if (ReferenceEquals(sender, CaptionOpacityBox)) settings = settings with { CaptionOpacity = double.Parse((string)((ComboBoxItem)CaptionOpacityBox.SelectedItem).Tag, CultureInfo.InvariantCulture) };
        if (ReferenceEquals(sender, AudioRetentionBox)) settings = settings with { SavedAudioLimit = AudioRetentionBox.IsChecked == true ? 200 : 0 };
        if (!PersistSettings(settings)) return;
        if (_speech != null) _speech.SavedAudioLimit = _settings.SavedAudioLimit;
        _overlay.Configure(_settings);
    }

    private void RefreshRules()
    {
        VocabularyList.ItemsSource = _settings.Vocabulary;
        SnippetsList.ItemsSource = _settings.Snippets;
        VocabularyEmpty.Visibility = _settings.Vocabulary.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnippetsEmpty.Visibility = _settings.Snippets.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AddVocabulary_Click(object sender, RoutedEventArgs e) => AddRule(false);
    private void AddSnippet_Click(object sender, RoutedEventArgs e) => AddRule(true);
    private void AddRule(bool snippet)
    {
        var phraseBox = snippet ? SnippetPhrase : VocabularyPhrase;
        var replacementBox = snippet ? SnippetReplacement : VocabularyReplacement;
        var feedback = snippet ? SnippetFeedback : VocabularyFeedback;
        var phrase = phraseBox.Text.Trim(); var replacement = replacementBox.Text.Trim();
        var rules = snippet ? _settings.Snippets : _settings.Vocabulary;
        if (phrase.Length == 0 || replacement.Length == 0) { feedback.Text = "Add both a spoken phrase and the text to write."; phraseBox.Focus(); return; }
        if (rules.Length >= OutputPersonalizer.MaxRules) { feedback.Text = "You can keep up to 200 rules. Remove one before adding another."; return; }
        if (_settings.Vocabulary.Concat(_settings.Snippets).Any(r => r.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase)))
        { feedback.Text = "That spoken phrase is already used. Remove its existing rule or choose a different phrase."; return; }
        var updated = rules.Append(new TextReplacement(phrase, replacement)).ToArray();
        if (!PersistSettings(snippet ? _settings with { Snippets = updated } : _settings with { Vocabulary = updated })) { feedback.Text = "Couldn't save. Your entry is still here so you can retry."; return; }
        phraseBox.Clear(); replacementBox.Clear(); RefreshRules();
        feedback.Text = snippet ? "Snippet created. Say the phrase in Clean mode to use it." : "Word added. Typr will use your spelling in Clean mode.";
        phraseBox.Focus();
    }
    private void RemoveVocabulary_Click(object sender, RoutedEventArgs e) => RemoveRule(false);
    private void RemoveSnippet_Click(object sender, RoutedEventArgs e) => RemoveRule(true);
    private void RemoveRule(bool snippet)
    {
        var selected = (TextReplacement?)(snippet ? SnippetsList : VocabularyList).SelectedItem;
        var feedback = snippet ? SnippetFeedback : VocabularyFeedback;
        if (selected == null) { feedback.Text = "Select a rule to remove."; return; }
        var updated = (snippet ? _settings.Snippets : _settings.Vocabulary).Where(r => r != selected).ToArray();
        if (!PersistSettings(snippet ? _settings with { Snippets = updated } : _settings with { Vocabulary = updated })) return;
        RefreshRules(); feedback.Text = "Rule removed.";
    }

    private void HistorySearch_Changed(object sender, TextChangedEventArgs e) { if (_initialized) RefreshHistory(); }
    private void RefreshHistory()
    {
        var query = HistorySearchBox.Text.Trim();
        var entries = _history.Entries.Where(entry => entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        HistoryList.ItemsSource = entries;
        HistoryEmpty.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = !_historyWritable ? "History couldn't be read. The original file is preserved." : query.Length > 0 ? "No matches. Try another word or clear the search." : "A fresh start. Your saved transcripts will appear here.";
    }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryDetail == null) return;
        var entry = HistoryList.SelectedItem as TranscriptEntry;
        HistoryDetail.Text = entry?.Text ?? "";
        HistoryDetail.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
        CopyHistoryButton.IsEnabled = RemoveHistoryButton.IsEnabled = entry != null;
    }
    private void SaveHistory(string text, string mode)
    {
        if (!_captureSettings.SaveHistory || !_settings.SaveHistory || !_historyWritable) return;
        try { _history.Add(text, mode); RefreshHistory(); }
        catch (Exception) { StatusText.Text += " Transcript couldn't be saved to history; Copy text is still available."; }
    }
    private void CopyHistory_Click(object sender, RoutedEventArgs e) { if (HistoryList.SelectedItem is TranscriptEntry entry) CopyText(entry.Text); }
    private void RemoveHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not TranscriptEntry entry || !_historyWritable) return;
        try { _history.Remove(entry.Id); RefreshHistory(); StatusText.Text = "Transcript removed from local history."; }
        catch (Exception) { StatusText.Text = "Couldn't remove that transcript. Check the local data folder and retry."; }
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Entries.Count == 0 || !_historyWritable) { StatusText.Text = "There is no loaded history to clear."; return; }
        if (MessageBox.Show(this, "Remove every saved transcript from this PC? This cannot be undone.", "Clear local history", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try { _history.Clear(); RefreshHistory(); StatusText.Text = "Local history cleared."; }
        catch (Exception) { StatusText.Text = "Couldn't clear history. Your existing entries are preserved."; }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_download != null) { _download.Cancel(); return; }
        if (_loading || _speech != null) return;
        _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        DownloadButton.Content = "Pause download"; LoadButton.IsEnabled = false; ModelPathBox.IsEnabled = false;
        ModelStatus.Text = "Downloading the verified model. You can pause and resume.";
        var progress = new Progress<double>(value => { if (!_closing) { DownloadProgress.Value = value; ModelStatus.Text = $"Downloading speech model · {value:P0}"; } });
        try
        {
            await ModelDownload.DownloadAsync(ModelPathBox.Text.Trim(), progress, _download.Token);
            if (!_closing) { ModelStatus.Text = "Download verified. Enable the microphone to try your voice."; PersistSettings(_settings with { SpeechModelPath = ModelPathBox.Text.Trim() }); }
        }
        catch (OperationCanceledException) { if (!_closing) ModelStatus.Text = "Download paused. Choose Download speech model to resume."; }
        catch (Exception ex) { if (!_closing) ModelStatus.Text = "Download couldn't finish: " + ex.Message; }
        finally
        {
            _download.Dispose(); _download = null;
            if (!_closing) { DownloadButton.Content = "Download speech model"; LoadButton.IsEnabled = true; ModelPathBox.IsEnabled = true; RefreshReadiness(); }
        }
    }
    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _speech != null || _download != null) return;
        _loading = true; SetSetupEnabled(false); RefreshReadiness();
        var directory = ModelPathBox.Text.Trim(); var threads = ThreadCountBox.SelectedIndex == 1 ? 4 : 2;
        ModelStatus.Text = "Verifying and loading local speech…";
        SpeechSession? loaded = null;
        try
        {
            loaded = await Task.Run(() => new SpeechSession(directory, threads, _settings.SavedAudioLimit), _lifetime.Token);
            if (_closing) { loaded.Dispose(); return; }
            _speech = loaded;
            loaded.CaptionForRecording += (generation, text) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_closing && ReferenceEquals(_speech, loaded) && generation == _speechGeneration && (_recording || _busy) && _settings.LiveCaptionsEnabled)
                        _overlay.SetLines(CaptionWrapper.Wrap(text, _settings.CaptionMaxCharacters, _settings.CaptionMaxLines).Select(line => line.Text));
                });
            };
            loaded.Level += level => Dispatcher.BeginInvoke(() => { if (!_closing) { MicLevel.Value = level; if (_recording) _overlay.Level(level); } });
            loaded.Error += message => Dispatcher.BeginInvoke(() =>
            {
                if (_closing || !ReferenceEquals(_speech, loaded)) return;
                _speechFaulted = true;
                CancelCapture(); StatusText.Text = "Microphone or speech error: " + message;
                ModelStatus.Text = "Speech stopped. Close the microphone, check your device, then enable it again.";
                _overlay.Error("Speech stopped. Check Typr for details.");
            });
            loaded.StartMicrophone();
            loaded.SavedAudioLimit = _settings.SavedAudioLimit;
            _speechFaulted = false;
            _hook = new GlobalKeyboardHook();
            _hook.Updated += (update, foreground) => Dispatcher.BeginInvoke(() => HandleHotkey(update, foreground));
            _hook.EscapePressed += () => Dispatcher.BeginInvoke(() => { if (_recording || _busy) CancelCapture(); });
            ModelStatus.Text = $"Local speech ready · {threads} CPU threads · microphone open for pre-roll.";
            PersistSettings(_settings with { SpeechModelPath = directory, CpuThreads = threads });
            StatusText.Text = "You're ready. Try your voice below, or hold Ctrl + Alt in a writable text field.";
            ShortcutStatus.Text = "Hold Ctrl + Alt in a writable field, or choose Start dictation to practice here.";
            Navigate("Workspace"); PracticeBox.Focus();
        }
        catch (OperationCanceledException) { loaded?.Dispose(); _speech = null; }
        catch (Exception ex)
        {
            _hook?.Dispose(); _hook = null; loaded?.Dispose(); _speech = null;
            ModelStatus.Text = "Couldn't enable speech: " + ex.Message;
            StatusText.Text = "Check the model folder and default communication microphone, then try again.";
        }
        finally { _loading = false; if (!_closing) { SetSetupEnabled(_speech == null); PauseButton.IsEnabled = _speech != null; RefreshReadiness(); } }
    }
    private void SetSetupEnabled(bool enabled)
    {
        DownloadButton.IsEnabled = LoadButton.IsEnabled = ModelPathBox.IsEnabled = ThreadCountBox.IsEnabled = enabled;
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true; _hook?.Dispose(); _hook = null; CancelCapture();
        var speech = _speech; _speech = null;
        PauseButton.IsEnabled = false; ModelStatus.Text = "Closing microphone…"; RefreshReadiness();
        try { if (speech != null) await Task.Run(speech.Dispose); ModelStatus.Text = "Microphone closed. Global shortcuts are released."; }
        catch (Exception ex) { ModelStatus.Text = "Microphone closed with an error: " + ex.Message; }
        finally { _loading = false; if (!_closing) { MicLevel.Value = 0; SetSetupEnabled(true); RefreshReadiness(); } }
    }
    private void RefreshReadiness()
    {
        bool ready = _speech != null && !_loading && !_speechFaulted;
        if (_hook != null) _hook.CancellationActive = _recording || _busy;
        RailStatus.Text = _loading ? "Updating microphone…" : _speechFaulted && _speech != null ? "Speech needs attention" : ready ? "Microphone is open" : "Microphone is closed";
        HeroButton.Content = ready ? "Try dictation  →" : "Set up Typr  →";
        HeroButton.IsEnabled = !_loading && !_busy && !_recording;
        ReadinessTitle.Text = _speechFaulted && _speech != null ? "Speech needs a fresh start." : ready ? "Your voice is ready. Make yourself heard." : "Your voice, at home on your PC.";
        ReadinessDetail.Text = _speechFaulted && _speech != null ? "Close the microphone in Settings, check your device, then enable it again." : ready ? "Local recognition is running. Close the microphone in Settings whenever you want." : "Open setup to download the speech model and enable your microphone.";
        PracticeButton.IsEnabled = ready && !_busy;
        PracticeButton.Content = _busy ? "Finishing…" : _recording ? "Finish dictation" : "Start dictation";
        CancelButton.Visibility = _recording || _busy ? Visibility.Visible : Visibility.Collapsed;
        CaptureState.Text = _busy ? "FINISHING YOUR WORDS" : _recording ? "LISTENING" : _speechFaulted && _speech != null ? "SPEECH NEEDS ATTENTION" : ready ? "READY TO LISTEN" : "MICROPHONE CLOSED";
    }
    private void Practice_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { _ = FinishCaptureAsync(); return; }
        if (_speech == null || _busy || _speechFaulted) return;
        PracticeBox.Focus();
        BeginCapture(DictationMode.Polish, new System.Windows.Interop.WindowInteropHelper(this).Handle, practice: true);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelCapture();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_recording || _busy)) { CancelCapture(); e.Handled = true; }
    }
    private void HandleHotkey(HotkeyUpdate update, nint foreground)
    {
        if (_closing || _speech == null || _speechFaulted) return;
        foreach (var action in update.Actions)
        {
            switch (action)
            {
                case HotkeyAction.Started: BeginCapture(update.Mode, foreground); break;
                case HotkeyAction.ModeChanged:
                    _mode = update.Mode;
                    if (_mode == DictationMode.VoiceEdit) { CancelCapture(); StatusText.Text = "Voice editing isn't available. Nothing new was inserted."; }
                    else ShortcutStatus.Text = "Raw mode · release Ctrl or Alt to finish.";
                    break;
                case HotkeyAction.HandsFreeEnabled: ShortcutStatus.Text = "Hands-free · Ctrl + Alt finishes; Esc cancels."; break;
                case HotkeyAction.Stopped: _ = FinishCaptureAsync(); break;
                case HotkeyAction.Cancelled: if (_recording || _busy) CancelCapture(); break;
                case HotkeyAction.PasteLast: _ = PasteLastAsync(foreground); break;
            }
        }
    }
    private void BeginCapture(DictationMode mode, nint foreground, bool practice = false)
    {
        if (_busy || _recording) { StatusText.Text = "Still finishing the previous dictation. Release the keys and try again."; return; }
        if (mode == DictationMode.VoiceEdit) { StatusText.Text = "Voice editing isn't available. Nothing was recorded."; return; }
        try
        {
            _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _captureSettings = _settings; _practiceCapture = practice;
            _target = practice ? null : ClipboardDelivery.CaptureTargetAsync(foreground, _operation.Token);
            _mode = mode; _recording = true;
            _speechGeneration = _speech!.StartRecording(); _overlay.Begin(foreground);
            _captureTime.Restart(); _clock.Start(); CaptureDuration.Text = "Listening · 00:00";
            ShortcutStatus.Text = practice ? "Speak naturally. Choose Finish dictation when you're done; Esc cancels." : "Listening · release Ctrl or Alt to finish. Esc cancels.";
            StatusText.Text = "Listening on this PC."; RefreshReadiness();
        }
        catch (Exception ex) { CancelCapture(); StatusText.Text = "Couldn't record: " + ex.Message; }
    }
    private async Task FinishCaptureAsync()
    {
        if (!_recording || _speech == null || _operation == null) return;
        _recording = false; _busy = true; _clock.Stop();
        var speech = _speech; var operation = _operation; var target = _target; var practice = _practiceCapture;
        bool clean = _mode != DictationMode.Raw && _captureSettings.PolishEnabled;
        var stopwatch = Stopwatch.StartNew();
        CaptureDuration.Text = "Processing…"; StatusText.Text = "Finishing your words on this PC.";
        _overlay.Processing(); ShortcutStatus.Text = "Finishing your words. Esc can still cancel before insertion."; RefreshReadiness();
        try
        {
            var raw = await speech.StopRecordingAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _latest = OutputPersonalizer.Transform(raw, clean, _captureSettings.Vocabulary, _captureSettings.Snippets);
            ResultBox.Text = _latest; ResultBox.Visibility = string.IsNullOrWhiteSpace(_latest) ? Visibility.Collapsed : Visibility.Visible;
            ResultEmpty.Visibility = string.IsNullOrWhiteSpace(_latest) ? Visibility.Visible : Visibility.Collapsed;
            CopyResultButton.IsEnabled = !string.IsNullOrWhiteSpace(_latest);
            if (string.IsNullOrWhiteSpace(_latest)) { StatusText.Text = "No speech detected. Check the microphone level and try again."; _overlay.Cancel(); return; }
            if (practice)
            {
                PracticeBox.SelectedText = _latest; PracticeBox.CaretIndex = PracticeBox.SelectionStart + _latest.Length;
                StatusText.Text = $"Added to your practice field · {stopwatch.Elapsed.TotalMilliseconds:F0} ms to finish · {(clean ? "Clean" : "Raw")}.";
                _overlay.Complete(_latest);
            }
            else
            {
                var capturedTarget = await target!;
                operation.Token.ThrowIfCancellationRequested();
                CaptureState.Text = "PLACING YOUR WORDS";
                _pendingDelivery = _delivery.DeliverAsync(_latest, capturedTarget, operation.Token);
                var result = await _pendingDelivery;
                if (_closing) return;
                StatusText.Text = result.Message;
                if (result.Confirmed) _overlay.Complete(_latest); else _overlay.Error("Text is ready. Use Copy text in Typr.");
            }
            SaveHistory(_latest, clean ? "Clean" : "Raw");
        }
        catch (OperationCanceledException)
        {
            TryCancel(speech);
            if (!_closing) { _overlay.Cancel(); StatusText.Text = "Cancelled pending work. Any text already inserted remains; your latest result is available to copy."; }
        }
        catch (Exception ex) { TryCancel(speech); if (!_closing) { StatusText.Text = "Dictation couldn't finish: " + ex.Message; _overlay.Error("Check Typr. Any completed text is available to copy."); } }
        finally
        {
            _busy = false;
            if (!_closing) { CaptureDuration.Text = "Hold Ctrl + Alt to talk"; ShortcutStatus.Text = "Ready for your next thought. Hold Ctrl + Alt in a writable field."; RefreshReadiness(); }
        }
    }
    private void CancelCapture()
    {
        _recording = false; _speechGeneration = -1; _operation?.Cancel(); _clock.Stop();
        if (_speech != null) TryCancel(_speech);
        _overlay.Cancel(); CaptureDuration.Text = "Hold Ctrl + Alt to talk";
        ShortcutStatus.Text = "Cancelled. Start again whenever you're ready.";
        StatusText.Text = "Cancelled pending work. This doesn't undo text already inserted.";
        RefreshReadiness();
    }
    private static void TryCancel(SpeechSession speech)
    {
        try { speech.CancelRecording(); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
    }
    private async Task PasteLastAsync(nint foreground)
    {
        if (_busy || _recording || string.IsNullOrEmpty(_latest)) return;
        _busy = true; _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var operation = _operation; RefreshReadiness();
        try
        {
            var target = await ClipboardDelivery.CaptureTargetAsync(foreground, operation.Token);
            _pendingDelivery = _delivery.DeliverAsync(_latest, target, operation.Token);
            var result = await _pendingDelivery;
            if (!_closing) StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { if (!_closing) StatusText.Text = "Repeat paste cancelled. This doesn't undo text already inserted."; }
        catch (Exception ex) { if (!_closing) StatusText.Text = "Couldn't paste: " + ex.Message; }
        finally { _busy = false; if (!_closing) RefreshReadiness(); }
    }
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_latest);
    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); StatusText.Text = "Copied. Paste your words wherever you need them."; }
        catch (Exception) { StatusText.Text = "The clipboard is busy. Your text is still here; try Copy again."; }
    }
    private void Data_Click(object sender, RoutedEventArgs e) => OpenDirectory(_localDirectory);
    private void Logs_Click(object sender, RoutedEventArgs e) => OpenDirectory(Path.Combine(_localDirectory, "logs"));
    private void OpenDirectory(string directory)
    {
        try { Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = "Couldn't open that folder. Check access to your local app data."; }
    }
}
