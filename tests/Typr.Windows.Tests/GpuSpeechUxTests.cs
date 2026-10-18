using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Typr.Core;
using Typr.Infrastructure;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// Hidden presentation checks only: no GPU runtime, microphone, native input, or downloads.
[Collection("WPF")]
public sealed class GpuSpeechUxTests(WpfFixture fixture)
{
    [Fact] public void InitialGpuChoicesAndExplicitChangesPreserveOtherPreferencesWithoutOpeningInput() => fixture.Run(() => WithWindow((window, path) => {
        string original = File.ReadAllText(path);
        Assert.Equal(1, Control<ComboBox>(window, "SpeechAccelerationBox").SelectedIndex);
        Assert.Equal(1, Control<ComboBox>(window, "GpuSpeechLatencyBox").SelectedIndex);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.True(Control<Button>(window, "DownloadGpuModelButton").IsEnabled);
        Control<ComboBox>(window, "SpeechAccelerationBox").SelectedIndex = 0;
        Control<ComboBox>(window, "GpuSpeechLatencyBox").SelectedIndex = 0;
        var saved = SettingsMigrator.Load(File.ReadAllText(path));
        Assert.Equal("Automatic", saved.SpeechAcceleration); Assert.Equal("Quality", saved.GpuSpeechLatency);
        Assert.Equal(4, saved.CpuThreads); Assert.Equal(55, saved.SavedAudioLimit);
        Assert.True(saved.SaveHistory); Assert.False(saved.AiPolishEnabled); Assert.False(saved.AllowButtonRecording);
        Assert.Null(Get<SpeechSession?>(window, "_speech")); Assert.False(Get<bool>(window, "_recording"));
        Assert.False(Get<bool>(window, "_inputActive")); Assert.False(window.IsVisible);
    }));

    [Theory]
    [InlineData("_loading")] [InlineData("_recording")] [InlineData("_busy")] [InlineData("_resourceTransitioning")]
    public void ActiveSpeechWorkLocksGpuPreferencesAndDownload(string field) => fixture.Run(() => WithWindow((window, _) => {
        Set(window, field, true); Invoke(window, "RefreshGpuSpeech"); AssertLocked(window);
        Set(window, field, false); Invoke(window, "RefreshGpuSpeech");
        Assert.True(Control<ComboBox>(window, "SpeechAccelerationBox").IsEnabled);
    }));

    [Fact] public void PreparedSessionReportsActualBackendWithoutAnIdleMicrophoneClaim() => fixture.Run(() => WithWindow((window, _) => {
        int inputs = 0;
        using var speech = new SpeechSession(() => new FakeStream(), () => { inputs++; throw new InvalidOperationException("No microphone should be opened."); });
        try {
            Set(window, "_speech", speech); Invoke(window, "RefreshReadiness");
            AssertLocked(window);
            Assert.Contains(speech.BackendDescription, Control<TextBlock>(window, "GpuBackendStatus").Text);
            Assert.DoesNotContain("microphone", speech.BackendDescription, StringComparison.OrdinalIgnoreCase);
            Set(window, "_recording", true); Set(window, "_inputActive", true); Invoke(window, "RefreshReadiness");
            Assert.DoesNotContain("microphone off", Control<TextBlock>(window, "GpuBackendStatus").Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, inputs);
        } finally { Set(window, "_speech", null); Set(window, "_recording", false); Set(window, "_inputActive", false); }
    }));

    [Fact] public void GamingAndPendingShortcutPreparationLockGpuPreferences() => fixture.Run(() => WithWindow((window, _) => {
        using var pending = new CancellationTokenSource();
        Set(window, "_recordingStart", pending); Invoke(window, "RefreshGpuSpeech"); AssertLocked(window);
        Set(window, "_recordingStart", null);
        var settings = Get<AppSettings>(window, "_settings");
        Set(window, "_settings", settings with { SpeechResourceMode = "Gaming" });
        Invoke(window, "RefreshGpuSpeech"); AssertLocked(window);
        Assert.Contains("paused for gaming", Control<TextBlock>(window, "GpuBackendStatus").Text);
        Set(window, "_settings", settings); Invoke(window, "RefreshGpuSpeech");
        Assert.True(Control<TextBox>(window, "GpuModelPathBox").IsEnabled);
    }));

