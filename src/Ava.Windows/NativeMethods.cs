using System.Runtime.InteropServices;

namespace Ava.Windows;

internal static class NativeMethods
{
    internal const int WhKeyboardLl = 13, WmKeydown = 0x100, WmKeyup = 0x101, WmSyskeydown = 0x104, WmSyskeyup = 0x105;
    internal const int GwlExstyle = -20, WsExNoactivate = 0x08000000, WsExToolwindow = 0x80, WsExTopmost = 8, WsExTransparent = 0x20;
    internal const uint KeyeventfKeyup = 2, KeyeventfUnicode = 4;
    internal delegate nint HookProc(int nCode, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardHookData { internal uint VkCode, ScanCode, Flags, Time; internal nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Msg { internal nint Hwnd; internal uint Message; internal nuint WParam; internal nint LParam; internal uint Time; internal Point Pt; internal uint Private; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { internal uint Size; internal Rect Monitor, Work; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal HardwareInput Hardware;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { internal ushort Vk, Scan; internal uint Flags, Time; internal nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { internal int Dx, Dy; internal uint MouseData, Flags, Time; internal nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct HardwareInput { internal uint Msg; internal ushort ParamL, ParamH; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int idHook, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern int GetMessage(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Msg msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint id, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    internal static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);
    internal static Input Key(ushort vk, bool up = false) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = vk, Flags = up ? KeyeventfKeyup : 0 } } };
    internal static uint SendKeys(params Input[] keys) => SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
}
