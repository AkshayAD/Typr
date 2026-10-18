using System.Runtime.InteropServices;

namespace Typr.Windows;

internal sealed class WindowsGameModeMonitor : IDisposable
{
    private readonly INativeApi _native;
    private readonly PowerModeCallback _callback;
    private IntPtr _registration;
    private int _state; // 0: ordinary work, 1: game mode, -1: disposed/unsupported.

    public WindowsGameModeMonitor() : this(new NativeApi()) { }

    internal WindowsGameModeMonitor(INativeApi native)
    {
        _native = native;
        _callback = OnPowerModeChanged;
        try {
            IsSupported = _native.Register(2, _callback, out _registration) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        if (!IsSupported) { _registration = IntPtr.Zero; Interlocked.Exchange(ref _state, -1); }
    }

    public bool IsSupported { get; }
    public bool IsGaming => Volatile.Read(ref _state) == 1;

    // Runs on a native callback thread. Dispatch to the UI, then read IsGaming there.
    public event Action? Changed;

    private void OnPowerModeChanged(int mode, IntPtr context)
    {
        try {
            int next = mode == 5 /* EffectivePowerModeGameMode */ ? 1 : 0;
            int previous;
            do {
                previous = Volatile.Read(ref _state);
                if (previous < 0 || previous == next) return;
            } while (Interlocked.CompareExchange(ref _state, next, previous) != previous);
            Changed?.Invoke();
        }
        catch { /* Managed subscriber exceptions must never escape through the native callback. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, -1) < 0) return;
        var registration = Interlocked.Exchange(ref _registration, IntPtr.Zero);
        // Unregister waits for in-flight callbacks: never call Dispose from Changed itself.
        try { if (registration != IntPtr.Zero) _native.Unregister(registration); }
        finally { GC.KeepAlive(_callback); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void PowerModeCallback(int mode, IntPtr context);

    internal interface INativeApi
    {
        int Register(uint version, PowerModeCallback callback, out IntPtr registration);
        int Unregister(IntPtr registration);
    }

    private sealed class NativeApi : INativeApi
    {
        public int Register(uint version, PowerModeCallback callback, out IntPtr registration) =>
            PowerRegisterForEffectivePowerModeNotifications(version, callback, IntPtr.Zero, out registration);

        public int Unregister(IntPtr registration) => PowerUnregisterFromEffectivePowerModeNotifications(registration);

        [DllImport("Powrprof.dll", ExactSpelling = true)]
        private static extern int PowerRegisterForEffectivePowerModeNotifications(uint version,
            PowerModeCallback callback, IntPtr context, out IntPtr registration);

        [DllImport("Powrprof.dll", ExactSpelling = true)]
        private static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr registration);
    }
}