    [Fact] public void RelativeGpuPathCannotDownloadOrOverwriteTheSavedAbsoluteFolder() => fixture.Run(() => WithWindow((window, path) => {
        string savedDirectory = SettingsMigrator.Load(File.ReadAllText(path)).GpuSpeechModelPath!;
        var box = Control<TextBox>(window, "GpuModelPathBox"); box.Text = @"relative\gpu";
        Assert.False(Control<Button>(window, "DownloadGpuModelButton").IsEnabled);
        Assert.Contains("absolute GPU model folder", Control<TextBlock>(window, "GpuBackendStatus").Text);
        var focus = new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, box, null);
        Invoke(window, "GpuModelPath_LostFocus", box, focus);
        Assert.Equal(savedDirectory, SettingsMigrator.Load(File.ReadAllText(path)).GpuSpeechModelPath);
        box.Text = Path.Combine(Path.GetDirectoryName(path)!, "chosen-gpu");
        Assert.True(Control<Button>(window, "DownloadGpuModelButton").IsEnabled);
        Invoke(window, "GpuModelPath_LostFocus", box, focus);
        Assert.Equal(box.Text, SettingsMigrator.Load(File.ReadAllText(path)).GpuSpeechModelPath);
        Assert.Null(Get<SpeechSession?>(window, "_speech"));
    }));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DownloadOwnershipKeepsTheOtherDownloadDisabledAndPauseTargetsOnlyGpu(bool gpuOwner) => fixture.Run(() => WithWindow((window, _) => {
        using var download = new CancellationTokenSource();
        try {
            Set(window, "_download", download); Set(window, "_gpuDownloading", gpuOwner);
            if (gpuOwner) Invoke(window, "SetSetupEnabled", true); // A generic setup refresh cannot unlock the CPU download.
            else Invoke(window, "RefreshGpuSpeech");
            AssertLocked(window);
            Assert.Equal(!gpuOwner, Control<Button>(window, "DownloadButton").IsEnabled);
            Assert.Equal(gpuOwner ? Visibility.Visible : Visibility.Collapsed, Control<Button>(window, "CancelGpuDownloadButton").Visibility);
            Invoke(window, "CancelGpuDownload_Click", window, new RoutedEventArgs());
            Assert.Equal(gpuOwner, download.IsCancellationRequested);
        } finally { Set(window, "_download", null); Set(window, "_gpuDownloading", false); }
        Invoke(window, "SetSetupEnabled", true);
        Assert.True(Control<Button>(window, "DownloadGpuModelButton").IsEnabled);
        Assert.True(Control<Button>(window, "DownloadButton").IsEnabled);
    }));

    private static void AssertLocked(MainWindow window)
    {
        Assert.False(Control<ComboBox>(window, "SpeechAccelerationBox").IsEnabled);
        Assert.False(Control<ComboBox>(window, "GpuSpeechLatencyBox").IsEnabled);
        Assert.False(Control<TextBox>(window, "GpuModelPathBox").IsEnabled);
        Assert.False(Control<Button>(window, "DownloadGpuModelButton").IsEnabled);
    }
    private static void WithWindow(Action<MainWindow, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "typr-gpu-ux-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, SettingsMigrator.Save(new AppSettings {
            SpeechModelPath = Path.Combine(root, "missing-cpu"), GpuSpeechModelPath = Path.Combine(root, "missing-gpu"),
            SpeechAcceleration = "CPU", GpuSpeechLatency = "Fast", CpuThreads = 4, SavedAudioLimit = 55,
            SaveHistory = true, AiPolishEnabled = false, BarVisible = false, LiveCaptionsEnabled = false
        }));
        var window = new MainWindow(root, Path.Combine(root, "local"), desktopIntegration: false);
        try { check(window, path); Assert.False(window.IsVisible); }
        finally {
            Set(window, "_recordingStart", null); Set(window, "_download", null); Set(window, "_speech", null);
            window.Close(); WpfFixture.Pump(); Directory.Delete(root, true);
        }
    }
    private static T Control<T>(MainWindow window, string name) where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private sealed class FakeStream : ISpeechStream
    {
        public void Accept(float[] samples) { }
        public void InputFinished() { }
        public string Decode() => "";
        public void Dispose() { }
    }
}
