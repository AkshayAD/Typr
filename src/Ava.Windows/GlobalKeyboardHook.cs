using System.ComponentModel;
using System.Runtime.InteropServices;
using Ava.Core;

namespace Ava.Windows;

/// <summary>The hook and its message pump live on a dedicated thread. No audio, UIA, or clipboard work runs here.</summary>
internal sealed class GlobalKeyboardHook : IDisposable
{
    private readonly Thread _thread;
    private readonly NativeMethods.HookProc _callback;
    private readonly HotkeyStateMachine _machine = new();
    private readonly HashSet<uint> _physicalKeys = [];
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private nint _hook;
    private Exception? _startupError;
    private bool _disposed;
    public event Action<HotkeyUpdate, nint>? Updated;

    public GlobalKeyboardHook()
    {
        _callback = Handle;
        _thread = new Thread(Run) { IsBackground = true, Name = "Ava keyboard hook" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Keyboard hook startup timed out.");
        if (_startupError != null) throw _startupError;
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.PeekMessage(out _, 0, 0, 0, 0);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == 0) _startupError = new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't register global shortcuts.");
        _ready.Set();
        if (_hook == 0) return;
        try
        {
            while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally { NativeMethods.UnhookWindowsHookEx(_hook); _hook = 0; }
    }

    private nint Handle(int code, nint wParam, nint lParam)
    {
        if (code < 0) return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        var message = (int)wParam;
        if (message is not (NativeMethods.WmKeydown or NativeMethods.WmKeyup or NativeMethods.WmSyskeydown or NativeMethods.WmSyskeyup))
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(lParam);
        if ((data.Flags & 0x10) != 0) return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        var down = message is NativeMethods.WmKeydown or NativeMethods.WmSyskeydown;
        var key = Map(data.VkCode);
        var wasDown = _physicalKeys.Any(k => Map(k) == key);
        if (down) _physicalKeys.Add(data.VkCode); else _physicalKeys.Remove(data.VkCode);
        var isDown = _physicalKeys.Any(k => Map(k) == key);
        // Multiple physical modifier keys collapse to a single logical transition.
        if (key is HotkeyKey.Control or HotkeyKey.Alt or HotkeyKey.Shift or HotkeyKey.Windows && wasDown == isDown)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        try
        {
            var update = _machine.Process(key, key == HotkeyKey.Other ? down : isDown);
            if (update.SendDummyKeyBeforeRelease)
                NativeMethods.SendKeys(NativeMethods.Key(0xE8), NativeMethods.Key(0xE8, true));
            Updated?.Invoke(update, NativeMethods.GetForegroundWindow());
            if (update.SuppressKey) return 1;
        }
        catch { /* Never let an exception escape a native callback. */ }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static HotkeyKey Map(uint key) => key switch
    {
        0x11 or 0xA2 or 0xA3 => HotkeyKey.Control,
        0x12 or 0xA4 or 0xA5 => HotkeyKey.Alt,
        0x10 or 0xA0 or 0xA1 => HotkeyKey.Shift,
        0x5B or 0x5C => HotkeyKey.Windows,
        0x20 => HotkeyKey.Space,
        0x1B => HotkeyKey.Escape,
        0x5A => HotkeyKey.Z,
        _ => HotkeyKey.Other
    };

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, 0x12, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
