using System.Windows;
using Typr.Core;
using Typr.Infrastructure;

namespace Typr.Windows;

public partial class MainWindow
{
    private sealed record PendingDictationOutput(CancellationTokenSource Operation, int Generation,
        AppSettings Settings, bool Practice, bool Clean, bool RestoreFocus, Task<DeliveryTarget>? Target,
        long DurationMs, Task<SpeechRecordingResult> Recognition)
    {
        public string Phase { get; set; } = "Recognizing…";
    }
    private readonly List<PendingDictationOutput> _dictationOutputs = [];
    private Task _dictationOutputTail = Task.CompletedTask;
    private readonly SemaphoreSlim _dictationPolishGate = new(1, 1);
    private TaskCompletionSource? _captureIdleWaiter;
    private bool HasPendingDictationOutput => _dictationOutputs.Count > 0;
    private volatile bool _captureInProgressForDelivery;

    private void SignalCaptureIdle()
    {
        var waiter = _captureIdleWaiter; _captureIdleWaiter = null;
        waiter?.TrySetResult();
    }
    private async Task WaitForCaptureIdleAsync(CancellationToken token)
    {
        while (_recording || _recordingStart != null)
        {
            _captureIdleWaiter ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _captureIdleWaiter.Task.WaitAsync(token);
        }
        token.ThrowIfCancellationRequested();
    }
    private void SetOutputPhase(PendingDictationOutput output, string phase, string? caption = null)
    {
        output.Phase = phase;
        if (_closing || _dictationOutputs.FirstOrDefault() != output) return;
        _processingPhase = phase;
        if (_recording || _recordingStart != null) return;
        CaptureDuration.Text = phase;
        if (caption != null) UpdateLiveBarCaption(caption);
        RefreshReadiness();
    }
    private async Task<(SpeechRecordingResult Recording, PolishOutcome Polished)> PrepareDictationOutputAsync(PendingDictationOutput output)
    {
        var token = output.Operation.Token;
        var recording = await output.Recognition;
        token.ThrowIfCancellationRequested();
        var prepared = OutputPersonalizer.Transform(recording.Text, output.Clean, output.Settings.Vocabulary, output.Settings.Snippets);
        if (output.Clean && output.Settings.AiPolishEnabled && !string.IsNullOrWhiteSpace(prepared))
            SetOutputPhase(output, "Polishing…", prepared);
        PolishOutcome polished;
        // A queued utterance's provider timeout begins when its turn starts,
        // rather than expiring while the previous polish owns the CLI.
        await _dictationPolishGate.WaitAsync(token);
        try { polished = output.Clean ? await PolishTextAsync(prepared, output.Settings, token)
            : new PolishOutcome(prepared, null, null, null, "Raw"); }
        finally { _dictationPolishGate.Release(); }
        token.ThrowIfCancellationRequested();
        return (recording, polished);
    }
    private async Task CompleteDictationOutputAsync(PendingDictationOutput output, Task previous)
    {
        // Recognition/polish can progress while the next microphone is open.
        // Insertion (including clipboard restoration) stays in speaking order.
        var preparedTask = PrepareDictationOutputAsync(output);
        try
        {
            await previous;
            var (recording, polished) = await preparedTask;
            var token = output.Operation.Token;
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(polished.Text))
            {
                if (!_recording && _recordingStart == null) { StatusText.Text = "No speech detected. Your previous transcript is still available. Check the input and try again."; _overlay.Cancel(); }
                return;
            }
            _latest = polished.Text;
            ResultBox.Text = polished.Text; ResultBox.Visibility = Visibility.Visible;
            ResultEmpty.Visibility = Visibility.Collapsed; CopyResultButton.IsEnabled = true;
            string deliveryStatus = output.Practice ? "Practice" : "Paste off";
            SetOutputPhase(output, "Ready to paste…", polished.Text);
            if (output.Practice)
            {
                await WaitForCaptureIdleAsync(token);
                PracticeBox.SelectedText = polished.Text;
                PracticeBox.CaretIndex = PracticeBox.SelectionStart + polished.Text.Length;
                StatusText.Text = "Added to your practice field.";
                _overlay.Complete(polished.Text);
            }
            else if (output.Settings.AutoPasteEnabled)
            {
                var target = await output.Target!;
                DeliveryResult result;
                do
                {
                    await WaitForCaptureIdleAsync(token);
                    SetOutputPhase(output, "Placing your words…");
                    _pendingDelivery = _delivery.DeliverAsync(polished.Text, target, token, output.RestoreFocus,
                        captureActive: () => _captureInProgressForDelivery);
                    result = await _pendingDelivery;
                } while (result.Deferred);
                if (!_recording && _recordingStart == null)
                {
                    StatusText.Text = result.Message;
                    if (result.Confirmed) _overlay.Complete(polished.Text);
                    else _overlay.Error("Text is ready. Use Copy text in Typr.");
                }
                deliveryStatus = result.Confirmed ? "Inserted" : "Copy available";
            }
            else if (!_recording && _recordingStart == null)
            {
                StatusText.Text = "Automatic paste is off. Your finished text is ready to copy or paste from the bar.";
                _overlay.Complete(polished.Text);
            }
            if (!_recording && _recordingStart == null && polished.Note.Length > 0 && polished.Note != "Raw") StatusText.Text += " " + polished.Note;
            if (output.Settings.SaveHistory) SaveHistory(polished.Text, output.Clean ? "Clean" : "Raw", recording.Text, recording.AudioFileName,
                polished.Provider, polished.Model, polished.Effort, deliveryStatus, output.DurationMs);
        }
        catch (OperationCanceledException)
        {
            if (!_closing && !_recording && _recordingStart == null) { _overlay.Cancel(); StatusText.Text = "Cancelled pending output. Text already inserted remains."; }
        }
        catch (Exception ex)
        {
            if (!_closing && !_recording && _recordingStart == null) { StatusText.Text = "Dictation couldn't finish: " + ex.Message; _overlay.Error("Check Typr. Completed text is available to copy."); }
        }
        finally
        {
            // Observe a preparation failure even if an earlier job failed first.
            try { await preparedTask; } catch { }
            _dictationOutputs.Remove(output); output.Operation.Dispose();
            if (!_closing)
            {
                if (HasPendingDictationOutput) _processingPhase = _dictationOutputs[0].Phase;
                else if (!_recording && _recordingStart == null) { CaptureDuration.Text = "Hold Ctrl + Alt to talk"; ShortcutStatus.Text = "Ready for your next thought. Hold Ctrl + Alt in a writable field."; }
                RefreshReadiness();
            }
        }
    }
}
