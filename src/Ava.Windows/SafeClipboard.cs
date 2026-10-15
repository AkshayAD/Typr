using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Ava.Windows;

internal sealed record ClipboardSnapshot(string? Text);
internal enum ClipboardRestoreResult { Restored, ChangedByUser, BusyOrFailed }

/// <summary>Only plain text is auto-replaced. Materializes data under the clipboard lock.</summary>
internal static class SafeClipboard
{
    private const uint UnicodeText = 13;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint previous);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    private static nint Owner => new WindowInteropHelper(Application.Current.MainWindow).Handle;

    public static bool TryReplacePlainText(string text, out ClipboardSnapshot snapshot, out uint sequence, out string reason)
    {
        snapshot = new(null); sequence = 0;
        reason = "Clipboard is busy. Your result is in Ava; use Copy result when ready.";
        if (!OpenClipboard(Owner)) return false;
        try
        {
            bool hasFormats = false;
            for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                hasFormats = true;
                if (format is not (1 or 7 or 13 or 16))
                {
                    reason = "Clipboard contains rich text, an image, or another format. Your result is in Ava; Copy result replaces the clipboard when you choose.";
                    return false;
                }
            }
            if (Marshal.GetLastPInvokeError() != 0) return false;
            if (hasFormats)
            {
                if (!IsClipboardFormatAvailable(UnicodeText)) return false;
                var handle = GetClipboardData(UnicodeText);
                if (handle == 0) return false;
                var length = GlobalSize(handle);
                if (length == 0 || length > 16 * 1024 * 1024) return false;
                var pointer = GlobalLock(handle);
                if (pointer == 0) return false;
                try
                {
                    var value = Marshal.PtrToStringUni(pointer, (int)length / 2)!;
                    int terminator = value.IndexOf('\0');
                    snapshot = new(terminator >= 0 ? value[..terminator] : value);
                }
                finally { GlobalUnlock(handle); }
            }
            if (!WriteLocked(text))
            {
                // Best effort recovery while we still hold exclusive clipboard ownership.
                if (snapshot.Text != null) WriteLocked(snapshot.Text);
                return false;
            }
            sequence = NativeMethods.GetClipboardSequenceNumber();
            reason = "";
            return true;
        }
        finally { CloseClipboard(); }
    }

    public static ClipboardRestoreResult RestoreIfOwned(ClipboardSnapshot snapshot, uint sequence)
    {
        if (!OpenClipboard(Owner)) return ClipboardRestoreResult.BusyOrFailed;
        try
        {
            // Check and mutate under the same native lock, so a newer copy is never overwritten.
            if (NativeMethods.GetClipboardSequenceNumber() != sequence) return ClipboardRestoreResult.ChangedByUser;
            bool restored = snapshot.Text == null ? EmptyClipboard() : WriteLocked(snapshot.Text);
            return restored ? ClipboardRestoreResult.Restored : ClipboardRestoreResult.BusyOrFailed;
        }
        finally { CloseClipboard(); }
    }

    private static bool WriteLocked(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var memory = GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == 0) return false;
        bool transferred = false;
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == 0) return false;
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            if (!EmptyClipboard()) return false;
            transferred = SetClipboardData(UnicodeText, memory) != 0;
            return transferred;
        }
        finally { if (!transferred) GlobalFree(memory); }
    }
}
