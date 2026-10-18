using System.Runtime.InteropServices;

namespace Typr.Windows;

/// <summary>Remembers the last external writable field without retaining clipboard data.</summary>
internal sealed class ExternalTargetTracker
{
    DeliveryTarget? current;
    long capturedAt;
    int refreshing;
    public DeliveryTarget? Current => Volatile.Read(ref current);

    public void Clear() { Volatile.Write(ref current, null); Volatile.Write(ref capturedAt, 0); }

    // Call at most once every 250 ms while the desktop companion is running. CaptureTargetAsync
    // globally bounds outstanding UIA requests, including providers that do not return.
    public async Task<DeliveryTarget?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return Current;
        try {
            var window = NativeMethods.GetForegroundWindow();
            if (window == 0 || !NativeMethods.IsWindow(window)) { Clear(); return null; }
            uint thread = NativeMethods.GetWindowThreadProcessId(window, out uint process);
            if (process == Environment.ProcessId) {
                // Main-window editors/history must never replace an external target.
                if (Environment.TickCount64 - Volatile.Read(ref capturedAt) > 120_000) Clear();
                return Current;
            }
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(thread, ref info) || info.Focus == 0) { Clear(); return null; }
            var target = await ClipboardDelivery.CaptureTargetAsync(window, cancellationToken, 400);
            if (NativeMethods.GetForegroundWindow() != window) { Clear(); return null; }
            if (!target.Password && target.Editable && target.RuntimeId != null) {
                Volatile.Write(ref current, target);
                Volatile.Write(ref capturedAt, Environment.TickCount64);
            }
            else Clear();
            return Current;
        }
        finally { Volatile.Write(ref refreshing, 0); }
    }

    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeMethods.Rect CaretRectangle;
    }
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo information);
}
