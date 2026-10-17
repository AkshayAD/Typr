using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Typr.Windows;

internal sealed record ClipboardSnapshot(IReadOnlyDictionary<uint, byte[]> Formats);
internal enum ClipboardRestoreResult { Restored, ChangedByUser, BusyOrFailed }

/// <summary>Materializes restorable clipboard formats before changing the clipboard.</summary>
internal static class SafeClipboard
{
    const uint UnicodeText = 13;
    const int MaximumBytes = 64 * 1024 * 1024;
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

    public static bool TryReplacePlainText(string text, out ClipboardSnapshot snapshot, out uint sequence, out string reason)
    {
        snapshot = new(new Dictionary<uint, byte[]>()); sequence = 0;
        reason = "The clipboard is busy or unavailable. Your result is in Typr; use Copy text when ready.";
        var owner = Owner;
        // EmptyClipboard with a null owner cannot subsequently receive SetClipboardData.
        if (owner == 0 || !OpenClipboard(owner)) return false;
        try {
            if (!CaptureLocked(out snapshot)) {
                reason = "Typr cannot safely preserve this clipboard format. Your result is in Typr; use Copy text when ready.";
                return false;
            }
            if (!WriteLocked(new Dictionary<uint, byte[]> { [UnicodeText] = Encoding.Unicode.GetBytes(text + "\0") })) {
                WriteLocked(snapshot.Formats);
                return false;
            }
        }
        finally { CloseClipboard(); }
        // Windows finalizes synthesized formats/sequence when the clipboard closes.
        // Capturing the number under the write lock makes our own update appear stale.
        sequence = NativeMethods.GetClipboardSequenceNumber();
        if (GetClipboardOwner() != owner) return false;
        reason = "";
        return true;
    }

    static bool CaptureLocked(out ClipboardSnapshot snapshot)
    {
        var formats = new Dictionary<uint, byte[]>();
        snapshot = new(formats);
        long total = 0;
        uint previous = 0;
        while (true) {
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
    {
        var owner = Owner;
        if (owner == 0 || !OpenClipboard(owner)) return ClipboardRestoreResult.BusyOrFailed;
        try {
            if (NativeMethods.GetClipboardSequenceNumber() != sequence) return ClipboardRestoreResult.ChangedByUser;
            return WriteLocked(snapshot.Formats) ? ClipboardRestoreResult.Restored : ClipboardRestoreResult.BusyOrFailed;
        }
        finally { CloseClipboard(); }
    }

    static bool WriteLocked(IReadOnlyDictionary<uint, byte[]> formats)
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
