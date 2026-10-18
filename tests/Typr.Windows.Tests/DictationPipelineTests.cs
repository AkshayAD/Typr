using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using Typr.Core;
using Typr.Infrastructure;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// Real host queue and speech worker; no window, microphone hardware, provider,
// clipboard or injected keys. Deliberately stall recognition behind a new input.
[Collection("WPF")]
public sealed class DictationPipelineTests(WpfFixture fixture)
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact] public Task NextUtteranceCapturesBeforeEarlierRecognitionFinishesAndPracticeInsertionStaysOrdered() => OnUi(async () => {
        await WithWindow(async (window, inputs, streams, local) => {
            await Start(window, inputs[0]);
            var first = Invoke<Task>(window, "FinishCaptureAsync");
            await streams[0].Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Property<bool>(window, "HasPendingDictationOutput"));
            Assert.True(Property<bool>(window, "ShouldShowDictationBar"));
            Assert.False(Get<bool>(window, "_busy"));
            await Start(window, inputs[1]);
            Assert.False(first.IsCompleted);
            Assert.Equal("LISTENING", Control<TextBlock>(window, "CaptureState").Text);
            var second = Invoke<Task>(window, "FinishCaptureAsync");
            await inputs[1].Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted); // Both microphones can close while old ASR is blocked.
            streams[0].Release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("First thought.Second thought.", Control<TextBox>(window, "PracticeBox").Text);
            Assert.Equal("Second thought.", Get<string>(window, "_latest"));
            Assert.False(Get<bool>(window, "_recording"));
            await Until(() => !Get<bool>(window, "_inputActive"));
            Assert.False(Property<bool>(window, "HasPendingDictationOutput"));
            Assert.False(Property<bool>(window, "ShouldShowDictationBar"));
            var history = new TranscriptHistoryStore(Path.Combine(local, "history.json")); history.Load();
            Assert.Equal(["Second thought.", "First thought."], history.Entries.Select(entry => entry.Text));
            Assert.All(history.Entries, entry => Assert.Equal("Practice", entry.DeliveryStatus));
        });
    });

    [Fact] public Task CancellingNewCapturePreservesOlderQueuedOutputAndItsSettings() => OnUi(async () => {
        await WithWindow(async (window, inputs, streams, local) => {
            await Start(window, inputs[0]);
            var first = Invoke<Task>(window, "FinishCaptureAsync");
            await streams[0].Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A new capture's snapshot must not replace the first job's history preference.
            Set(window, "_settings", Get<AppSettings>(window, "_settings") with { PolishEnabled = true });
            await Start(window, inputs[1]);
            Invoke(window, "CancelCapture");
            streams[0].Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await inputs[1].Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("First thought.", Control<TextBox>(window, "PracticeBox").Text);
            var history = new TranscriptHistoryStore(Path.Combine(local, "history.json")); history.Load();
            Assert.Equal("Raw", Assert.Single(history.Entries).Mode);
            Assert.False(Property<bool>(window, "HasPendingDictationOutput"));
        });
    });

    [Fact] public Task CancellingBetweenUtterancesDiscardsOnlyPendingOutputAndDoesNotPasteLate() => OnUi(async () => {
        await WithWindow(async (window, inputs, streams, _) => {
            await Start(window, inputs[0]);
            var first = Invoke<Task>(window, "FinishCaptureAsync");
            await streams[0].Finalizing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Invoke(window, "CancelCapture");
            streams[0].Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(Control<TextBox>(window, "PracticeBox").Text);
            Assert.False(Property<bool>(window, "HasPendingDictationOutput"));
            await Start(window, inputs[1]);
            await Invoke<Task>(window, "FinishCaptureAsync").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Second thought.", Control<TextBox>(window, "PracticeBox").Text);
        });
    });

    [Fact] public Task RejectedPhysicalChordCannotFinishOrCancelABarStartedRecording() => OnUi(async () => {
        await WithWindow(async (window, inputs, streams, _) => {
            await Start(window, inputs[0]);
            var machine = new HotkeyStateMachine();
            Send(HotkeyKey.Control, true); Send(HotkeyKey.Alt, true);
            Send(HotkeyKey.Alt, false); Send(HotkeyKey.Control, false);
            Assert.True(Get<bool>(window, "_recording"));
            Send(HotkeyKey.Control, true); Send(HotkeyKey.Alt, true); Send(HotkeyKey.Other, true);
            Assert.True(Get<bool>(window, "_recording"));
            streams[0].Release.TrySetResult();
            await Invoke<Task>(window, "FinishCaptureAsync").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("First thought.", Control<TextBox>(window, "PracticeBox").Text);
            void Send(HotkeyKey key, bool down) => Invoke(window, "HandleHotkey", machine.Process(key, down), (nint)0);
        });
    });

    [Fact] public Task ReleasedMicrophoneFailureDoesNotCancelEarlierValidQueuedOutput() => OnUi(async () => {
        await WithWindow(async (window, inputs, streams, _) => {
            streams[0].Release.TrySetResult();
            Set(window, "_settings", Get<AppSettings>(window, "_settings") with { AutoPasteEnabled = true });
            var destination = new TaskCompletionSource<DeliveryTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
            Invoke(window, "BeginCapture", DictationMode.Raw, (nint)0, false, destination.Task, false);
            await inputs[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstOperation = Get<CancellationTokenSource>(window, "_operation");
            var first = Invoke<Task>(window, "FinishCaptureAsync");
            await Until(() => Get<string>(window, "_latest") == "First thought.");
            Assert.False(first.IsCompleted); // Ready text is waiting for its original destination.
            await Start(window, inputs[1]);
            inputs[1].FailStop = true;
            var second = Invoke<Task>(window, "FinishCaptureAsync");
            await Until(() => Get<bool>(window, "_speechFaulted"));
            Assert.False(firstOperation.IsCancellationRequested);
            Assert.False(first.IsCompleted);
            Invoke(window, "CancelCapture"); // Explicitly cancel the still-pending test destination.
            destination.TrySetCanceled();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        });
    });

    static async Task Start(MainWindow window, FakeInput input)
    {
        await Invoke<Task>(window, "StartCaptureAsync", DictationMode.Raw, (nint)0, true, false);
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Control<TextBlock>(window, "CaptureState").Text == "LISTENING");
    }
    async Task OnUi(Func<Task> action)
    {
        Task task = Task.CompletedTask; fixture.Run(() => task = action());
        await task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    static async Task WithWindow(Func<MainWindow, FakeInput[], FakeStream[], string, Task> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "typr-pipeline-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(Path.Combine(root, "settings"), Path.Combine(root, "local"), false);
        var inputs = new[] { new FakeInput(), new FakeInput() };
        var streams = new[] { new FakeStream("First thought.", true), new FakeStream("Second thought.", false) };
        int inputIndex = -1, streamIndex = -1;
        using var speech = new SpeechSession(() => streams[Interlocked.Increment(ref streamIndex)], () => inputs[Interlocked.Increment(ref inputIndex)]);
        Set(window, "_speech", speech);
        Set(window, "_settings", new AppSettings { SaveHistory = true, PolishEnabled = false, AiPolishEnabled = false, AutoPasteEnabled = false, BarVisible = false, LiveCaptionsEnabled = false });
        Invoke(window, "ObserveSpeechSession", speech);
        try { await check(window, inputs, streams, Path.Combine(root, "local")); Assert.False(window.IsVisible); }
        finally
        {
            streams[0].Release.TrySetResult();
            Invoke(window, "CancelAllDictation");
            await Get<Task>(window, "_dictationOutputTail");
            Set(window, "_speech", null);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    static async Task Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(ready());
    }
    sealed class FakeInput : IMicrophoneCapture
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed { add { } remove { } }
        public bool FailStop;
        public void Start() { Samples?.Invoke([.2f, .4f]); Started.TrySetResult(); }
        public void Stop() { Stopped.TrySetResult(); if (FailStop) throw new InvalidOperationException("Synthetic microphone stop failure."); }
        public void Dispose() { }
    }
    sealed class FakeStream(string result, bool block) : ISpeechStream
    {
        bool finished;
        public TaskCompletionSource Finalizing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Accept(float[] samples) { }
        public void InputFinished() => finished = true;
        public string Decode()
        {
            if (!finished) return "";
            Finalizing.TrySetResult();
            if (block && !Release.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Final recognition was not released.");
            return result;
        }
        public void Dispose() { }
    }
    static T Control<T>(MainWindow window, string name) => (T)window.FindName(name);
    static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;
    static T Property<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetProperty(name, Hidden)!.GetValue(window)!;
    static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Hidden)!.SetValue(window, value);
    static void Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Hidden)!.Invoke(window, args);
    static T Invoke<T>(MainWindow window, string name, params object?[] args) => (T)typeof(MainWindow).GetMethod(name, Hidden)!.Invoke(window, args)!;
}
