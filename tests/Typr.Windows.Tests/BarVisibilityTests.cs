using System.IO;
using System.Reflection;
using Typr.Core;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// Inspect the presentation policy on hidden windows without enabling desktop integration.
[Collection("WPF")]
public sealed class BarVisibilityTests(WpfFixture fixture)
{
    [Fact] public void HiddenBarAppearsForPendingRecordingAndActualInputThenRestoresHiddenPreference() => fixture.Run(() => WithWindow((window, path) => {
        Assert.False(Visible(window));
        using var starting = new CancellationTokenSource();
        Set(window, "_recordingStart", starting);
        Assert.True(Visible(window));
        Set(window, "_recordingStart", null); Set(window, "_recording", true);
        Assert.True(Visible(window));
        Set(window, "_inputActive", true); Set(window, "_recording", false);
        Assert.True(Visible(window)); // Keep showing feedback until the microphone really closes.
        Set(window, "_inputActive", false);
        Assert.False(Visible(window));
        Set(window, "_busy", true);
        Assert.False(Visible(window)); // Unrelated background work does not reveal a hidden bar.
        Set(window, "_busy", false);
        Assert.False(Settings(window).BarVisible);
        Assert.False(SettingsMigrator.Load(File.ReadAllText(path)).BarVisible);
        Assert.False(window.IsVisible);
    }));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GamingHidesIdleBarAndRestoresTheSavedEverydayPreference(bool preferredVisible) => fixture.Run(() => WithWindow((window, _) => {
        Set(window, "_settings", Settings(window) with { BarVisible = preferredVisible, SpeechResourceMode = "Gaming" });
        Assert.False(Visible(window));
        Set(window, "_recording", true);
        Assert.True(Visible(window));
        Set(window, "_recording", false);
        Assert.False(Visible(window));
        Set(window, "_settings", Settings(window) with { SpeechResourceMode = "Everyday" });
        Assert.Equal(preferredVisible, Visible(window));
        Assert.Equal(preferredVisible, Settings(window).BarVisible);
    }));

    private static void WithWindow(Action<MainWindow, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "typr-bar-visibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, SettingsMigrator.Save(new AppSettings {
            SpeechModelPath = Path.Combine(root, "missing"), BarVisible = false, LiveCaptionsEnabled = false
        }));
        var window = new MainWindow(root, Path.Combine(root, "local"), desktopIntegration: false);
        try { check(window, path); }
        finally {
            Set(window, "_recordingStart", null); Set(window, "_recording", false); Set(window, "_inputActive", false); Set(window, "_busy", false);
            window.Close(); WpfFixture.Pump(); Directory.Delete(root, true);
        }
    }
    private static bool Visible(MainWindow window) => (bool)typeof(MainWindow).GetProperty("ShouldShowDictationBar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static AppSettings Settings(MainWindow window) => (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
}
