using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Typr.Core;
using Typr.Infrastructure;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// Hidden product controls plus the real speech worker with fake audio/recognition.
// No Show, handles, hooks, UIA, device enumeration, clipboard or native input.
[Collection("WPF")]
public sealed class RecordingPreparationTests(WpfFixture fixture)
{
    [Theory]
    [InlineData("release")] [InlineData("escape")] [InlineData("bar cancel")]
    public void CancellationDuringPreparationCannotOpenAnInputLater(string cancellation) => fixture.Run(() => {
        WithWindow((window, _) => {
            int opens = 0;
            using var speech = new SpeechSession(() => new FakeStream(), () => { ++opens; return new FakeInput(); });
            var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_speech", speech); Set(window, "_loading", true); Set(window, "_speechLoadingTask", preparation.Task);
            var start = Invoke<Task>(window, "StartCaptureAsync", DictationMode.Polish, (nint)0, true, false);
            Assert.False(start.IsCompleted); Assert.Equal(0, opens);
            Assert.Equal(Visibility.Visible, Control<Button>(window, "CancelButton").Visibility);
            if (cancellation == "release")
                Invoke(window, "HandleHotkey", new HotkeyUpdate(RecordingState.Idle, DictationMode.Polish, [HotkeyAction.Stopped], false, false), (nint)0);
            else if (cancellation == "bar cancel")
                Invoke<Task>(window, "CompanionActionAsync", "Cancel", true).GetAwaiter().GetResult();
            else Invoke(window, "CancelCapture"); // Actual Esc routing calls this same boundary.
            Set(window, "_loading", false); preparation.SetResult(); Complete(start);
            Assert.Equal(0, opens); Assert.False(Get<bool>(window, "_recording"));
            Assert.Null(Get<object?>(window, "_recordingStart")); Assert.False(window.IsVisible);
            Set(window, "_speech", null);
        });
    });

    [Fact] public void ExplicitPendingStartOpensOnceAndKeepsModeChangesThenCancelClosesIt() => fixture.Run(() => {
        WithWindow((window, _) => {
            int opens = 0;
            var input = new FakeInput();
            using var speech = new SpeechSession(() => new FakeStream(), () => { ++opens; return input; });
            var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_speech", speech); Set(window, "_loading", true); Set(window, "_speechLoadingTask", preparation.Task);
            var start = Invoke<Task>(window, "StartCaptureAsync", DictationMode.Polish, (nint)0, true, false);
            Invoke(window, "HandleHotkey", new HotkeyUpdate(RecordingState.Holding, DictationMode.Raw, [HotkeyAction.ModeChanged], false, false), (nint)0);
            Assert.Equal(0, opens);
            Set(window, "_loading", false); preparation.SetResult(); Complete(start); Complete(input.Started.Task);
            Assert.Equal(1, opens); Assert.True(Get<bool>(window, "_recording"));
            Assert.Equal(DictationMode.Raw, Get<DictationMode>(window, "_mode"));
            Assert.False(Control<ComboBox>(window, "InputDeviceBox").IsEnabled);
            Invoke(window, "CancelCapture"); Complete(input.Closed.Task);
            Assert.False(Get<bool>(window, "_recording")); Assert.False(window.IsVisible);
            Assert.Equal("Microphone off", Control<TextBlock>(window, "RailStatus").Text);
            Set(window, "_speech", null);
        });
    });

    [Fact] public void ShortcutOnlyConsentAlsoGuardsHostButtonHandlers() => fixture.Run(() => {
        WithWindow((window, _) => {
            int opens = 0;
            using var speech = new SpeechSession(() => new FakeStream(), () => { ++opens; return new FakeInput(); });
            Set(window, "_speech", speech);
            Invoke(window, "RefreshReadiness");
            Assert.False(Control<Button>(window, "PracticeButton").IsEnabled);
            Invoke(window, "Practice_Click", window, new RoutedEventArgs());
            Invoke<Task>(window, "CompanionActionAsync", "HoldStart", true).GetAwaiter().GetResult();
            Invoke<Task>(window, "CompanionActionAsync", "ToggleRecording", true).GetAwaiter().GetResult();
            Assert.Equal(0, opens); Assert.False(Get<bool>(window, "_recording"));
            Assert.Null(Get<object?>(window, "_recordingStart"));
            Set(window, "_speech", null);
        });
    });

    [Fact] public void SavedInputInitializesWithoutEnumerationAndCanReturnToSystemDefault() => fixture.Run(() => {
        WithWindow((window, settingsPath) => {
            var choices = Control<ComboBox>(window, "InputDeviceBox");
            Assert.Equal("synthetic-input", choices.SelectedValue);
            Assert.Equal(2, choices.Items.Count);
            Assert.Contains("Saved input", Assert.IsType<MicrophoneInput>(choices.SelectedItem).Name);
            Assert.Null(Get<SpeechSession?>(window, "_speech"));
            choices.SelectedValue = "";
            var saved = SettingsMigrator.Load(File.ReadAllText(settingsPath));
            Assert.Null(saved.InputDeviceId); Assert.False(saved.AllowButtonRecording);
            Assert.Equal(.63, saved.BarOpacity);
        }, selectedInput: "synthetic-input");
    });

