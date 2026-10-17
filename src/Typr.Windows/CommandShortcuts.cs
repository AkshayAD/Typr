using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Typr.Core;

namespace Typr.Windows;

internal sealed class CommandShortcuts : IDisposable
{
    private HwndSource? _source;
    private readonly Dictionary<int, string> _actions = [];
    public event Action<string>? Requested;

    public string Configure(Window owner, AppSettings settings)
    {
        Dispose();
        if (!settings.CommandShortcutsEnabled) return "Command shortcuts are off.";
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(HandleMessage);
        string[] errors = [];
        foreach (var (id, action, shortcut) in new[] { (3101, "ToggleMicrophone", settings.MicrophoneShortcut), (3102, "ToggleRecording", settings.HandsFreeShortcut), (3103, "PasteLast", settings.PasteShortcut) })
        {
            var (modifiers, key) = Parse(shortcut);
            if (RegisterHotKey(handle, id, modifiers | 0x4000, key)) _actions[id] = action;
            else errors = [.. errors, shortcut];
        }
        return errors.Length == 0 ? "Command shortcuts are ready." : "Already used by another app: " + string.Join(", ", errors) + ". Choose another shortcut.";
    }

    internal static (uint Modifiers, uint Key) Parse(string shortcut)
    {
        uint modifiers = shortcut.Contains("Ctrl+") ? 2u : 0;
        if (shortcut.Contains("Shift+")) modifiers |= 4;
        string key = shortcut.Split('+')[^1];
        return (modifiers, key switch { "Space" => 0x20, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79, "M" => 0x4D, "R" => 0x52, "V" => 0x56, _ => throw new ArgumentException("Unsupported command shortcut.") });
    }
    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && _actions.TryGetValue((int)wParam, out string? action))
        {
            handled = true; Requested?.Invoke(action);
        }
        return 0;
    }
    public void Dispose()
    {
        if (_source == null) return;
        foreach (int id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear(); _source.RemoveHook(HandleMessage); _source = null;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
