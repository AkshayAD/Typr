using System.Windows;
using System.Windows.Controls;

namespace Typr.Windows;

public partial class MainWindow
{
    private WindowsGameModeMonitor? _gameModeMonitor;
    private bool _windowsGaming, _resourcePreferencesInitializing, _resourceTransitioning, _resumeSpeechAfterGaming, _gameInputsPaused;
    private Task? _resourceTransitionTask;
    private bool SpeechPausedForGaming => _settings.SpeechResourceMode == "Gaming" || _settings.SpeechResourceMode == "Automatic" && _windowsGaming;
    private Typr.Core.AppSettings EffectiveShortcutSettings => _settings with { CommandShortcutsEnabled = _settings.CommandShortcutsEnabled && !(SpeechPausedForGaming && !_recording && !_busy && !HasPendingDictationOutput) };

    private void InitializeSpeechResources(bool desktopIntegration)
    {
        _resourcePreferencesInitializing = true;
        SpeechResourceModeBox.SelectedIndex = Array.IndexOf(new[] { "Automatic", "Everyday", "Gaming" }, _settings.SpeechResourceMode);
        _resourcePreferencesInitializing = false;
        if (desktopIntegration) {
            _gameModeMonitor = new WindowsGameModeMonitor();
            _gameModeMonitor.Changed += () => Dispatcher.BeginInvoke(() => {
                if (_closing) return;
                _windowsGaming = _gameModeMonitor?.IsGaming == true;
                RefreshReadiness();
            });
            _windowsGaming = _gameModeMonitor.IsGaming;
            RefreshReadiness();
        }
        RefreshReadiness();
    }

    private void SpeechResourceMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _resourcePreferencesInitializing || SpeechResourceModeBox.SelectedIndex < 0) return;
        PersistSettings(_settings with { SpeechResourceMode = new[] { "Automatic", "Everyday", "Gaming" }[SpeechResourceModeBox.SelectedIndex] });
        RefreshReadiness();
    }

    private void RefreshSpeechResources()
    {
        bool paused = SpeechPausedForGaming;
        SpeechResourceStatus.Text = paused
            ? _recording || _busy || HasPendingDictationOutput ? "Gaming · finishing current dictation before pausing" : _loading || _resourceTransitioning ? "Gaming · releasing speech resources" : "Gaming · speech paused, microphone off"
            : "Everyday · " + (_speech?.BackendDescription ?? "speech unloaded") + (_settings.SpeechResourceMode == "Automatic" && _gameModeMonitor is { IsSupported: false } ? " · automatic game detection unavailable; use Gaming manually" : "");
        if (_closing) return;
        if (_desktopIntegration)
        {
            bool showBar = ShouldShowDictationBar;
            if (!showBar && _dictationBar.IsVisible) _dictationBar.HideBar();
            else if (showBar && !_dictationBar.IsVisible) _dictationBar.ShowBar();
            bool pauseInputs = paused && !_recording && !_busy && !HasPendingDictationOutput;
            if (pauseInputs) { _targetTimer.Stop(); _externalTargets.Clear(); }
            else if (IsLoaded && !_targetTimer.IsEnabled && !_closing) _targetTimer.Start();
            if (IsLoaded && _gameInputsPaused != pauseInputs && !_closing)
            {
                _gameInputsPaused = pauseInputs;
                ShortcutConfigurationStatus.Text = _commandShortcuts.Configure(this, EffectiveShortcutSettings);
                ConfigureDictationHook();
            }
        }
        if (_resourceTransitioning) return;
        if (paused)
        {
            CancelPendingRecordingStart();
            _download?.Cancel(); _historyOperation?.Cancel(); _providerComparison?.Cancel();
            if (_loading || _recording || _busy || HasPendingDictationOutput || _historyOperation != null || _speech == null) return;
            _resumeSpeechAfterGaming = true;
            var speech = _speech; _speech = null;
            _resourceTransitioning = true;
            _resourceTransitionTask = ReleaseSpeechForGamingAsync(speech);
        }
        else if (_resumeSpeechAfterGaming && !_loading && !_recording && !_busy && !HasPendingDictationOutput)
        {
            _resumeSpeechAfterGaming = false;
            _resourceTransitionTask = LoadSpeechAsync(focusPractice: false);
        }
    }

    private async Task ReleaseSpeechForGamingAsync(Typr.Infrastructure.SpeechSession speech)
    {
        RefreshReadiness();
        try { await Task.Run(speech.Dispose); }
        catch (Exception) { if (!_closing) StatusText.Text = "Speech stopped with a cleanup error. Restart Typr before dictating again."; _speechFaulted = true; }
        finally {
            _activeInputGenerations.Clear(); _inputActive = false; _readyInputGeneration = -1; MicLevel.Value = 0;
            _resourceTransitioning = false;
            if (!_closing) { ModelStatus.Text = "Speech paused for gaming. Microphone off."; PauseButton.IsEnabled = false; RefreshReadiness(); }
        }
    }
}