    [Fact] public void CancelKeepsClosingFeedbackUntilTheWorkerConfirmsInputClosure() => fixture.Run(() => {
        WithWindow((window, _) => {
            Set(window, "_inputActive", true); Set(window, "_recording", true);
            Invoke(window, "RefreshReadiness");
            Assert.Equal("Microphone active", Control<TextBlock>(window, "RailStatus").Text);
            Invoke(window, "CancelCapture");
            Assert.Contains("Closing microphone", Control<TextBlock>(window, "RailStatus").Text);
            Assert.Equal("CLOSING MICROPHONE", Control<TextBlock>(window, "CaptureState").Text);
            // The real CaptureStateChanged(false) callback updates this field only
            // after the capture adapter's Stop + Dispose have completed.
            Set(window, "_inputActive", false); Invoke(window, "RefreshReadiness");
            Assert.Equal("Microphone off", Control<TextBlock>(window, "RailStatus").Text);
        });
    });

    [Fact] public void ListeningWaitsForActualInputAndIgnoresStaleOrCancelledReadiness() => fixture.Run(() => {
        WithWindow((window, _) => {
            using var speech = new SpeechSession(() => new FakeStream(), () => new FakeInput());
            using var previous = new SpeechSession(() => new FakeStream(), () => new FakeInput());
            Set(window, "_speech", speech); Set(window, "_recording", true); Set(window, "_speechGeneration", 21);
            Set(window, "_inputActive", true); Set(window, "_activeInputGeneration", 21);
            Invoke(window, "RefreshReadiness");
            Assert.Equal("STARTING MICROPHONE", Control<TextBlock>(window, "CaptureState").Text);
            Assert.False(Get<System.Windows.Threading.DispatcherTimer>(window, "_clock").IsEnabled);
            Invoke(window, "SpeechInputReady", previous, 21);
            Invoke(window, "SpeechInputReady", speech, 20);
            Assert.Equal("STARTING MICROPHONE", Control<TextBlock>(window, "CaptureState").Text);
            Invoke(window, "SpeechInputReady", speech, 21);
            Assert.Equal("LISTENING", Control<TextBlock>(window, "CaptureState").Text);
            Assert.True(Get<System.Windows.Threading.DispatcherTimer>(window, "_clock").IsEnabled);
            Assert.Equal("Listening on this PC.", Control<TextBlock>(window, "StatusText").Text);
            Invoke(window, "CancelCapture");
            Invoke(window, "SpeechInputReady", speech, 21);
            Assert.Equal(-1, Get<int>(window, "_readyInputGeneration"));
            Assert.Equal("CLOSING MICROPHONE", Control<TextBlock>(window, "CaptureState").Text);
            Assert.False(Get<System.Windows.Threading.DispatcherTimer>(window, "_clock").IsEnabled);
            Set(window, "_speech", null);
        });
    });

    [Fact] public void EmptyRecordingKeepsTheLastSuccessfulTranscriptAvailable() => fixture.Run(() => {
        WithWindow((window, _) => {
            var input = new FakeInput();
            using var speech = new SpeechSession(() => new FakeStream(""), () => input);
            Set(window, "_speech", speech); Set(window, "_latest", "Previous synthetic transcript");
            Complete(Invoke<Task>(window, "StartCaptureAsync", DictationMode.Raw, (nint)0, true, false));
            Complete(input.Started.Task);
            Complete(Invoke<Task>(window, "FinishCaptureAsync"));
            Assert.Equal("Previous synthetic transcript", Get<string>(window, "_latest"));
            Assert.Contains("previous transcript", Control<TextBlock>(window, "StatusText").Text);
            Assert.True(input.Closed.Task.IsCompletedSuccessfully);
            Set(window, "_speech", null);
        });
    });

    private static void WithWindow(Action<MainWindow, string> check, string? selectedInput = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "typr-recording-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, SettingsMigrator.Save(new AppSettings {
            InputDeviceId = selectedInput, LiveCaptionsEnabled = false, BarVisible = false, BarOpacity = .63
        }));
        var window = new MainWindow(root, Path.Combine(root, "local"), desktopIntegration: false);
        try { check(window, settingsPath); Assert.False(window.IsVisible); }
        finally { Set(window, "_speech", null); window.Close(); WpfFixture.Pump(); Directory.Delete(root, true); }
    }
    private static T Control<T>(MainWindow window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private static void Set(MainWindow window, string field, object? value) => typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static T Invoke<T>(MainWindow window, string method, params object?[] args) => (T)Invoke(window, method, args)!;
    private static void Complete(Task task)
    {
        var deadline = Stopwatch.StartNew();
        while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5)) { WpfFixture.Pump(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "Headless operation timed out."); task.GetAwaiter().GetResult();
    }
    private sealed class FakeInput : IMicrophoneCapture
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<float[]>? Samples { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start() => Started.SetResult();
        public void Stop() { }
        public void Dispose() => Closed.TrySetResult();
    }
    private sealed class FakeStream(string text = "synthetic") : ISpeechStream
    {
        public void Accept(float[] samples) { }
        public void InputFinished() { }
        public string Decode() => text;
        public void Dispose() { }
    }
}
