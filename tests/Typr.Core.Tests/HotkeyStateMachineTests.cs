using Typr.Core;
using Xunit;

namespace Typr.Core.Tests;

public sealed class HotkeyStateMachineTests
{
    private static HotkeyUpdate Down(HotkeyStateMachine state, HotkeyKey key) => state.Process(key, true);
    private static HotkeyUpdate Up(HotkeyStateMachine state, HotkeyKey key) => state.Process(key, false);
    private static HotkeyUpdate Start(HotkeyStateMachine state)
    {
        Assert.Empty(Down(state, HotkeyKey.Control).Actions);
        return Down(state, HotkeyKey.Alt);
    }
    [Fact] public void BaseChordStartsAndReleaseStops()
    {
        var state = new HotkeyStateMachine();
        var start = Start(state);
        Assert.Equal([HotkeyAction.Started], start.Actions);
        Assert.Equal(DictationMode.Polish, start.Mode);
        Assert.Equal(RecordingState.Holding, start.State);
        var stop = Up(state, HotkeyKey.Alt);
        Assert.Equal([HotkeyAction.Stopped], stop.Actions);
        Assert.True(stop.SendDummyKeyBeforeRelease);
    }
    [Fact] public void AltThenControlWorks()
    {
        var state = new HotkeyStateMachine();
        Down(state, HotkeyKey.Alt);
        Assert.Contains(HotkeyAction.Started, Down(state, HotkeyKey.Control).Actions);
    }
    [Fact] public void ShiftIsStickyEvenWhenReleasedFirst()
    {
        var state = new HotkeyStateMachine();
        Start(state);
        Assert.Contains(HotkeyAction.ModeChanged, Down(state, HotkeyKey.Shift).Actions);
        Up(state, HotkeyKey.Shift);
        Assert.Equal(DictationMode.Raw, Up(state, HotkeyKey.Control).Mode);
    }
    [Fact] public void WindowsBeatsShiftAndIsSticky()
    {
        var state = new HotkeyStateMachine();
        Down(state, HotkeyKey.Shift);
        Assert.Equal(DictationMode.Raw, Start(state).Mode);
        Assert.Equal(DictationMode.VoiceEdit, Down(state, HotkeyKey.Windows).Mode);
        Assert.True(Up(state, HotkeyKey.Windows).SendDummyKeyBeforeRelease);
        Up(state, HotkeyKey.Shift);
        Assert.Equal(DictationMode.VoiceEdit, Up(state, HotkeyKey.Alt).Mode);
    }
    [Fact] public void WindowsHeldBeforeBaseStartsVoiceEdit()
    {
        var state = new HotkeyStateMachine();
        Down(state, HotkeyKey.Windows);
        Assert.Equal(DictationMode.VoiceEdit, Start(state).Mode);
        Assert.Empty(Down(state, HotkeyKey.Shift).Actions);
    }
    [Fact] public void AutoRepeatDoesNotStartOrStopTwice()
    {
        var state = new HotkeyStateMachine();
        Start(state);
        Assert.Empty(Down(state, HotkeyKey.Alt).Actions);
        Up(state, HotkeyKey.Alt);
        Assert.Empty(Up(state, HotkeyKey.Alt).Actions);
        Assert.Empty(Down(state, HotkeyKey.Alt).Actions);
    }
    [Fact] public void RequiresBothBaseModifiersReleasedBeforeRearm()
    {
        var state = new HotkeyStateMachine();
        Start(state);
        Up(state, HotkeyKey.Alt);
        Assert.Empty(Down(state, HotkeyKey.Alt).Actions);
        Up(state, HotkeyKey.Control);
        Up(state, HotkeyKey.Alt);
        Assert.Contains(HotkeyAction.Started, Start(state).Actions);
    }
    [Fact] public void OtherKeyCancelsAndDoesNotRestartOnItsRelease()
    {
        var state = new HotkeyStateMachine();
        Start(state);
        var result = Down(state, HotkeyKey.Other);
        Assert.Contains(HotkeyAction.Cancelled, result.Actions);
        Assert.False(result.SuppressKey);
        Assert.Empty(Up(state, HotkeyKey.Other).Actions);
        Assert.Empty(Up(state, HotkeyKey.Control).Actions);
        Assert.True(Up(state, HotkeyKey.Alt).SendDummyKeyBeforeRelease);
        Assert.Contains(HotkeyAction.Started, Start(state).Actions);
    }
    [Fact] public void EscapeCancelsAndSwallowsBothTransitions()
    {
        var state = new HotkeyStateMachine();
        Start(state);
        var cancel = Down(state, HotkeyKey.Escape);
        Assert.Contains(HotkeyAction.Cancelled, cancel.Actions);
        Assert.True(cancel.SuppressKey);
        Assert.True(Down(state, HotkeyKey.Escape).SuppressKey);
        Assert.True(Up(state, HotkeyKey.Escape).SuppressKey);
        Assert.Empty(Up(state, HotkeyKey.Alt).Actions);
    }
    [Fact] public void IdleEscapeIsUntouched()
    {
        var state = new HotkeyStateMachine();
        Assert.False(Down(state, HotkeyKey.Escape).SuppressKey);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandsFreeSurvivesReleaseAndFinishesWithNextBaseChord(bool addSpace)
    {
        var state = new HotkeyStateMachine();
        Start(state);
        var enable = Down(state, HotkeyKey.Space);
        Assert.Equal(RecordingState.HandsFree, enable.State);
        Assert.Equal([HotkeyAction.HandsFreeEnabled], enable.Actions);
        Assert.True(enable.SuppressKey);
        Assert.True(Up(state, HotkeyKey.Space).SuppressKey);
        Assert.Empty(Up(state, HotkeyKey.Alt).Actions);
        Assert.Empty(Up(state, HotkeyKey.Control).Actions);
        Assert.Equal([HotkeyAction.Stopped], Start(state).Actions);
        if (addSpace)
        {
            var finish = Down(state, HotkeyKey.Space);
            Assert.Empty(finish.Actions);
            Assert.True(finish.SuppressKey);
            Assert.Equal(RecordingState.Idle, finish.State);
        }
    }
    [Fact] public void HandsFreeAllowsTypingButEscapeStillCancels()
    {
        var state = new HotkeyStateMachine();
        Start(state); Down(state, HotkeyKey.Space); Up(state, HotkeyKey.Space);
        Up(state, HotkeyKey.Alt); Up(state, HotkeyKey.Control);
        Assert.Empty(Down(state, HotkeyKey.Other).Actions);
        Assert.Contains(HotkeyAction.Cancelled, Down(state, HotkeyKey.Escape).Actions);
    }
    [Fact] public void PasteLastSwallowsZOnlyWhileIdleWithExactChord()
    {
        var state = new HotkeyStateMachine();
        Down(state, HotkeyKey.Control); Down(state, HotkeyKey.Shift);
        var result = Down(state, HotkeyKey.Z);
        Assert.Equal([HotkeyAction.PasteLast], result.Actions);
        Assert.True(result.SuppressKey);
        Assert.True(Up(state, HotkeyKey.Z).SuppressKey);
    }
    [Fact] public void SpaceHeldBeforeBaseChordAlsoEnablesHandsFree()
    {
        var state = new HotkeyStateMachine();
        Down(state, HotkeyKey.Space);
        var result = Start(state);
        Assert.Equal(RecordingState.HandsFree, result.State);
        Assert.Contains(HotkeyAction.HandsFreeEnabled, result.Actions);
    }
    [Fact] public void EarlierUnrelatedHeldKeyDoesNotHideAnotherCancellation()
    {
        var state = new HotkeyStateMachine();
        Down(state, HotkeyKey.Other);
        Start(state);
        Assert.Contains(HotkeyAction.Cancelled, Down(state, HotkeyKey.Other).Actions);
    }
    [Fact] public void ModeResetsForNextRecording()
    {
        var state = new HotkeyStateMachine();
        Start(state); Down(state, HotkeyKey.Shift);
        Up(state, HotkeyKey.Alt); Up(state, HotkeyKey.Control); Up(state, HotkeyKey.Shift);
        Assert.Equal(DictationMode.Polish, Start(state).Mode);
    }
    [Fact] public void ModifiersHeldTracksAllFourModifiers()
    {
        var state = new HotkeyStateMachine();
        Assert.False(state.ModifiersHeld);
        Down(state, HotkeyKey.Shift); Assert.True(state.ModifiersHeld);
        Up(state, HotkeyKey.Shift); Assert.False(state.ModifiersHeld);
    }
    [Fact] public void UnrelatedAltAndWindowsReleaseDoNotAskForDummyKey()
    {
        var state = new HotkeyStateMachine();
        Down(state, HotkeyKey.Alt); Assert.False(Up(state, HotkeyKey.Alt).SendDummyKeyBeforeRelease);
        Down(state, HotkeyKey.Windows); Assert.False(Up(state, HotkeyKey.Windows).SendDummyKeyBeforeRelease);
    }
}
