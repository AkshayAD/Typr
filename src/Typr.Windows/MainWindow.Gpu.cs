using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Typr.Infrastructure;

namespace Typr.Windows;

public partial class MainWindow
{
    private bool _gpuPreferencesInitializing, _gpuDownloading, _gpuPathEdited;
    private bool PreferGpu => _settings.SpeechAcceleration == "Automatic";
    private bool GpuFast => _settings.GpuSpeechLatency == "Fast";
    private string SelectedGpuDirectory => string.IsNullOrWhiteSpace(GpuModelPathBox.Text)
        ? _settings.GpuSpeechModelPath ?? NeMoSpeechDownload.DefaultDirectory : GpuModelPathBox.Text.Trim();
    private bool CanChangeGpuSpeech => !_closing && _speech == null && !_loading && !_busy && !HasPendingDictationOutput && !_recording &&
        _recordingStart == null && _download == null && !SpeechPausedForGaming && !_resourceTransitioning;

    private void InitializeGpuSpeech()
    {
        _gpuPreferencesInitializing = true;
        SpeechAccelerationBox.SelectedIndex = PreferGpu ? 0 : 1;
        GpuSpeechLatencyBox.SelectedIndex = GpuFast ? 1 : 0;
        GpuModelPathBox.Text = _modelLocations.ResolveGpu(_settings.GpuSpeechModelPath);
        _gpuPathEdited = false;
        _gpuPreferencesInitializing = false;
        RefreshGpuSpeech();
    }

    private bool AnySpeechModelReady(string directory) => ModelDownload.IsReady(directory) ||
        PreferGpu && TryGpuDirectory(out var gpuDirectory) && NeMoSpeechDownload.IsReady(gpuDirectory);

    private bool TryGpuDirectory(out string directory)
    {
        directory = SelectedGpuDirectory;
        try {
            if (!Path.IsPathFullyQualified(directory)) return false;
            directory = Path.GetFullPath(directory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private void RefreshGpuSpeech()
    {
        if (GpuBackendStatus == null) return;
        bool editable = CanChangeGpuSpeech;
        SpeechAccelerationBox.IsEnabled = GpuSpeechLatencyBox.IsEnabled = GpuModelPathBox.IsEnabled = editable;
        DownloadGpuModelButton.IsEnabled = editable && TryGpuDirectory(out _);
        CancelGpuDownloadButton.Visibility = _gpuDownloading ? Visibility.Visible : Visibility.Collapsed;
        CancelGpuDownloadButton.IsEnabled = _gpuDownloading && !_closing;
        if (_gpuDownloading) {
            DownloadButton.IsEnabled = ModelPathBox.IsEnabled = LoadButton.IsEnabled = false;
            return;
        }
        GpuBackendStatus.Text = _speech != null ? "Prepared: " + _speech.BackendDescription
            : _loading ? "Preparing local speech..."
            : SpeechPausedForGaming ? "Speech paused for gaming."
            : !TryGpuDirectory(out var directory) ? "Enter an absolute GPU model folder, such as D:\\Typr\\models\\nemotron-gpu."
            : !PreferGpu ? "CPU selected. GPU recognition is off."
            : NeMoSpeechDownload.IsReady(directory) ? "GPU model found. Prepare speech to check GPU availability."
            : "GPU model is optional. Automatic uses the CPU model until it is installed.";
    }

    private void GpuSpeechPreference_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _gpuPreferencesInitializing || SpeechAccelerationBox.SelectedIndex < 0 || GpuSpeechLatencyBox.SelectedIndex < 0) return;
        if (!CanChangeGpuSpeech) { InitializeGpuSpeech(); return; }
        PersistSettings(_settings with {
            SpeechAcceleration = SpeechAccelerationBox.SelectedIndex == 1 ? "CPU" : "Automatic",
            GpuSpeechLatency = GpuSpeechLatencyBox.SelectedIndex == 1 ? "Fast" : "Quality"
        });
        RefreshReadiness();
    }

    private void GpuModelPath_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_gpuPreferencesInitializing) { _gpuPathEdited = true; RefreshGpuSpeech(); }
    }

    private void GpuModelPath_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_initialized || _gpuPreferencesInitializing || !_gpuPathEdited || !CanChangeGpuSpeech || !TryGpuDirectory(out var directory)) return;
        if (_settings.GpuSpeechModelPath != directory) PersistSettings(_settings with { GpuSpeechModelPath = directory });
        _gpuPathEdited = false;
        RefreshReadiness();
    }

    private async void DownloadGpuModel_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeGpuSpeech || !TryGpuDirectory(out var directory)) return;
        using var download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = download; _gpuDownloading = true;
        SetSetupEnabled(false); RefreshReadiness();
        GpuBackendStatus.Text = "Downloading optional GPU model. You can pause and resume.";
        var progress = new Progress<double>(value => {
            if (!_closing && _gpuDownloading) { GpuDownloadProgress.Value = value; GpuBackendStatus.Text = $"Downloading GPU model · {value:P0}"; }
        });
        try {
            await NeMoSpeechDownload.DownloadAsync(directory, progress, download.Token, _modelLocations);
            if (!_closing) {
                PersistSettings(_settings with { GpuSpeechModelPath = directory });
                GpuBackendStatus.Text = "GPU download verified. Prepare speech to use Automatic acceleration.";
                StatusText.Text = "GPU model installed. Your microphone is off.";
            }
        }
        catch (OperationCanceledException) { if (!_closing) StatusText.Text = "GPU download paused. Choose Download GPU model to resume."; }
        catch (Exception ex) { if (!_closing) StatusText.Text = "GPU download couldn't finish: " + ex.Message; }
        finally {
            _download = null; _gpuDownloading = false;
            if (!_closing) { SetSetupEnabled(_speech == null); RefreshReadiness(); }
        }
    }

    private void CancelGpuDownload_Click(object sender, RoutedEventArgs e) { if (_gpuDownloading) _download?.Cancel(); }
}
