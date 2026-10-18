using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Typr.Core;
using Typr.Infrastructure;
using Microsoft.Win32;

namespace Typr.Windows;

public partial class MainWindow : Window
{
    private SpeechSession? _speech;
    private GlobalKeyboardHook? _hook;
    private readonly CaptionOverlay _overlay = new();
    private readonly ClipboardDelivery _delivery;
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
    private bool _inputActive;
    private int _activeInputGeneration;
    private int _readyInputGeneration = -1;
    private int _speechGeneration;
    private DictationMode _mode;
    private string _latest = "";
    private AppSettings _settings = new(), _captureSettings = new();

    public MainWindow() : this(DefaultProfile()) { }
    private MainWindow((string Settings, string Local) profile) : this(profile.Settings, profile.Local, true) { }
    private static (string Settings, string Local) DefaultProfile() => (
        ProfileMigration.MigrateDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        ProfileMigration.MigrateDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    // Explicit directories keep presentation integration checks away from personal data.
    internal MainWindow(string settingsDirectory, string localDirectory, bool desktopIntegration = false)
    {
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        _localDirectory = localDirectory;
        _delivery = new ClipboardDelivery(Path.Combine(localDirectory, "logs"));
        _history = new TranscriptHistoryStore(Path.Combine(localDirectory, "history.json"));
        InitializeComponent();
        string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        AppVersionText.Text = $"TYPR  /  v{version}";
        Title = $"Typr {version} — your voice, your words";
        try { if (File.Exists(_settingsPath)) _settings = SettingsMigrator.Load(File.ReadAllText(_settingsPath)); }
        catch (Exception)
        {
            _settingsWritable = false;
            StatusText.Text = "Existing settings couldn't be read. The file is preserved; changes apply for this session only.";
        }
        if (desktopIntegration) _settings = _settings with { SpeechModelPath = ProfileMigration.NormalizeModelPath(_settings.SpeechModelPath,
            Path.Combine(Path.GetDirectoryName(localDirectory)!, ProfileMigration.LegacyDirectoryName), localDirectory) };
        try { _history.Load(); }
        catch (Exception)
        {
            _historyWritable = false;
            StatusText.Text = "History couldn't be read. The original file is preserved; new history saves are disabled.";
        }
        ModelPathBox.Text = _settings.SpeechModelPath ?? ModelDownload.DefaultDirectory;
        InitializeGpuSpeech();
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
        ConfigureCaptionOverlay();
        InitializeInputChoice();
        RefreshRules(); RefreshHistory();
        _initialized = true;
        ModelStatus.Text = AnySpeechModelReady(ModelPathBox.Text)
            ? "Speech model found. Your recording shortcut prepares it when needed."
            : "Download the model, then hold Ctrl + Alt in a writable field to talk.";
        RefreshReadiness();
        _clock.Tick += (_, _) => CaptureDuration.Text = $"Listening · {_captureTime.Elapsed:mm\\:ss}";
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        Closing += OnClosing;
        InitializeSpeechResources(desktopIntegration);
        InitializeCompanion(desktopIntegration);
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        => Dispatcher.BeginInvoke(() => { if (!_closing) ThemeManager.Apply(_settings.Theme, _settings.Accent); });

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; CancelPendingRecordingStart(); _lifetime.Cancel(); _operation?.Cancel(); _download?.Cancel();
        _gameModeMonitor?.Dispose(); _gameModeMonitor = null;
        if (_speech != null) TryCancel(_speech);
        _clock.Stop(); SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        await CloseCompanionAsync();
        _hook?.Dispose(); _hook = null;
        if (_pendingDelivery != null) { try { await _pendingDelivery; } catch { } }
        if (_resourceTransitionTask != null) { try { await _resourceTransitionTask; } catch { } }
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
    private async void Hero_Click(object sender, RoutedEventArgs e)
    {
        if (SpeechPausedForGaming) { Navigate("Settings"); StatusText.Text = "Speech is paused for gaming. Choose Everyday to resume."; return; }
        if (_loading || _busy || _recording || _recordingStart != null) return;
        if (_speech != null && (_speechFaulted || ReferenceEquals(sender, ReadinessActionButton))) Navigate("Settings");
        else if (_speech == null)
        {
            if (AnySpeechModelReady(ModelPathBox.Text.Trim())) await LoadSpeechAsync(focusPractice: true);
            else Navigate("Settings");
        }
        else
        {
            Navigate("Workspace"); PracticeBox.Focus();
            if (_settings.AllowButtonRecording) Practice_Click(sender, e);
            else StatusText.Text = ShortcutStatus.Text = "Hold Ctrl + Alt to turn the microphone on and talk. Release either key to finish; Esc cancels.";
        }
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
        ConfigureCompanion();
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
        var selected = (HistoryList.SelectedItem as TranscriptEntry)?.Id;
        var entries = _history.Entries.Where(entry => entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        HistoryList.ItemsSource = entries;
        if (selected != null) HistoryList.SelectedItem = entries.FirstOrDefault(entry => entry.Id == selected);
        HistoryEmpty.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = !_historyWritable ? "History couldn't be read. The original file is preserved." : query.Length > 0 ? "No matches. Try another word or clear the search." : "A fresh start. Your saved transcripts will appear here.";
        UpdateHistoryDetails(HistoryList.SelectedItem as TranscriptEntry);
        RefreshHistorySummary();
    }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryDetail == null) return;
        var entry = HistoryList.SelectedItem as TranscriptEntry;
        HistoryDetail.Text = entry?.Text ?? "";
        HistoryDetail.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
        CopyHistoryButton.IsEnabled = RemoveHistoryButton.IsEnabled = entry != null;
        UpdateHistoryDetails(entry);
    }
    private void SaveHistory(string text, string mode, string rawText = "", string? audioFileName = null,
        string? provider = null, string? model = null, string? reasoning = null, string deliveryStatus = "Saved", long durationMs = 0)
    {
        if (!_captureSettings.SaveHistory || !_settings.SaveHistory || !_historyWritable) return;
        try { _history.Add(text, mode, rawText, audioFileName, provider, model, reasoning, deliveryStatus, durationMs); RefreshHistory(); RefreshHistorySummary(); }
        catch (Exception) { StatusText.Text += " Transcript couldn't be saved to history; Copy text is still available."; }
    }
    private void CopyHistory_Click(object sender, RoutedEventArgs e) { if (HistoryList.SelectedItem is TranscriptEntry entry) CopyText(entry.Text); }
    private void RemoveHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not TranscriptEntry entry || !_historyWritable || _historyOperation != null) return;
        try { _history.Remove(entry.Id); RefreshHistory(); StatusText.Text = "Transcript removed from local history."; }
        catch (Exception) { StatusText.Text = "Couldn't remove that transcript. Check the local data folder and retry."; }
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_historyOperation != null) return;
        if (_history.Entries.Count == 0 || !_historyWritable) { StatusText.Text = "There is no loaded history to clear."; return; }
        if (MessageBox.Show(this, "Remove every saved transcript from this PC? This cannot be undone.", "Clear local history", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try { _history.Clear(); RefreshHistory(); StatusText.Text = "Local history cleared."; }
        catch (Exception) { StatusText.Text = "Couldn't clear history. Your existing entries are preserved."; }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (SpeechPausedForGaming) { StatusText.Text = "Speech downloads are paused for gaming. Choose Everyday to resume."; return; }
        if (_download != null) { _download.Cancel(); return; }
        if (_loading || _speech != null) return;
        _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        DownloadButton.Content = "Pause download"; LoadButton.IsEnabled = false; ModelPathBox.IsEnabled = false;
        RefreshGpuSpeech();
        ModelStatus.Text = "Downloading the verified model. You can pause and resume.";
        var progress = new Progress<double>(value => { if (!_closing) { DownloadProgress.Value = value; ModelStatus.Text = $"Downloading speech model · {value:P0}"; } });
        try
        {
            await ModelDownload.DownloadAsync(ModelPathBox.Text.Trim(), progress, _download.Token);
            if (!_closing) { ModelStatus.Text = "Download verified. Hold Ctrl + Alt to prepare speech and talk."; PersistSettings(_settings with { SpeechModelPath = ModelPathBox.Text.Trim() }); }
        }
        catch (OperationCanceledException) { if (!_closing) ModelStatus.Text = "Download paused. Choose Download speech model to resume."; }
        catch (Exception ex) { if (!_closing) ModelStatus.Text = "Download couldn't finish: " + ex.Message; }
        finally
        {
            _download.Dispose(); _download = null;
            if (!_closing) { DownloadButton.Content = "Download speech model"; LoadButton.IsEnabled = true; ModelPathBox.IsEnabled = true; RefreshReadiness(); }
        }
    }
    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadSpeechAsync(focusPractice: true);
    private Task LoadSpeechAsync(bool focusPractice)
    {
        if (SpeechPausedForGaming || _resourceTransitioning) { StatusText.Text = "Speech is paused for gaming or changing modes. Wait for Everyday mode before preparing speech."; return Task.CompletedTask; }
        if (_loading) return _speechLoadingTask ?? Task.CompletedTask;
        if (_speech != null || _download != null) return Task.CompletedTask;
        return _speechLoadingTask = LoadSpeechCoreAsync(focusPractice);
    }
    private async Task LoadSpeechCoreAsync(bool focusPractice)
    {
        _loading = true; SetSetupEnabled(false); RefreshReadiness();
        var directory = ModelPathBox.Text.Trim(); var threads = ThreadCountBox.SelectedIndex == 1 ? 4 : 2;
        var inputDeviceId = _settings.InputDeviceId;
        string? gpuDirectory = PreferGpu && NeMoSpeechDownload.IsReady(SelectedGpuDirectory) ? SelectedGpuDirectory : null;
        bool gpuFast = GpuFast;
        ModelStatus.Text = "Verifying and loading local speech…";
        SpeechSession? loaded = null;
        try
        {
            loaded = await Task.Run(() => new SpeechSession(directory, threads, _settings.SavedAudioLimit, inputDeviceId, gpuDirectory, gpuFast), _lifetime.Token);
            if (_closing) { loaded.Dispose(); return; }
            _speech = loaded;
            loaded.CaptureStateChanged += (generation, active) => Dispatcher.BeginInvoke(() =>
            {
                if (_closing || !ReferenceEquals(_speech, loaded)) return;
                if (active) { _inputActive = true; _activeInputGeneration = generation; }
                else if (_activeInputGeneration == generation) { _inputActive = false; if (_readyInputGeneration == generation) _readyInputGeneration = -1; }
                if (!active && !_recording && !_busy) CaptureDuration.Text = "Hold Ctrl + Alt to talk";
                RefreshReadiness();
            });
            loaded.InputReady += generation => Dispatcher.BeginInvoke(() => SpeechInputReady(loaded, generation));
            loaded.CaptionForRecording += (generation, text) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_closing && ReferenceEquals(_speech, loaded) && generation == _speechGeneration && (_recording || _busy) && _settings.LiveCaptionsEnabled)
                    {
                        UpdateLiveBarCaption(text);
                        _overlay.SetLines(CaptionWrapper.Wrap(text, _settings.CaptionMaxCharacters, _settings.CaptionMaxLines).Select(line => line.Text));
                    }
                });
            };
            loaded.Level += level => Dispatcher.BeginInvoke(() => { if (!_closing && _recording && ReferenceEquals(_speech, loaded)) { MicLevel.Value = level; _overlay.Level(level); _dictationBar.UpdateLevel(level); } });
            loaded.Error += message => Dispatcher.BeginInvoke(() =>
            {
                if (_closing || !ReferenceEquals(_speech, loaded)) return;
                _speechFaulted = true;
                CancelCapture(); StatusText.Text = "Microphone or speech error: " + message;
                ModelStatus.Text = "Speech stopped. Unload speech, check the selected input, then prepare it again.";
                _overlay.Error("Speech stopped. Check Typr for details.");
            });
            loaded.Prepare();
            loaded.SavedAudioLimit = _settings.SavedAudioLimit;
            _speechFaulted = false;
            ModelStatus.Text = "Local speech prepared · " + loaded.BackendDescription + (loaded.GpuFallbackReason is { } fallback ? ". " + fallback : ".");
            PersistSettings(_settings with { SpeechModelPath = directory, CpuThreads = threads });
            StatusText.Text = "Speech is prepared. The microphone opens only for a deliberate recording.";
            ShortcutStatus.Text = "Hold Ctrl + Alt in a writable field. Wait for Listening on the first use.";
            if (focusPractice) { Navigate("Workspace"); PracticeBox.Focus(); }
        }
        catch (OperationCanceledException) { loaded?.Dispose(); _speech = null; }
        catch (Exception ex)
        {
            loaded?.Dispose(); _speech = null;
            ModelStatus.Text = "Couldn't prepare speech: " + ex.Message;
            StatusText.Text = "Check the model folder and selected input, then try again.";
        }
        finally { _loading = false; if (!_closing) { SetSetupEnabled(_speech == null); PauseButton.IsEnabled = _speech != null; RefreshReadiness(); } }
    }
    private void SetSetupEnabled(bool enabled)
    {
        DownloadButton.IsEnabled = LoadButton.IsEnabled = ModelPathBox.IsEnabled = ThreadCountBox.IsEnabled = enabled;
        RefreshGpuSpeech();
    }
    private void SpeechInputReady(SpeechSession speech, int generation)
    {
        if (_closing || !ReferenceEquals(_speech, speech) || !_recording || generation != _speechGeneration || _readyInputGeneration == generation) return;
        _readyInputGeneration = generation;
        _captureTime.Restart(); _clock.Start(); _overlay.Listening(); CaptureDuration.Text = "Listening · 00:00";
        ShortcutStatus.Text = _practiceCapture ? "Listening. Release the keys or choose Finish dictation; Esc cancels." : "Listening. Release Ctrl or Alt to finish; hands-free uses the finish shortcut. Esc cancels.";
        StatusText.Text = "Listening on this PC.";
        RefreshReadiness();
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        _resumeSpeechAfterGaming = false;
        if (_loading) return;
        _loading = true; CancelCapture();
        var speech = _speech; _speech = null;
        PauseButton.IsEnabled = false; ModelStatus.Text = "Unloading speech…"; RefreshReadiness();
        try { if (speech != null) await Task.Run(speech.Dispose); ModelStatus.Text = "Speech unloaded. Your microphone is off; the recording shortcut can prepare speech again."; }
        catch (Exception ex) { ModelStatus.Text = "Speech unloaded with an error: " + ex.Message; }
        finally { _loading = false; if (!_closing) { _inputActive = false; MicLevel.Value = 0; SetSetupEnabled(true); RefreshReadiness(); } }
    }
    private void RefreshReadiness()
    {
        bool paused = SpeechPausedForGaming;
        bool ready = _speech != null && !_loading && !_speechFaulted && !paused && !_resourceTransitioning;
        bool installed = _speech != null || AnySpeechModelReady(ModelPathBox.Text.Trim());
        bool starting = _recordingStart != null;
        bool listening = _recording && _readyInputGeneration == _speechGeneration;
        if (_hook != null) _hook.CancellationActive = _recording || _busy || starting;
        InputDeviceBox.IsEnabled = RefreshInputsButton.IsEnabled = !_loading && _speech == null && !starting;
        RailStatus.Text = _inputActive ? (_recording ? "Microphone active" : "Closing microphone…") : _recording ? "Starting microphone…" : _loading ? "Preparing speech…" : _speechFaulted && _speech != null ? "Speech needs attention" : "Microphone off";
        HeroButton.Content = paused ? "Speech paused" : ready ? (_settings.AllowButtonRecording ? "Try dictation  →" : "Try the shortcut  →") : installed && _speech == null ? "Prepare speech  →" : "Set up Typr  →";
        HeroButton.IsEnabled = !paused && !_resourceTransitioning && !_loading && !_busy && !_recording && !starting;
        ReadinessActionButton.IsEnabled = !_resourceTransitioning && !_loading && !_busy && !_recording && !starting;
        ReadinessActionButton.Content = paused ? "Change mode" : installed && _speech == null ? "Prepare speech" : "Open setup";
        ReadinessTitle.Text = paused ? "Make room for your game." : _speechFaulted && _speech != null ? "Speech needs a fresh start." : _loading ? "Preparing speech…" : ready ? "Speech ready. Microphone off until dictation." : installed ? "Speech model installed. Ready to prepare." : "Your voice, at home on your PC.";
        ReadinessDetail.Text = paused ? "Gaming mode pauses new dictation and releases speech resources after current dictation finishes. Choose Everyday in Settings to resume." : _speechFaulted && _speech != null ? "Unload speech in Settings, check the selected input, then prepare it again." : ready ? "Keep speech prepared for quicker starts. Hold Ctrl + Alt to turn the microphone on; release to finish. Wait for Listening before speaking." : installed ? "Prepare speech to load the model into memory with the microphone off. Your recording shortcut can also prepare it; the first start takes longer." : "Download the speech model once in Settings. Then prepare speech for quicker starts, or hold Ctrl + Alt and wait for Listening before speaking.";
        PracticeButton.IsEnabled = !_busy && !starting && (_recording || ready && _settings.AllowButtonRecording);
        PracticeButton.Content = starting ? "Preparing…" : _busy ? "Finishing…" : _recording ? "Finish dictation" : _settings.AllowButtonRecording ? "Start dictation" : "Use recording shortcut";
        CancelButton.Visibility = _recording || _busy || starting ? Visibility.Visible : Visibility.Collapsed;
        CaptureState.Text = starting ? "PREPARING TO LISTEN" : _recording ? (listening ? "LISTENING" : "STARTING MICROPHONE") : _inputActive ? "CLOSING MICROPHONE" : _busy ? _processingPhase.ToUpperInvariant() : paused ? "GAMING / SPEECH PAUSED" : _speechFaulted && _speech != null ? "SPEECH NEEDS ATTENTION" : ready ? "SPEECH READY / MICROPHONE OFF" : "MICROPHONE OFF";
        RefreshCompanionState();
        RefreshSpeechResources();
        RefreshGpuSpeech();
    }
    private async void Practice_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { _ = FinishCaptureAsync(); return; }
        if (_speech == null || _busy || _speechFaulted || !_settings.AllowButtonRecording) return;
        PracticeBox.Focus();
        await StartCaptureAsync(DictationMode.Polish, 0, practice: true);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelCapture();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_recordingStart != null || _recording || _busy)) { CancelCapture(); e.Handled = true; }
    }
    private void HandleHotkey(HotkeyUpdate update, nint foreground)
    {
        if (_closing || !_settings.CommandShortcutsEnabled) return;
        foreach (var action in update.Actions)
        {
            switch (action)
            {
                case HotkeyAction.Started:
                    bool practice = IsActive && PracticeBox.IsKeyboardFocusWithin;
                    if (IsActive && !practice) { StatusText.Text = "Click the practice field or a writable field in another app before dictating."; break; }
                    _ = StartCaptureAsync(update.Mode, foreground, practice);
                    break;
                case HotkeyAction.ModeChanged:
                    if (!_recording && _recordingStart == null) break;
                    _mode = _startingMode = update.Mode;
                    if (_mode == DictationMode.VoiceEdit) { CancelCapture(); StatusText.Text = "Voice editing isn't available. Nothing new was inserted."; }
                    else ShortcutStatus.Text = "Raw mode · release Ctrl or Alt to finish.";
                    break;
                case HotkeyAction.HandsFreeEnabled:
                    if (_recording || _recordingStart != null) ShortcutStatus.Text = "Hands-free · Ctrl + Alt finishes; Esc cancels.";
                    break;
                case HotkeyAction.Stopped: CancelPendingRecordingStart(); _ = FinishCaptureAsync(); RefreshReadiness(); break;
                // A new chord can be rejected while an earlier recording drains.
                // Its cancellation must not cancel that earlier finalization.
                // Escape during processing is handled separately by the hook.
                case HotkeyAction.Cancelled: if (_recordingStart != null || _recording) CancelCapture(); break;
                case HotkeyAction.PasteLast: _ = PasteLastAsync(foreground); break;
            }
        }
    }
    private void BeginCapture(DictationMode mode, nint foreground, bool practice = false, Task<DeliveryTarget>? destination = null, bool restoreFocus = false)
    {
        if (_busy || _recording || _historyOperation != null) { StatusText.Text = "Still finishing the previous operation. Release the keys and try again."; return; }
        if (mode == DictationMode.VoiceEdit) { StatusText.Text = "Voice editing isn't available. Nothing was recorded."; return; }
        try
        {
            _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _captureSettings = _settings; _practiceCapture = practice;
            _restoreDeliveryFocus = restoreFocus;
            _target = practice ? null : destination ?? ClipboardDelivery.CaptureTargetAsync(foreground, _operation.Token);
            _mode = mode; _recording = true; _readyInputGeneration = -1;
            _speechGeneration = _speech!.StartRecording(); _overlay.Begin(foreground);
            _captureTime.Reset(); _clock.Stop(); CaptureDuration.Text = "Opening microphone…";
            UpdateLiveBarCaption("");
            ShortcutStatus.Text = "Opening microphone. Speak when Listening appears; release or Esc cancels the start.";
            StatusText.Text = "Opening the selected microphone. Wait for Listening before speaking."; RefreshReadiness();
        }
        catch (Exception ex) { CancelCapture(); StatusText.Text = "Couldn't record: " + ex.Message; }
    }
    private async Task FinishCaptureAsync()
    {
        if (!_recording || _speech == null || _operation == null) return;
        _recording = false; _busy = true; _clock.Stop(); _captureTime.Stop(); MicLevel.Value = 0;
        var speech = _speech; var operation = _operation; var target = _target; var practice = _practiceCapture;
        bool clean = _mode != DictationMode.Raw && _captureSettings.PolishEnabled;
        bool restoreFocus = _restoreDeliveryFocus;
        string deliveryStatus = practice ? "Practice" : "Paste off";
        var stopwatch = Stopwatch.StartNew();
        _processingPhase = "Recognizing…";
        CaptureDuration.Text = "Processing…"; StatusText.Text = "Finishing your words on this PC.";
        _overlay.Processing(); ShortcutStatus.Text = "Finishing your words. Esc can still cancel before insertion."; RefreshReadiness();
        try
        {
            var raw = await speech.StopRecordingAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var prepared = OutputPersonalizer.Transform(raw, clean, _captureSettings.Vocabulary, _captureSettings.Snippets);
            if (clean && _captureSettings.AiPolishEnabled && !string.IsNullOrWhiteSpace(prepared)) {
                _processingPhase = "Polishing…";
                CaptureDuration.Text = "Polishing…";
                StatusText.Text = "Recognized locally. Polishing punctuation and formatting…";
                UpdateLiveBarCaption(prepared); RefreshReadiness();
            }
            var polished = clean ? await PolishTextAsync(prepared, _captureSettings, operation.Token) : new PolishOutcome(prepared, null, null, null, "Raw");
            operation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(polished.Text)) { StatusText.Text = "No speech detected. Your previous transcript is still available. Check the input and try again."; _overlay.Cancel(); return; }
            _latest = polished.Text;
            ResultBox.Text = _latest; ResultBox.Visibility = string.IsNullOrWhiteSpace(_latest) ? Visibility.Collapsed : Visibility.Visible;
            ResultEmpty.Visibility = string.IsNullOrWhiteSpace(_latest) ? Visibility.Visible : Visibility.Collapsed;
            CopyResultButton.IsEnabled = !string.IsNullOrWhiteSpace(_latest);
            if (practice)
            {
                PracticeBox.SelectedText = _latest; PracticeBox.CaretIndex = PracticeBox.SelectionStart + _latest.Length;
                StatusText.Text = $"Added to your practice field · {stopwatch.Elapsed.TotalMilliseconds:F0} ms to finish · {(clean ? "Clean" : "Raw")}.";
                _overlay.Complete(_latest);
            }
            else if (_captureSettings.AutoPasteEnabled)
            {
                var capturedTarget = await target!;
                operation.Token.ThrowIfCancellationRequested();
                CaptureState.Text = "PLACING YOUR WORDS";
                _pendingDelivery = _delivery.DeliverAsync(_latest, capturedTarget, operation.Token, restoreFocus);
                var result = await _pendingDelivery;
                if (_closing) return;
                StatusText.Text = result.Message;
                deliveryStatus = result.Confirmed ? "Inserted" : "Copy available";
                if (result.Confirmed) _overlay.Complete(_latest); else _overlay.Error("Text is ready. Use Copy text in Typr.");
            }
            else { StatusText.Text = "Automatic paste is off. Your finished text is ready to copy or paste from the bar."; _overlay.Complete(_latest); }
            if (polished.Note.Length > 0 && polished.Note != "Raw") StatusText.Text += " " + polished.Note;
            SaveHistory(_latest, clean ? "Clean" : "Raw", raw, speech.LastAudioFileName, polished.Provider, polished.Model, polished.Effort,
                deliveryStatus, (long)_captureTime.Elapsed.TotalMilliseconds);
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
        _barHoldActive = false; ++_barHoldGeneration; CancelPendingRecordingStart();
        _recording = false; _speechGeneration = _readyInputGeneration = -1; _operation?.Cancel(); _clock.Stop(); MicLevel.Value = 0;
        if (_speech != null) TryCancel(_speech);
        _overlay.Cancel(); CaptureDuration.Text = _inputActive ? "Closing microphone…" : "Hold Ctrl + Alt to talk";
        ShortcutStatus.Text = "Cancelled. Start again whenever you're ready.";
        StatusText.Text = "Cancelled pending work. This doesn't undo text already inserted.";
        RefreshReadiness();
    }
    private static void TryCancel(SpeechSession speech)
    {
        try { speech.CancelRecording(); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
    }
    private async Task PasteLastAsync(nint foreground, DeliveryTarget? destination = null, bool restoreFocus = false)
    {
        if (_busy || _recording || _historyOperation != null || string.IsNullOrEmpty(_latest)) return;
        _busy = true; _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _processingPhase = "Placing your words…";
        var operation = _operation; RefreshReadiness();
        try
        {
            var target = destination ?? await ClipboardDelivery.CaptureTargetAsync(foreground, operation.Token);
            _pendingDelivery = _delivery.DeliverAsync(_latest, target, operation.Token, restoreFocus);
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
