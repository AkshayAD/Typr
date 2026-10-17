using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Typr.Windows;

internal sealed record DeliveryTarget(nint Window, int[]? RuntimeId, string? Value, bool Readable, bool Password, bool Editable = false);
internal sealed record DeliveryResult(string Message, bool Confirmed);

/// <summary>Fails closed on focus uncertainty. No automatic second delivery after an unconfirmed paste.</summary>
internal sealed class ClipboardDelivery(string? logDirectory = null)
{
    static readonly SemaphoreSlim CaptureGate = new(1, 1);
    public static DeliveryTarget CaptureTarget(nint window)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element == null || NativeMethods.GetForegroundWindow() != window) return new(window, null, null, false, false);
            var password = !KnownNotPassword(element);
            var runtimeId = element.GetRuntimeId();
            var editable = !password && IsEditable(element);
            var value = password ? null : ReadValue(element);
            // UIA calls may cross processes. Validate focus again after slow value reads.
            var current = AutomationElement.FocusedElement;
            if (current == null || NativeMethods.GetForegroundWindow() != window ||
                !runtimeId.SequenceEqual(current.GetRuntimeId()))
                return new(window, null, null, false, current == null || !KnownNotPassword(current));
            if (!KnownNotPassword(current)) return new(window, runtimeId, null, false, true);
            return new(window, runtimeId, value, value != null, password, editable);
        }
        catch { return new(window, null, null, false, false); }
    }

    public static async Task<DeliveryTarget> CaptureTargetAsync(nint window, CancellationToken cancellationToken, int timeoutMs = 500)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CaptureGate.Wait(0)) return new(window, null, null, false, false);
        var capture = Task.Run(() => { try { return CaptureTarget(window); } finally { CaptureGate.Release(); } }, CancellationToken.None);
        try
        {
            return await capture
                .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)), cancellationToken);
        }
        catch (TimeoutException) { return new(window, null, null, false, false); }
    }

    private static string? ReadValue(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(65_536);
        return null;
    }

    private static bool KnownNotPassword(AutomationElement element) =>
        element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is bool password && !password;

    private static bool IsEditable(AutomationElement element)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true) is not true) return false;
        var controlType = element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true);
        if (!Equals(controlType, ControlType.Edit) && !Equals(controlType, ControlType.Document) && !Equals(controlType, ControlType.ComboBox)) return false;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out _))
            return element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty, true) is bool readOnlyValue && !readOnlyValue;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) {
            var pattern = (TextPattern)text;
            var readOnly = pattern.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            if (readOnly is bool value) return !value;
            // Browser document providers can expose a mixed document while the caret
            // sits in an editable range. Require explicit writable selection attributes.
            var selection = pattern.GetSelection();
            return element.Current.HasKeyboardFocus && selection.Length > 0 && selection.All(range =>
                range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is bool selectedReadOnly && !selectedReadOnly);
        }
        return false;
    }

    private static bool StillFocusedEditableTarget(DeliveryTarget target)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element == null || target.RuntimeId == null ||
                !target.RuntimeId.SequenceEqual(element.GetRuntimeId()) || !KnownNotPassword(element) || !IsEditable(element))
                return false;
            // Re-read focus after potentially slow cross-process property queries.
            var current = AutomationElement.FocusedElement;
            return current != null && target.RuntimeId.SequenceEqual(current.GetRuntimeId()) &&
                NativeMethods.GetForegroundWindow() == target.Window;
        }
        catch { return false; }
    }

    private static bool SameElement(DeliveryTarget expected, DeliveryTarget actual) => expected.Window == actual.Window &&
        expected.RuntimeId != null && actual.RuntimeId != null && expected.RuntimeId.SequenceEqual(actual.RuntimeId);

    private sealed record PasteAttempt(uint Sent, nint Foreground, string? Error);

    private static async Task<PasteAttempt> SendPasteWithDeadlineAsync(DeliveryTarget target, uint clipboardSequence, Action<long> markAttempt, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(500);
        var token = deadline.Token;
        long expires = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
        var work = Task.Run(() =>
        {
            var inputs = new[] { NativeMethods.Key(0x11), NativeMethods.Key(0x56), NativeMethods.Key(0x56, true), NativeMethods.Key(0x11, true) };
            // UIA is isolated on an MTA worker. A stalled provider must never cause a late paste.
            if (!StillFocusedEditableTarget(target)) return new PasteAttempt(0, 0, "Focus couldn't be confirmed.");
            token.ThrowIfCancellationRequested();
            var foreground = NativeMethods.GetForegroundWindow();
            if (Stopwatch.GetTimestamp() >= expires) return new PasteAttempt(0, foreground, "The insertion deadline expired.");
            if (NativeMethods.ModifiersHeld()) return new PasteAttempt(0, foreground, "Keyboard modifiers changed before insertion.");
            if (foreground != target.Window) return new PasteAttempt(0, foreground, "Focus changed before insertion.");
            if (NativeMethods.GetClipboardSequenceNumber() != clipboardSequence)
                return new PasteAttempt(0, foreground, "The clipboard changed before insertion.");
            markAttempt(Stopwatch.GetTimestamp());
            token.ThrowIfCancellationRequested();
            // No await or UIA call between these final guards and the sole text delivery.
            uint count = NativeMethods.SendKeys(inputs);
            if (count != inputs.Length) NativeMethods.SendKeys(NativeMethods.Key(0x56, true), NativeMethods.Key(0x11, true));
            return new PasteAttempt(count, foreground, null);
        }, CancellationToken.None);
        try { return await work.WaitAsync(token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            deadline.Cancel();
            return new PasteAttempt(0, 0, "The focused app didn't respond before the paste deadline.");
        }
    }

    public async Task<DeliveryResult> DeliverAsync(string text, DeliveryTarget target, CancellationToken cancellationToken, bool restoreFocus = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        text = text.TrimEnd('\r', '\n');
        if (text.Length == 0) return new("No speech was detected.", false);
        var foregroundBefore = NativeMethods.GetForegroundWindow();
        uint sent = 0; int expectedInputs = 0; string outcome = "not-delivered"; nint foregroundAtPaste = 0;
        bool modifiers = NativeMethods.ModifiersHeld();
        ClipboardSnapshot previous = new(new Dictionary<uint, byte[]>());
        uint clipboardSequence = 0;
        bool clipboardReplaced = false;
        bool clipboardRestored = false;
        long attemptedAt = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (target.Password || !target.Editable || target.RuntimeId == null) {
                outcome = "unconfirmed-target";
                return new("Automatic paste requires a confirmed, writable, non-password field. Your result is in Typr; use Copy text when ready.", false);
            }
            while (NativeMethods.ModifiersHeld() && stopwatch.ElapsedMilliseconds < 2500)
                await Task.Delay(20, cancellationToken);
            modifiers = NativeMethods.ModifiersHeld();
            if (modifiers) { outcome = "modifiers-held"; return new("Shortcut keys are still held. Your result is in Typr; use Copy text when ready.", false); }
            cancellationToken.ThrowIfCancellationRequested();
            var foreground = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
            if (!NativeMethods.IsWindow(target.Window) ||
                foreground != target.Window && (!restoreFocus || foregroundProcess != Environment.ProcessId || !FocusWindow(target.Window))) {
                outcome = "focus-unavailable";
                return new("Couldn't focus the destination. Your result is in Typr; use Copy text when ready.", false);
            }
            await Task.Delay(50, cancellationToken);
            var before = await CaptureTargetAsync(target.Window, cancellationToken);
            if (before.Password || !before.Editable || !SameElement(target, before)) {
                outcome = "target-changed";
                return new("The destination changed or isn't confirmed writable. Your result is in Typr; use Copy text when ready.", false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.ModifiersHeld() || NativeMethods.GetForegroundWindow() != target.Window) {
                outcome = "focus-or-modifiers-changed";
                return new("Focus or shortcut keys changed. Your result is in Typr; use Copy text when ready.", false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeClipboard.TryReplacePlainText(text, out previous, out clipboardSequence, out var clipboardReason)) {
                outcome = "clipboard-unavailable";
                return new(clipboardReason, false);
            }
            clipboardReplaced = true;
            outcome = "copied";
            if (Marshal.SizeOf<NativeMethods.Input>() != 40)
                throw new InvalidOperationException("The keyboard input layout is not valid for Windows x64.");
            expectedInputs = 4;
            var attempt = await SendPasteWithDeadlineAsync(before, clipboardSequence,
                timestamp => Interlocked.Exchange(ref attemptedAt, timestamp), cancellationToken);
            sent = attempt.Sent; foregroundAtPaste = attempt.Foreground;
            if (attempt.Error != null) {
                outcome = "paste-guard-rejected";
                return new(attempt.Error + " Text remains available in Typr. Check the field before trying again.", false);
            }
            if (sent != expectedInputs)
            {
                // Synthetic modifiers were released by the worker; never retry a partial send.
                outcome = "blocked-or-partial";
                return new("Insertion was blocked or incomplete. Your result is in Typr; check the field before trying again.", false);
            }
            var pasteTimer = Stopwatch.StartNew();
            while (pasteTimer.ElapsedMilliseconds < 400)
            {
                await Task.Delay(40, cancellationToken);
                var after = await CaptureTargetAsync(target.Window, cancellationToken, 400 - (int)pasteTimer.ElapsedMilliseconds);
                if (!SameElement(before, after)) break;
                if (before.Readable && after.Readable && after.Value != before.Value && after.Value != null &&
                    Occurrences(Normalise(after.Value), Normalise(text)) > Occurrences(Normalise(before.Value!), Normalise(text)))
                {
                    outcome = "verified";
                    // Keep the clipboard alive long enough for slow apps and restore only if still ours.
                    // This is cleanup for an already-confirmed paste. Cancellation must not
                    // strand the dictation on the clipboard or erase the previous clipboard.
                    if (clipboardReplaced) await Task.Delay(Math.Max(0, 800 - (int)pasteTimer.ElapsedMilliseconds));
                    if (clipboardReplaced && SafeClipboard.RestoreIfOwned(previous, clipboardSequence) == ClipboardRestoreResult.BusyOrFailed)
                    {
                        outcome = "verified-clipboard-restore-failed";
                        return new("Pasted and verified. The previous clipboard couldn't be restored.", true);
                    }
                    clipboardRestored = true;
                    outcome = "verified";
                    return new("Pasted and verified.", true);
                }
            }
            outcome = "inconclusive";
            return new("Insertion sent, but not confirmed. Your result is in Typr. Check the field before trying again.", false);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            return new("Couldn't finish paste: " + ex.Message + " Your result is still in Typr.", false);
        }
        finally
        {
            if (clipboardReplaced && !clipboardRestored) {
                long timestamp = Interlocked.Read(ref attemptedAt);
                if (timestamp != 0) {
                    double elapsedMs = (Stopwatch.GetTimestamp() - timestamp) * 1000d / Stopwatch.Frequency;
                    await Task.Delay(Math.Max(0, 800 - (int)elapsedMs));
                }
                if (SafeClipboard.RestoreIfOwned(previous, clipboardSequence) == ClipboardRestoreResult.BusyOrFailed)
                    outcome += "-clipboard-restore-failed";
            }
            try
            {
                var directory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typr", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "delivery.log"), $"{DateTimeOffset.Now:O} before=0x{foregroundBefore:X} atPaste=0x{foregroundAtPaste:X} SendInput={sent}/{expectedInputs} modifiers={modifiers} result={outcome}\n");
            }
            catch { }
        }
    }

    private static string Normalise(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\r', '\n');

    private static int Occurrences(string source, string value)
    {
        if (value.Length == 0) return 0;
        int count = 0, position = 0;
        while ((position = source.IndexOf(value, position, StringComparison.Ordinal)) >= 0)
        {
            count++; position += value.Length;
        }
        return count;
    }

    private static bool FocusWindow(nint hwnd)
    {
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;
        NativeMethods.SetForegroundWindow(hwnd);
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;
        var current = NativeMethods.GetCurrentThreadId();
        var other = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        bool attached = false;
        try
        {
            attached = other != current && NativeMethods.AttachThreadInput(current, other, true);
            NativeMethods.SetForegroundWindow(hwnd);
            return NativeMethods.GetForegroundWindow() == hwnd;
        }
        finally { if (attached) NativeMethods.AttachThreadInput(current, other, false); }
    }
}
