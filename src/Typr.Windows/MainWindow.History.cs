using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using Typr.Core;
using Typr.Infrastructure;

namespace Typr.Windows;

public partial class MainWindow
{
    private CancellationTokenSource? _historyOperation;
    private WhisperRetranscriber? _retranscriber;
    private SoundPlayer? _historyPlayer;
    private HistoryPreview? _historyPreview;
    private sealed record HistoryPreview(Guid EntryId, string Text, string Model, string Kind, string? Provider, string? Effort);
    private WhisperRetranscriber Retranscriber => _retranscriber ??= new(Path.Combine(_localDirectory, "models"));

    private void RefreshHistorySummary()
    {
        if (HistorySummary == null) return;
        HistorySummary.Text = $"{_history.Entries.Count} saved · {_history.Entries.Count(entry => AudioPath(entry) != null)} with audio · on this PC";
    }
    private string? AudioPath(TranscriptEntry entry)
    {
        if (entry.AudioFileName == null) return null;
        var path = Path.Combine(_localDirectory, "audio", entry.AudioFileName);
        return File.Exists(path) ? path : null;
    }
    private void UpdateHistoryDetails(TranscriptEntry? entry)
    {
        if (HistoryMetadata == null) return;
        _historyPreview = null; HistoryPreviewPanel.Visibility = Visibility.Collapsed;
        HistoryMetadata.Text = entry == null ? "Select a transcript to see its original words, delivery and versions." :
            $"{entry.DisplayDate} · {entry.Provider ?? "Local"} · {entry.Model ?? "Speech recognition"}{(entry.Reasoning == null ? "" : " · " + entry.Reasoning)}\n{entry.DeliveryStatus} · {TimeSpan.FromMilliseconds(entry.DurationMs):mm\\:ss} recorded · {entry.Revisions.Count} previous versions";
        HistoryRawDetail.Text = entry?.RawText.Length > 0 ? entry.RawText : "The original recognition wasn't retained for this older entry.";
        HistoryAudioStatus.Text = entry == null ? "Select an entry to inspect its recording." : AudioPath(entry) != null ? "Recording available locally. Re-transcription runs on this PC." : "No retained recording. Enable audio retention in Settings for future recordings.";
        bool idle = _historyOperation == null;
        RePolishHistoryButton.IsEnabled = PasteHistoryButton.IsEnabled = entry != null && idle;
        RestoreHistoryButton.IsEnabled = entry?.Revisions.Count > 0 && idle;
        ListenHistoryButton.IsEnabled = RetranscribeHistoryButton.IsEnabled = entry != null && AudioPath(entry) != null && idle;
    }
    private void SelectHistory(Guid id)
    {
        RefreshHistory(); HistoryList.SelectedItem = _history.Entries.FirstOrDefault(entry => entry.Id == id);
    }
    private void RetranscriptionModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_companionInitialized || _companionInitializing || RetranscriptionModelBox.SelectedValue is not string id) return;
        PersistSettings(_settings with { RetranscriptionModel = id });
        HistoryOperationStatus.Text = Retranscriber.IsReady(id) ? "Selected model is downloaded. Integrity is verified again before use." : "Download the selected model before re-transcribing.";
    }
    private async void DownloadRetranscription_Click(object sender, RoutedEventArgs e)
    {
        if (_historyOperation != null) return;
        await RunHistoryOperationAsync(async token =>
        {
            string id = _settings.RetranscriptionModel;
            var progress = new Progress<double>(value => { if (!_closing) { HistoryProgress.Value = value; HistoryOperationStatus.Text = $"Downloading local model · {value:P0}"; } });
            await Retranscriber.DownloadAsync(id, progress, token);
            if (!_closing) HistoryOperationStatus.Text = "Model downloaded and verified. Select a recording and choose Re-transcribe.";
        });
    }
    private async void RePolishHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not TranscriptEntry entry) return;
        var settings = _settings;
        await RunHistoryOperationAsync(async token =>
        {
            HistoryOperationStatus.Text = "Polishing a preview with your selected provider and model…";
            var original = entry.RawText.Length > 0 ? entry.RawText : entry.Text;
            string prepared = OutputPersonalizer.Transform(original, true, settings.Vocabulary, settings.Snippets);
            var polished = await PolishTextAsync(prepared, settings, token, force: true);
            token.ThrowIfCancellationRequested();
            if (polished.Model == null) { HistoryOperationStatus.Text = polished.Note + " The saved transcript is unchanged."; return; }
            ShowHistoryPreview(new(entry.Id, polished.Text, polished.Model, "Re-polish", polished.Provider, polished.Effort));
            HistoryOperationStatus.Text = "Preview ready. Review it, then apply to save a new version.";
        });
    }
    private async void RetranscribeHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not TranscriptEntry entry || AudioPath(entry) is not { } path) return;
        var settings = _settings;
        if (!Retranscriber.IsReady(settings.RetranscriptionModel)) { HistoryOperationStatus.Text = "Download the selected local model first."; return; }
        await RunHistoryOperationAsync(async token =>
        {
            HistoryOperationStatus.Text = "Re-transcribing retained audio locally. A CPU decode may take a while…";
            string raw = await Retranscriber.TranscribeAsync(path, settings.RetranscriptionModel, settings.CpuThreads, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw)) { HistoryOperationStatus.Text = "No speech detected. The saved transcript is unchanged."; return; }
            string prepared = OutputPersonalizer.Transform(raw, true, settings.Vocabulary, settings.Snippets);
            var polished = await PolishTextAsync(prepared, settings, token);
            token.ThrowIfCancellationRequested();
            ShowHistoryPreview(new(entry.Id, polished.Text, settings.RetranscriptionModel + (polished.Model == null ? "" : " + " + polished.Model), "Re-transcribe", polished.Provider, polished.Effort));
            HistoryOperationStatus.Text = "Re-transcription preview ready. " + polished.Note;
        });
    }
    private async Task RunHistoryOperationAsync(Func<CancellationToken, Task> action)
    {
        if (SpeechPausedForGaming) { HistoryOperationStatus.Text = "Speech processing is paused for gaming. Choose Everyday to resume."; return; }
        if (_historyOperation != null || _closing || _busy || _recording) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _historyOperation = operation;
        SetHistoryBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!_closing) HistoryOperationStatus.Text = "Cancelled. Your saved transcript is unchanged."; }
        catch (Exception ex) { if (!_closing) HistoryOperationStatus.Text = "Couldn't finish: " + ex.Message; }
        finally { _historyOperation = null; if (!_closing) { SetHistoryBusy(false); RefreshReadiness(); } }
    }
    private void SetHistoryBusy(bool busy)
    {
        HistoryList.IsEnabled = HistorySearchBox.IsEnabled = RetranscriptionModelBox.IsEnabled = DownloadRetranscriptionButton.IsEnabled = !busy;
        CancelHistoryButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RePolishHistoryButton.IsEnabled = PasteHistoryButton.IsEnabled = HistoryList.SelectedItem != null && !busy;
        RestoreHistoryButton.IsEnabled = HistoryList.SelectedItem is TranscriptEntry entry && entry.Revisions.Count > 0 && !busy;
        ListenHistoryButton.IsEnabled = RetranscribeHistoryButton.IsEnabled = HistoryList.SelectedItem is TranscriptEntry audio && AudioPath(audio) != null && !busy;
        RemoveHistoryButton.IsEnabled = HistoryList.SelectedItem != null && !busy;
        ClearHistoryButton.IsEnabled = !busy;
    }
    private void ShowHistoryPreview(HistoryPreview preview)
    {
        if (_closing || HistoryList.SelectedItem is not TranscriptEntry entry || entry.Id != preview.EntryId) return;
        _historyPreview = preview; HistoryPreviewText.Text = preview.Text;
        HistoryPreviewPanel.Visibility = Visibility.Visible;
    }
    private void ApplyHistoryPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_historyPreview is not { } preview || !_historyWritable || _historyOperation != null) return;
        try
        {
            _history.Revise(preview.EntryId, HistoryPreviewText.Text, preview.Model, preview.Kind, preview.Provider, preview.Effort);
            SelectHistory(preview.EntryId); HistoryOperationStatus.Text = "New version saved. Restore previous returns to the prior text.";
        }
        catch (Exception) { HistoryOperationStatus.Text = "Couldn't save the version. The existing transcript is preserved."; }
    }
    private void DiscardHistoryPreview_Click(object sender, RoutedEventArgs e) { _historyPreview = null; HistoryPreviewPanel.Visibility = Visibility.Collapsed; }
    private void RestoreHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not TranscriptEntry entry || !_historyWritable || _historyOperation != null) return;
        try { _history.RestorePrevious(entry.Id); SelectHistory(entry.Id); HistoryOperationStatus.Text = "Previous version restored."; }
        catch (Exception) { HistoryOperationStatus.Text = "Couldn't restore the version. Your saved transcript is preserved."; }
    }
    private void CancelHistory_Click(object sender, RoutedEventArgs e) => _historyOperation?.Cancel();
    private void ListenHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not TranscriptEntry entry || AudioPath(entry) is not { } path) return;
        try { _historyPlayer?.Stop(); _historyPlayer?.Dispose(); _historyPlayer = new SoundPlayer(path); _historyPlayer.Play(); }
        catch (Exception) { HistoryOperationStatus.Text = "Couldn't play that local recording."; }
    }
    private void StopHistoryAudio_Click(object sender, RoutedEventArgs e) => _historyPlayer?.Stop();
    private async void PasteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not TranscriptEntry entry || _busy || _recording) return;
        await RunHistoryOperationAsync(async token =>
        {
            var target = await _externalTargets.RefreshAsync(token);
            if (target == null) { HistoryOperationStatus.Text = "Click a writable field in another app, return here, then choose Paste selected."; return; }
            _pendingDelivery = _delivery.DeliverAsync(entry.Text, target, token, restoreFocus: true);
            var result = await _pendingDelivery;
            if (!_closing) HistoryOperationStatus.Text = result.Message;
        });
    }
}
