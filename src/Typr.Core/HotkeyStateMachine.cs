namespace Typr.Core;

// The hook filters injected events and normalizes left/right modifier keys, keeping
// a modifier down until BOTH physical keys of that modifier have been released.
public enum HotkeyKey { Control, Alt, Shift, Windows, Space, Escape, Z, Other }
public enum RecordingState { Idle, Holding, HandsFree }
public enum DictationMode { Polish, Raw, VoiceEdit }
public enum HotkeyAction { Started, Stopped, Cancelled, ModeChanged, HandsFreeEnabled, PasteLast }
public sealed record HotkeyUpdate(RecordingState State, DictationMode Mode,
    IReadOnlyList<HotkeyAction> Actions, bool SuppressKey, bool SendDummyKeyBeforeRelease);

/// <summary>A pure state machine; call once per normalized key transition.</summary>
public sealed class HotkeyStateMachine
{
    private readonly HashSet<HotkeyKey> _down = [];
    private readonly HashSet<HotkeyKey> _swallowed = [];
    private bool _armed = true;
    private bool _chordClaimed;
    public RecordingState State { get; private set; }
    public DictationMode Mode { get; private set; } = DictationMode.Polish;
    public bool ModifiersHeld => _down.Overlaps([HotkeyKey.Control, HotkeyKey.Alt, HotkeyKey.Shift, HotkeyKey.Windows]);

    public HotkeyUpdate Process(HotkeyKey key, bool isDown)
    {
        var actions = new List<HotkeyAction>(2);
        bool wasBaseDown = BaseDown;
        bool suppress = _swallowed.Contains(key);
        bool dummy = !isDown && _down.Contains(key) && _chordClaimed
            && key is HotkeyKey.Alt or HotkeyKey.Windows;
        bool changed = isDown ? _down.Add(key) : _down.Remove(key);
        // Other represents many physical keys; one held key must not hide the next.
        if (key == HotkeyKey.Other && isDown) changed = true;
        if (!isDown) _swallowed.Remove(key);
        if (!changed) return Result(actions, suppress, false); // Ignore auto-repeat.

        if (isDown && key == HotkeyKey.Escape && State != RecordingState.Idle)
        {
            Stop(HotkeyAction.Cancelled, actions);
            suppress = true;
        }
        else if (isDown && State == RecordingState.Idle && key == HotkeyKey.Z
            && _down.Contains(HotkeyKey.Control) && _down.Contains(HotkeyKey.Shift)
            && !_down.Contains(HotkeyKey.Alt) && !_down.Contains(HotkeyKey.Windows))
        {
            actions.Add(HotkeyAction.PasteLast);
            suppress = true;
        }
        else if (BaseDown && !wasBaseDown && _armed)
        {
            _chordClaimed = true;
            if (State == RecordingState.HandsFree)
                Stop(HotkeyAction.Stopped, actions);
            else if (State == RecordingState.Idle)
            {
                State = RecordingState.Holding;
                Mode = CurrentMode;
                actions.Add(HotkeyAction.Started);
                if (_down.Contains(HotkeyKey.Space))
                {
                    State = RecordingState.HandsFree;
                    actions.Add(HotkeyAction.HandsFreeEnabled);
                }
            }
            _armed = false;
        }
        else if (State == RecordingState.Holding && !BaseDown)
        {
            Stop(HotkeyAction.Stopped, actions);
        }
        else if (isDown && BaseDown && key == HotkeyKey.Space)
        {
            // The base chord is recognized first. Space extends that same capture.
            if (State == RecordingState.Holding)
            {
                State = RecordingState.HandsFree;
                actions.Add(HotkeyAction.HandsFreeEnabled);
            }
            suppress = _chordClaimed;
        }
        else if (isDown && BaseDown && State != RecordingState.Idle
            && key is not (HotkeyKey.Control or HotkeyKey.Alt or HotkeyKey.Shift or HotkeyKey.Windows))
        {
            Stop(HotkeyAction.Cancelled, actions);
        }

        if (State != RecordingState.Idle && BaseDown && CurrentMode > Mode)
        {
            Mode = CurrentMode; // Sticky: Windows beats Shift, including mid-hold.
            actions.Add(HotkeyAction.ModeChanged);
        }
        if (!_down.Contains(HotkeyKey.Control) && !_down.Contains(HotkeyKey.Alt))
            _armed = true;
        if (!_down.Contains(HotkeyKey.Control) && !_down.Contains(HotkeyKey.Alt)
            && !_down.Contains(HotkeyKey.Windows))
            _chordClaimed = false;
        if (isDown && suppress) _swallowed.Add(key);
        return Result(actions, suppress, dummy);
    }

    private bool BaseDown => _down.Contains(HotkeyKey.Control) && _down.Contains(HotkeyKey.Alt);
    private DictationMode CurrentMode => _down.Contains(HotkeyKey.Windows) ? DictationMode.VoiceEdit
        : _down.Contains(HotkeyKey.Shift) ? DictationMode.Raw : DictationMode.Polish;
    private void Stop(HotkeyAction action, List<HotkeyAction> actions)
    {
        State = RecordingState.Idle;
        _armed = false;
        actions.Add(action);
    }
    private HotkeyUpdate Result(List<HotkeyAction> actions, bool suppress, bool dummy)
        => new(State, Mode, actions.ToArray(), suppress, dummy);
}
