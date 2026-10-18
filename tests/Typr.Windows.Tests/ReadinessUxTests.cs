using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32.SafeHandles;
using Typr.Core;
using Typr.Infrastructure;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

[Collection("WPF")]
public sealed class ReadinessUxTests(WpfFixture fixture)
{
    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("malformed")]
    public void HiddenStartupReusesSharedModelsAcrossProfilesWithoutRewritingUserFiles(string settingsState) => fixture.Run(() => {
        string root = Path.Combine(Path.GetTempPath(), "typr-central-startup-" + Guid.NewGuid().ToString("N"));
        string cpu = Path.Combine(root, "central", "cpu"), gpu = Path.Combine(root, "central", "gpu");
        string catalog = Path.Combine(root, "model-locations.json");
        try {
            foreach (var (name, size) in new (string, long)[] {
                ("encoder.int8.onnx", 652916849), ("decoder.int8.onnx", 7257753),
                ("joiner.int8.onnx", 1735862), ("tokens.txt", 8952)
            }) SparseFile(Path.Combine(cpu, name), size);
            SparseFile(Path.Combine(gpu, NeMoSpeechDownload.ModelFileName), NeMoSpeechDownload.ModelSize);
            foreach (var file in NeMoSpeechDownload.BundleFiles) SparseFile(Path.Combine(gpu, file.RelativePath), file.Size);
            File.WriteAllText(catalog, JsonSerializer.Serialize(new { CpuDirectory = cpu, GpuDirectory = gpu }));
            foreach (string profileName in new[] { "first-install", "updated-install" }) {
                string profile = Path.Combine(root, profileName), local = Path.Combine(profile, "local");
                Directory.CreateDirectory(local);
                string settings = Path.Combine(profile, "settings.json"), history = Path.Combine(local, "history.json");
                string? original = settingsState == "missing" ? null : settingsState == "malformed" ? "unreadable settings" :
                    SettingsMigrator.Save(new AppSettings { SpeechModelPath = Path.Combine(root, "old-install", "cpu"),
                        GpuSpeechModelPath = Path.Combine(root, "old-install", "gpu") });
                if (original != null) File.WriteAllText(settings, original);
                File.WriteAllText(history, "unreadable history preserved");
                var window = new MainWindow(profile, local, desktopIntegration: false, modelCatalogPath: catalog);
                try {
                    Assert.Equal(cpu, Control<TextBox>(window, "ModelPathBox").Text);
                    Assert.Equal(gpu, Control<TextBox>(window, "GpuModelPathBox").Text);
                    Assert.Equal("Prepare speech", Control<Button>(window, "ReadinessActionButton").Content);
                    Assert.False(window.IsVisible);
                    Assert.Null(Get<SpeechSession?>(window, "_speech"));
                    Assert.False(Get<bool>(window, "_inputActive"));
                    Assert.Equal(original, File.Exists(settings) ? File.ReadAllText(settings) : null);
                    Assert.Equal("unreadable history preserved", File.ReadAllText(history));
                }
                finally { window.Close(); WpfFixture.Pump(); }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact] public void InstalledUnloadedModelOffersPreparationWithoutOpeningInput() => fixture.Run(() => {
        WithWindow((window, root) => {
            var model = Path.Combine(root, "model"); Directory.CreateDirectory(model);
            foreach (var (name, size) in new (string, long)[] {
                ("encoder.int8.onnx", 652916849), ("decoder.int8.onnx", 7257753),
                ("joiner.int8.onnx", 1735862), ("tokens.txt", 8952)
            }) {
                using var file = File.Create(Path.Combine(model, name));
                Assert.True(DeviceIoControl(file.SafeFileHandle, 0x000900C4 /* FSCTL_SET_SPARSE */,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
                    "Couldn't create sparse readiness fixture: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                file.SetLength(size);
            }
            Control<TextBox>(window, "ModelPathBox").Text = model;
            Invoke(window, "RefreshReadiness");
            Assert.Contains("Prepare speech", (string)Control<Button>(window, "HeroButton").Content);
            Assert.Equal("Prepare speech", Control<Button>(window, "ReadinessActionButton").Content);
            Assert.Contains("installed", Control<TextBlock>(window, "ReadinessTitle").Text);
            Assert.Contains("microphone off", Control<TextBlock>(window, "ReadinessDetail").Text);
            Assert.Null(Get<SpeechSession?>(window, "_speech"));
            Assert.False(Get<bool>(window, "_inputActive"));
            Set(window, "_speechFaulted", true); Invoke(window, "RefreshReadiness");
            Assert.Equal("Prepare speech", Control<Button>(window, "ReadinessActionButton").Content);
        });
    });

    [Fact] public void PreparedShortcutOnlyHeroExplainsHowToTalkWithoutOpeningInput() => fixture.Run(() => {
        WithWindow((window, _) => {
            int opens = 0;
            using var speech = new SpeechSession(() => new FakeStream(), () => { ++opens; throw new InvalidOperationException("No input should open."); });
            Set(window, "_speech", speech); Invoke(window, "RefreshReadiness");
            Assert.Equal("Try the shortcut  →", Control<Button>(window, "HeroButton").Content);
            Assert.Equal("Open setup", Control<Button>(window, "ReadinessActionButton").Content);
            Invoke(window, "Hero_Click", Control<Button>(window, "HeroButton"), new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>(window, "WorkspacePage").Visibility);
            Assert.Contains("Hold Ctrl + Alt", Control<TextBlock>(window, "ShortcutStatus").Text);
            Assert.False(Get<bool>(window, "_recording")); Assert.Equal(0, opens);
            Invoke(window, "Hero_Click", Control<Button>(window, "ReadinessActionButton"), new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>(window, "SettingsPage").Visibility);
            Assert.Equal(0, opens);
            Set(window, "_speech", null);
        });
    });

    [Fact] public void MissingModelGoesToSetupAndExplainsOneTimeDownload() => fixture.Run(() => {
        WithWindow((window, _) => {
            Assert.Equal("Open setup", Control<Button>(window, "ReadinessActionButton").Content);
            Assert.Contains("Download", Control<TextBlock>(window, "ReadinessDetail").Text);
            Invoke(window, "Hero_Click", Control<Button>(window, "ReadinessActionButton"), new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, Control<FrameworkElement>(window, "SettingsPage").Visibility);
            Assert.Null(Get<SpeechSession?>(window, "_speech"));
        });
    });

    private static void WithWindow(Action<MainWindow, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "typr-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), SettingsMigrator.Save(new AppSettings {
            SpeechModelPath = Path.Combine(root, "missing-model"), LiveCaptionsEnabled = false, BarVisible = false
        }));
        var window = new MainWindow(root, Path.Combine(root, "local"), desktopIntegration: false);
        try { check(window, root); Assert.False(window.IsVisible); }
        finally { Set(window, "_speech", null); window.Close(); WpfFixture.Pump(); Directory.Delete(root, true); }
    }
    private static T Control<T>(MainWindow window, string name) where T : class => Assert.IsAssignableFrom<T>(window.FindName(name));
    private static void SparseFile(string path, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        Assert.True(DeviceIoControl(file.SafeFileHandle, 0x000900C4 /* FSCTL_SET_SPARSE */,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "Couldn't create sparse model fixture.");
        file.SetLength(size);
    }
    private static void Set(MainWindow window, string field, object? value) => typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static T Get<T>(MainWindow window, string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string method, params object?[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
    private sealed class FakeStream : ISpeechStream
    {
        public void Accept(float[] samples) { }
        public void InputFinished() { }
        public string Decode() => "";
        public void Dispose() { }
    }
}
