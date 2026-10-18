using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Controls;
using Typr.Core;
using Typr.Infrastructure;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// Feed normalized physical-key transitions without installing a hook, showing a
// window, opening a microphone, loading a recognizer, or sending native input.
[Collection("WPF")]
public sealed class HoldReleaseTests(WpfFixture fixture)
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(HotkeyKey.Other)]
    [InlineData(HotkeyKey.Windows)]
    [InlineData(HotkeyKey.Shift)]
    [InlineData(HotkeyKey.Space)]
    public void ReleasedHoldAndRejectedNewChordCannotCancelEarlierRecognitionBacklog(HotkeyKey nextKey) => fixture.Run(() => WithWindow(window => {
        using var operation = new CancellationTokenSource();
        var backlog = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = operation.Token.Register(() => backlog.TrySetCanceled(operation.Token));
        Set(window, "_operation", operation); Set(window, "_busy", true); Set(window, "_speechGeneration", 42);
        ((TextBlock)window.FindName("ShortcutStatus")).Text = "Finishing captured audio";
        var machine = new HotkeyStateMachine();
        machine.Process(HotkeyKey.Control, true); machine.Process(HotkeyKey.Alt, true);
        // Recognition is still draining after the first modifier release. The
        // second release and a later rejected chord must leave its token intact.
        Send(HotkeyKey.Alt, false); Send(HotkeyKey.Control, false);
        Assert.False(operation.IsCancellationRequested);
        Send(HotkeyKey.Control, true); Send(HotkeyKey.Alt, true); Send(nextKey, true);
        Send(nextKey, false); Send(HotkeyKey.Alt, false); Send(HotkeyKey.Control, false);
        Assert.False(operation.IsCancellationRequested);
        Assert.False(backlog.Task.IsCompleted);
        Assert.Equal(42, Get<int>(window, "_speechGeneration"));
        Assert.True(Get<bool>(window, "_busy"));
        Assert.Equal("Finishing captured audio", ((TextBlock)window.FindName("ShortcutStatus")).Text);
        backlog.SetResult("Every queued word survived release.");
        Assert.Equal("Every queued word survived release.", backlog.Task.GetAwaiter().GetResult());

        void Send(HotkeyKey key, bool down) => Invoke(window, "HandleHotkey", machine.Process(key, down), (nint)0);
        // The synthetic token is disposed before window cleanup runs.
        Set(window, "_operation", null);
    }));

    [Fact] public void UnrelatedKeyStillCancelsAnAcceptedPendingHold() => fixture.Run(() => WithWindow(window => {
        using var pending = new CancellationTokenSource(); var token = pending.Token;
        Set(window, "_recordingStart", pending);
        var machine = new HotkeyStateMachine();
        machine.Process(HotkeyKey.Control, true); machine.Process(HotkeyKey.Alt, true);
        Invoke(window, "HandleHotkey", machine.Process(HotkeyKey.Other, true), (nint)0);
        Assert.True(token.IsCancellationRequested);
        Assert.Null(Get<object?>(window, "_recordingStart"));
    }));

    [Fact] public void ModeChangeStillAppliesToAnAcceptedPendingHold() => fixture.Run(() => WithWindow(window => {
        using var pending = new CancellationTokenSource(); Set(window, "_recordingStart", pending);
        var machine = new HotkeyStateMachine();
        machine.Process(HotkeyKey.Control, true); machine.Process(HotkeyKey.Alt, true);
        Invoke(window, "HandleHotkey", machine.Process(HotkeyKey.Shift, true), (nint)0);
        Assert.Equal(DictationMode.Raw, Get<DictationMode>(window, "_startingMode"));
        Set(window, "_recordingStart", null);
    }));

    [Fact] public void ExplicitCancelStillCancelsRecognitionBacklog() => fixture.Run(() => WithWindow(window => {
        using var operation = new CancellationTokenSource(); Set(window, "_operation", operation); Set(window, "_busy", true);
        Invoke(window, "CancelCapture");
        Assert.True(operation.IsCancellationRequested);
        Assert.Equal(-1, Get<int>(window, "_speechGeneration"));
        Set(window, "_operation", null);
    }));

    [Fact] public Task PreparedRecordingOpensBeforeOriginalFocusCaptureCompletesAndReleaseDrainsBacklog() => OnUi(() => WithWindowAsync(async window => {
        var input = new FakeCapture(); var stream = new FakeStream(blockFinal: true);
        using var session = new SpeechSession(() => stream, () => input);
        Set(window, "_speech", session);
        Set(window, "_settings", new AppSettings { PolishEnabled = false, AutoPasteEnabled = false, AiPolishEnabled = false });
        var originalFocus = new TaskCompletionSource<DeliveryTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            Invoke(window, "BeginCapture", DictationMode.Polish, (nint)0, false, originalFocus.Task, false);
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(originalFocus.Task.IsCompleted);
            Assert.Same(originalFocus.Task, Get<Task<DeliveryTarget>>(window, "_target"));
            int generation = Get<int>(window, "_speechGeneration");
            var operation = Get<CancellationTokenSource>(window, "_operation");
            var machine = new HotkeyStateMachine();
            machine.Process(HotkeyKey.Control, true); machine.Process(HotkeyKey.Alt, true);
            Send(HotkeyKey.Alt, false);
            await stream.Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(input.Stopped.Task.IsCompleted);
            Assert.True(Get<bool>(window, "_busy"));
            Send(HotkeyKey.Control, false);
            Send(HotkeyKey.Control, true); Send(HotkeyKey.Alt, true); Send(HotkeyKey.Other, true);
            Send(HotkeyKey.Other, false); Send(HotkeyKey.Alt, false); Send(HotkeyKey.Control, false);
            Assert.False(operation.IsCancellationRequested);
            Assert.Equal(generation, Get<int>(window, "_speechGeneration"));
            stream.Release.TrySetResult();
            var deadline = Stopwatch.StartNew();
            while (Get<bool>(window, "_busy") && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.False(Get<bool>(window, "_busy"));
            Assert.Equal("All captured words after release.", Get<string>(window, "_latest"));
            Assert.False(originalFocus.Task.IsCompleted); // Never recaptured a different field.
            void Send(HotkeyKey key, bool down) => Invoke(window, "HandleHotkey", machine.Process(key, down), (nint)0);
        }
        finally { stream.Release.TrySetResult(); Set(window, "_speech", null); }
    }));

    [Fact] public Task ReleaseDuringColdPreparationNeverOpensALateMicrophone() => OnUi(() => WithWindowAsync(async window => {
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new FakeCapture(); var stream = new FakeStream(blockFinal: false);
        using var session = new SpeechSession(() => stream, () => input);
        Set(window, "_loading", true); Set(window, "_speechLoadingTask", loading.Task);
        var start = (Task)typeof(MainWindow).GetMethod("StartCaptureAsync", Hidden)!.Invoke(window, [DictationMode.Polish, (nint)0, true, false])!;
        Assert.NotNull(Get<object?>(window, "_recordingStart"));
        var machine = new HotkeyStateMachine();
        machine.Process(HotkeyKey.Control, true); machine.Process(HotkeyKey.Alt, true);
        Invoke(window, "HandleHotkey", machine.Process(HotkeyKey.Alt, false), (nint)0);
        Set(window, "_speech", session); loading.TrySetResult();
        try {
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(input.Started.Task.IsCompleted);
            Assert.False(Get<bool>(window, "_recording"));
            Assert.Null(Get<object?>(window, "_recordingStart"));
        }
        finally { Set(window, "_loading", false); Set(window, "_speech", null); }
    }));

    async Task OnUi(Func<Task> action)
    {
        Task result = Task.CompletedTask; fixture.Run(() => result = action());
        await result.WaitAsync(TimeSpan.FromSeconds(15));
    }
    static async Task WithWindowAsync(Func<MainWindow, Task> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Typr-hold-release-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(Path.Combine(directory, "settings"), Path.Combine(directory, "local"), desktopIntegration: false);
        Get<CaptionOverlay>(window, "_overlay").Configure(new AppSettings { LiveCaptionsEnabled = false, BarVisible = false });
        try { await check(window); Assert.False(window.IsVisible); }
        finally {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    sealed class FakeCapture : IMicrophoneCapture
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start() { Started.TrySetResult(); Samples?.Invoke([.2f, .3f]); }
        public void Stop() => Stopped.TrySetResult();
        public void Dispose() { }
    }
    sealed class FakeStream(bool blockFinal) : ISpeechStream
    {
        bool finished;
        public readonly TaskCompletionSource Finalizing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Accept(float[] samples) { }
        public void InputFinished() => finished = true;
        public string Decode()
        {
            if (!finished) return "Still catching up";
            Finalizing.TrySetResult();
            if (blockFinal && !Release.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic final decode wasn't released.");
            return "All captured words after release.";
        }
        public void Dispose() => Release.TrySetResult();
    }

    static void WithWindow(Action<MainWindow> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Typr-hold-release-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(Path.Combine(directory, "settings"), Path.Combine(directory, "local"), desktopIntegration: false);
        try { check(window); Assert.False(window.IsVisible); }
        finally {
            bool closed = false; window.Closed += (_, _) => closed = true;
            window.Close(); WpfFixture.Pump(); Assert.True(closed);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Hidden)!.SetValue(window, value);
    static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;
    static void Invoke(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, Hidden)!.Invoke(window, arguments);
}
