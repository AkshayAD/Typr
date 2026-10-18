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

[Collection("WPF")]
public sealed class SpeechResourceTests(WpfFixture fixture)
{
    [Fact] public void GamingUnloadsPreparedSpeechAndRejectsNewRecordingOrPreparation() => fixture.Run(() => WithWindow((window, path) => {
        int opens = 0;
        using var speech = new SpeechSession(() => new FakeStream(), () => { ++opens; return new FakeInput(); });
        Set(window, "_speech", speech);
        Control<ComboBox>(window, "SpeechResourceModeBox").SelectedIndex = 2;
        Complete(Get<Task>(window, "_resourceTransitionTask"));
        Assert.Null(Get<SpeechSession?>(window, "_speech"));
        Assert.Equal("Gaming", SettingsMigrator.Load(File.ReadAllText(path)).SpeechResourceMode);
        Complete(Invoke<Task>(window, "StartCaptureAsync", DictationMode.Raw, (nint)0, true, false));
        Complete(Invoke<Task>(window, "LoadSpeechAsync", false));
        Assert.Equal(0, opens);
        Assert.False(Control<Button>(window, "HeroButton").IsEnabled);
        Assert.Equal("GAMING / SPEECH PAUSED", Control<TextBlock>(window, "CaptureState").Text);
        Assert.False(GetProperty<AppSettings>(window, "EffectiveShortcutSettings").CommandShortcutsEnabled);
        Assert.False(window.IsVisible);
    }));

    [Fact] public void AutomaticUsesWindowsGameSignalAndEverydayOverridesItWithoutOpeningInput() => fixture.Run(() => WithWindow((window, _) => {
        Set(window, "_windowsGaming", true); Invoke(window, "RefreshReadiness");
        Assert.True(GetProperty<bool>(window, "SpeechPausedForGaming"));
        Assert.False(GetProperty<AppSettings>(window, "EffectiveShortcutSettings").CommandShortcutsEnabled);
        Control<ComboBox>(window, "SpeechResourceModeBox").SelectedIndex = 1;
        Assert.False(GetProperty<bool>(window, "SpeechPausedForGaming"));
        Assert.True(GetProperty<AppSettings>(window, "EffectiveShortcutSettings").CommandShortcutsEnabled);
        Assert.Null(Get<SpeechSession?>(window, "_speech"));
        Assert.False(Get<bool>(window, "_loading"));
        Control<ComboBox>(window, "SpeechResourceModeBox").SelectedIndex = 0;
        Assert.True(GetProperty<bool>(window, "SpeechPausedForGaming"));
        Set(window, "_windowsGaming", false); Invoke(window, "RefreshReadiness");
        Assert.False(GetProperty<bool>(window, "SpeechPausedForGaming"));
        Assert.Null(Get<SpeechSession?>(window, "_speech")); // Previously unloaded stays unloaded.
    }));

    [Fact] public void EnteringGamingKeepsCurrentDictationThroughItsCompleteEndingThenUnloads() => fixture.Run(() => WithWindow((window, _) => {
        var input = new FakeInput();
        using var speech = new SpeechSession(() => new FakeStream(), () => input);
        Set(window, "_speech", speech);
        Complete(Invoke<Task>(window, "StartCaptureAsync", DictationMode.Raw, (nint)0, true, false));
        Complete(input.Started.Task);
        input.Emit([.25f]);
        Control<ComboBox>(window, "SpeechResourceModeBox").SelectedIndex = 2;
        Assert.Same(speech, Get<SpeechSession>(window, "_speech"));
        Assert.True(Get<bool>(window, "_recording"));
        Assert.Contains("finishing current dictation", Control<TextBlock>(window, "SpeechResourceStatus").Text);
        Assert.True(GetProperty<AppSettings>(window, "EffectiveShortcutSettings").CommandShortcutsEnabled);
        Complete(Invoke<Task>(window, "FinishCaptureAsync"));
        Complete(Get<Task>(window, "_resourceTransitionTask"));
        Assert.Equal("First and last words", Get<string>(window, "_latest"));
        Assert.True(input.Disposed);
        Assert.Null(Get<SpeechSession?>(window, "_speech"));
        Assert.Equal("GAMING / SPEECH PAUSED", Control<TextBlock>(window, "CaptureState").Text);
    }));

