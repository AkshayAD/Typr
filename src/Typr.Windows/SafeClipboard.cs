using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Typr.Windows;

internal sealed record ClipboardSnapshot(IReadOnlyDictionary<uint, byte[]> Formats);
internal sealed record ClipboardReplacement(bool Replaced, ClipboardSnapshot Previous, uint Sequence, string Reason);
internal sealed class ClipboardCleanupCanceledException(ClipboardReplacement replacement, CancellationToken cancellationToken)
    : OperationCanceledException("Insertion cancelled, but the previous clipboard couldn't be restored.", cancellationToken)
{
    public ClipboardReplacement Replacement { get; } = replacement;
}
internal enum ClipboardRestoreResult { Restored, ChangedByUser, BusyOrFailed }

/// <summary>Materializes restorable clipboard formats before changing the clipboard.</summary>
internal static class SafeClipboard
{
    const uint UnicodeText = 13;
    const int MaximumBytes = 64 * 1024 * 1024;
    static readonly SemaphoreSlim ReplacementGate = new(1, 1);
    const string Unavailable = "The clipboard is busy or unavailable. Your result is in Typr; use Copy text when ready.";
    const string TimedOut = "The clipboard didn't respond in time. Your result is in Typr; use Copy text when ready.";
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)] static extern uint EnumClipboardFormats(uint previous);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint memory);

    static nint Owner
    {
        get {
            var application = Application.Current;
            if (application == null || !application.Dispatcher.CheckAccess()) return 0;
            var window = application.MainWindow;
            if (window == null) return 0;
            var handle = new WindowInteropHelper(window).Handle;
            return NativeMethods.IsWindow(handle) ? handle : 0;
        }
    }

    // Also used by isolated native integration tests to preserve the user's clipboard.
    public static bool TryCapture(out ClipboardSnapshot snapshot)
    {
        snapshot = new(new Dictionary<uint, byte[]>());
        if (!OpenClipboard(Owner)) return false;
        try { return CaptureLocked(out snapshot); }
        finally { CloseClipboard(); }
    }

    public static Task<ClipboardReplacement> TryReplacePlainTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture the UI-owned HWND before moving all native clipboard work off the dispatcher.
        var owner = Owner;
        if (owner == 0 || !ReplacementGate.Wait(0)) return Task.FromResult(Failure(Unavailable));
        return RunTransactionAsync(permission => {
            try { return ReplacePlainText(text, owner, permission); }
            finally { ReplacementGate.Release(); }
        }, replacement => RestoreIfOwned(replacement.Previous, replacement.Sequence, owner), cancellationToken);
    }

    static ClipboardReplacement Failure(string reason) => new(false, new(new Dictionary<uint, byte[]>()), 0, reason);

    // A delayed renderer may not return when our deadline expires. Revoke its write
    // permission before returning; it can finish reading later but cannot mutate data.
    internal static async Task<ClipboardReplacement> RunTransactionAsync(
        Func<ClipboardWritePermission, ClipboardReplacement> replace,
        Func<ClipboardReplacement, ClipboardRestoreResult> rollback,
        CancellationToken cancellationToken, int timeoutMs = 500)
    {
        var permission = new ClipboardWritePermission(cancellationToken, timeoutMs);
        var worker = Task.Run(() => replace(permission));
        try {
            var result = await worker.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && !permission.Expired) return result;
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }

        string reason = TimedOut;
        ClipboardReplacement? failedCleanup = null;
        if (permission.AbandonBeforeWrite()) {
            // Observe a renderer failure even when the UI has already recovered.
            _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        } else {
            // Once native mutation begins it cannot be cancelled safely. Finish and
            // restore before reporting timeout/cancellation, without blocking the UI.
            var result = await worker.ConfigureAwait(false);
            if (result.Replaced && await Task.Run(() => rollback(result)).ConfigureAwait(false) == ClipboardRestoreResult.BusyOrFailed) {
                reason += " The previous clipboard couldn't be restored.";
                failedCleanup = result;
            }
        }
        if (cancellationToken.IsCancellationRequested) {
            if (failedCleanup != null) throw new ClipboardCleanupCanceledException(failedCleanup, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return Failure(reason);
    }

    static ClipboardReplacement ReplacePlainText(string text, nint owner, ClipboardWritePermission permission)
    {
        // EmptyClipboard with a null owner cannot subsequently receive SetClipboardData.
        if (!permission.CanRead || !OpenClipboard(owner)) return Failure(Unavailable);
        ClipboardSnapshot snapshot;
        try {
            if (!CaptureLocked(out snapshot, () => permission.CanRead))
                return Failure("Typr cannot safely preserve this clipboard format. Your result is in Typr; use Copy text when ready.");
            bool writeStarted = false;
            if (!WriteLocked(new Dictionary<uint, byte[]> { [UnicodeText] = Encoding.Unicode.GetBytes(text + "\0") },
                () => writeStarted = permission.TryBeginWrite())) {
                if (writeStarted) WriteLocked(snapshot.Formats);
                return Failure(Unavailable);
            }
        }
        finally { CloseClipboard(); }
        // Windows finalizes synthesized formats/sequence when the clipboard closes.
        // Capturing the number under the write lock makes our own update appear stale.
        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        if (GetClipboardOwner() != owner) return Failure(Unavailable);
        return new(true, snapshot, sequence, "");
    }

    static bool CaptureLocked(out ClipboardSnapshot snapshot, Func<bool>? canRead = null)
    {
        var formats = new Dictionary<uint, byte[]>();
        snapshot = new(formats);
        long total = 0;
        uint previous = 0;
        while (true) {
            if (canRead != null && !canRead()) return false;
            Marshal.SetLastPInvokeError(0);
            uint format = EnumClipboardFormats(previous);
            if (format == 0) return Marshal.GetLastPInvokeError() == 0;
            previous = format;
            // Windows synthesizes CF_BITMAP from a retained DIB. GDI/metafile handles
            // otherwise need specialized ownership; leave that clipboard untouched.
            if (format == 2 && (IsClipboardFormatAvailable(8) || IsClipboardFormatAvailable(17))) continue;
            if (format is 2 or 3 or 9 or 14 or 128 or 130 or 131 or 142 || formats.Count >= 128) return false;
            var handle = GetClipboardData(format);
            if (handle == 0) return false;
            var length = GlobalSize(handle);
            if (length == 0 || length > MaximumBytes || total + (long)length > MaximumBytes) return false;
            var pointer = GlobalLock(handle);
            if (pointer == 0) return false;
            try {
                var bytes = new byte[(int)length];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                formats.Add(format, bytes);
                total += bytes.Length;
            }
            finally { GlobalUnlock(handle); }
        }
    }

    public static ClipboardRestoreResult RestoreIfOwned(ClipboardSnapshot snapshot, uint sequence)
        => RestoreIfOwned(snapshot, sequence, Owner);

    public static Task<ClipboardRestoreResult> RestoreIfOwnedAsync(ClipboardSnapshot snapshot, uint sequence)
    {
        var owner = Owner;
        return Task.Run(() => RestoreIfOwned(snapshot, sequence, owner));
    }

    static ClipboardRestoreResult RestoreIfOwned(ClipboardSnapshot snapshot, uint sequence, nint owner)
    {
        if (owner == 0 || !OpenClipboard(owner)) return ClipboardRestoreResult.BusyOrFailed;
        try {
            if (NativeMethods.GetClipboardSequenceNumber() != sequence) return ClipboardRestoreResult.ChangedByUser;
            return WriteLocked(snapshot.Formats) ? ClipboardRestoreResult.Restored : ClipboardRestoreResult.BusyOrFailed;
        }
        finally { CloseClipboard(); }
    }

    static bool WriteLocked(IReadOnlyDictionary<uint, byte[]> formats, Func<bool>? beginWrite = null)
    {
        // Allocate every format before emptying, so allocation failure leaves the
        // original clipboard untouched. Windows takes ownership only after Set succeeds.
        var handles = new Dictionary<uint, nint>();
        try {
            foreach (var (format, bytes) in formats) {
                var memory = GlobalAlloc(0x42, (nuint)bytes.Length);
                if (memory == 0) return false;
                handles.Add(format, memory);
                var pointer = GlobalLock(memory);
                if (pointer == 0) return false;
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { GlobalUnlock(memory); }
            }
            if (beginWrite != null && !beginWrite()) return false;
            if (!EmptyClipboard()) return false;
            foreach (var format in handles.Keys.ToArray()) {
                if (SetClipboardData(format, handles[format]) == 0) return false;
                handles.Remove(format);
            }
            return true;
        }
        finally { foreach (var memory in handles.Values) GlobalFree(memory); }
    }
}

internal sealed class ClipboardWritePermission(CancellationToken cancellationToken, int timeoutMs)
{
    readonly object gate = new();
    readonly long expires = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (timeoutMs / 1000d));
    bool writing, abandoned;
    public bool Expired => Stopwatch.GetTimestamp() >= expires;
    public bool CanRead { get { lock (gate) return !abandoned && !cancellationToken.IsCancellationRequested && !Expired; } }
    public bool TryBeginWrite()
    {
        lock (gate) {
            if (abandoned || cancellationToken.IsCancellationRequested || Expired) return false;
            writing = true; return true;
        }
    }
    public bool AbandonBeforeWrite()
    {
        lock (gate) {
            if (writing) return false;
            abandoned = true; return true;
        }
    }
}