    [Fact] public void GamingCancelsPendingPreparationAndBackgroundSpeechWork() => fixture.Run(() => WithWindow((window, _) => {
        var pending = new CancellationTokenSource(); var token = pending.Token;
        using var download = new CancellationTokenSource(); using var history = new CancellationTokenSource(); using var comparison = new CancellationTokenSource();
        Set(window, "_recordingStart", pending); Set(window, "_loading", true);
        Set(window, "_download", download); Set(window, "_historyOperation", history); Set(window, "_providerComparison", comparison);
        Control<ComboBox>(window, "SpeechResourceModeBox").SelectedIndex = 2;
        Assert.True(token.IsCancellationRequested);
        Assert.True(download.IsCancellationRequested); Assert.True(history.IsCancellationRequested); Assert.True(comparison.IsCancellationRequested);
        Assert.Null(Get<object?>(window, "_recordingStart"));
        Set(window, "_download", null); Set(window, "_historyOperation", null); Set(window, "_providerComparison", null); Set(window, "_loading", false);
    }));

    [Fact] public void CancelDuringGamingWaitsForDisposalThenClearsActualInputFeedback() => fixture.Run(() => WithWindow((window, _) => {
        var input = new FakeInput();
        using var speech = new SpeechSession(() => new FakeStream(), () => input);
        Set(window, "_speech", speech);
        Complete(Invoke<Task>(window, "StartCaptureAsync", DictationMode.Raw, (nint)0, true, false));
        Complete(input.Started.Task);
        Set(window, "_inputActive", true); Set(window, "_readyInputGeneration", Get<int>(window, "_speechGeneration"));
        Control<ComboBox>(window, "SpeechResourceModeBox").SelectedIndex = 2;
        Invoke(window, "CancelCapture");
        Complete(Get<Task>(window, "_resourceTransitionTask"));
        Assert.True(input.Disposed);
        Assert.False(Get<bool>(window, "_inputActive"));
        Assert.Equal(-1, Get<int>(window, "_readyInputGeneration"));
        Assert.Equal("Microphone off", Control<TextBlock>(window, "RailStatus").Text);
        Assert.Equal("GAMING / SPEECH PAUSED", Control<TextBlock>(window, "CaptureState").Text);
    }));

    static void WithWindow(Action<MainWindow, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "typr-resources-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, SettingsMigrator.Save(new AppSettings { SpeechModelPath = Path.Combine(root, "missing"), BarVisible = false, LiveCaptionsEnabled = false, AiPolishEnabled = false }));
        var window = new MainWindow(root, Path.Combine(root, "local"), desktopIntegration: false);
        try { check(window, path); }
        finally { Set(window, "_resumeSpeechAfterGaming", false); Set(window, "_speech", null); window.Close(); WpfFixture.Pump(); Directory.Delete(root, true); }
    }
    static T Control<T>(MainWindow window, string name) where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));
    static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static T GetProperty<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    static T Invoke<T>(MainWindow window, string name, params object?[] args) => (T)Invoke(window, name, args)!;
    static void Complete(Task task) {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(5)) { WpfFixture.Pump(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    sealed class FakeInput : IMicrophoneCapture {
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed { add { } remove { } }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public void Start() => Started.TrySetResult();
        public void Emit(float[] samples) => Samples?.Invoke(samples);
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
    sealed class FakeStream : ISpeechStream {
        public void Accept(float[] samples) { }
        public string Decode() => "First and last words";
        public void InputFinished() { }
        public void Dispose() { }
    }
}
