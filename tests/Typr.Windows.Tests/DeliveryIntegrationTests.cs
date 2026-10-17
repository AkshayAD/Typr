using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// These tests explicitly send synthetic text only to the disposable --paste-target
// process. They serialize with the WPF tests and restore materialized clipboard data.
[Collection("WPF")]
public sealed class DeliveryIntegrationTests(WpfFixture fixture) : IDisposable
{
    readonly string logDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Typr-delivery-log-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (System.IO.Directory.Exists(logDirectory)) System.IO.Directory.Delete(logDirectory, recursive: true); }
    [Fact] public Task RichClipboardPastesIntoSeparateProcessAndRestoresEveryFormat() => OnUi(async () => {
        using var owner = new ClipboardOwner();
        using var clipboard = new ClipboardScope();
        clipboard.SeedRichData();
        using var target = await ExternalTarget.StartAsync();
        var captured = await ClipboardDelivery.CaptureTargetAsync(target.Handle, CancellationToken.None);
        Assert.True(captured.Editable, "The separate WPF text field must be recognized as writable.");
        const string text = "Synthetic native paste — café\r\nSecond line";
        var result = await new ClipboardDelivery(logDirectory).DeliverAsync(text, captured, CancellationToken.None);
        Assert.True(result.Confirmed, result.Message);
        Assert.Equal(text, await target.ReadAsync());
        Assert.Equal("Synthetic clipboard seed", Clipboard.GetText());
        Assert.Equal("<html><body>Synthetic HTML seed</body></html>", Clipboard.GetData(DataFormats.Html));
        Assert.Equal(new[] { @"C:\TyprSynthetic\example.txt" }, Clipboard.GetFileDropList().Cast<string>());
        Assert.True(Clipboard.ContainsData(clipboard.Marker));
    });

    [Fact] public Task BusyClipboardReturnsCopyRecoveryWithoutTypingOrChangingClipboard() => OnUi(async () => {
        using var owner = new ClipboardOwner();
        using var clipboard = new ClipboardScope();
        clipboard.SeedRichData();
        using var target = await ExternalTarget.StartAsync();
        var captured = await ClipboardDelivery.CaptureTargetAsync(target.Handle, CancellationToken.None);
        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        using (var locked = new ClipboardLock()) {
            var result = await new ClipboardDelivery(logDirectory).DeliverAsync("Must never arrive", captured, CancellationToken.None);
            Assert.False(result.Confirmed);
            Assert.Contains("clipboard", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Copy text", result.Message);
            await Task.Delay(300);
            Assert.Empty(await target.ReadAsync());
            Assert.Equal(sequence, NativeMethods.GetClipboardSequenceNumber());
        }
        Assert.Equal("Synthetic clipboard seed", Clipboard.GetText());
        Assert.True(Clipboard.ContainsData(clipboard.Marker));
    });

    [Fact] public Task CancelledDeliveryDoesNotSendLaterOrChangeTheClipboard() => OnUi(async () => {
        using var owner = new ClipboardOwner();
        using var clipboard = new ClipboardScope();
        clipboard.SeedRichData();
        using var target = await ExternalTarget.StartAsync();
        var captured = await ClipboardDelivery.CaptureTargetAsync(target.Handle, CancellationToken.None);
        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(15);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClipboardDelivery(logDirectory).DeliverAsync("Must never arrive", captured, cancellation.Token));
        await Task.Delay(900);
        Assert.Empty(await target.ReadAsync());
        Assert.Equal(sequence, NativeMethods.GetClipboardSequenceNumber());
    });

    [Fact] public Task ChangedFocusIsNotStolenBackAndTrackerExcludesOwnEditors() => OnUi(async () => {
        using var owner = new ClipboardOwner();
        using var target = await ExternalTarget.StartAsync();
        var tracker = new ExternalTargetTracker();
        var captured = await tracker.RefreshAsync();
        Assert.NotNull(captured);
        Assert.Equal(target.Handle, captured.Window);
        var editor = new TextBox { Text = "Own app editor" };
        owner.Window.Content = editor;
        await FocusOwnWindow(owner, editor);
        Assert.Equal(owner.Handle, NativeMethods.GetForegroundWindow());
        Assert.Equal(captured, await tracker.RefreshAsync());
        var result = await new ClipboardDelivery(logDirectory).DeliverAsync("Must never arrive", captured, CancellationToken.None);
        Assert.False(result.Confirmed);
        Assert.Equal(owner.Handle, NativeMethods.GetForegroundWindow());
        Assert.Empty(await target.ReadAsync());
        Assert.Equal("Own app editor", editor.Text);
        tracker.Clear();
        Assert.Null(await tracker.RefreshAsync());
    });

    [Fact] public Task ForegroundCaptureWaitsForBusyPollGateAndCapturesFreshExternalField() => OnUi(async () => {
        using var owner = new ClipboardOwner();
        using var target = await ExternalTarget.StartAsync();
        var gate = (SemaphoreSlim)typeof(ClipboardDelivery).GetField("CaptureGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        bool held = true;
        try {
            var capture = ClipboardDelivery.CaptureTargetAsync(target.Handle, CancellationToken.None, 1500);
            await Task.Delay(80);
            Assert.False(capture.IsCompleted); // A poll collision must wait, not return an unconfirmed target.
            gate.Release(); held = false;
            var captured = await capture.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(captured.Editable);
            Assert.NotNull(captured.RuntimeId);
            Assert.Equal(target.Handle, captured.Window);
            Assert.Equal(1, gate.CurrentCount);
        }
        finally { if (held) gate.Release(); }
    });

    [Fact] public Task CaptureGateTimeoutAndCancellationDoNotQueueLateWorkers() => OnUi(async () => {
        using var owner = new ClipboardOwner();
        using var target = await ExternalTarget.StartAsync();
        var gate = (SemaphoreSlim)typeof(ClipboardDelivery).GetField("CaptureGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        try {
            var elapsed = Stopwatch.StartNew();
            var captured = await ClipboardDelivery.CaptureTargetAsync(target.Handle, CancellationToken.None, 150);
            Assert.False(captured.Editable);
            Assert.Null(captured.RuntimeId);
            Assert.InRange(elapsed.ElapsedMilliseconds, 75, 1000);
            Assert.Equal(0, gate.CurrentCount);
            using var cancellation = new CancellationTokenSource(50);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ClipboardDelivery.CaptureTargetAsync(target.Handle, cancellation.Token, 1500));
            Assert.Equal(0, gate.CurrentCount);
        }
        finally { gate.Release(); }
        await Task.Delay(200);
        Assert.Equal(1, gate.CurrentCount); // No timed-out waiter can later take ownership.
        Assert.True((await ClipboardDelivery.CaptureTargetAsync(target.Handle, CancellationToken.None, 1000)).Editable);
        Assert.Equal(1, gate.CurrentCount);
    });

    [Fact] public Task KeyboardPasteLastCapturesCurrentFieldWhileBarStillRejectsStaleTrackedField() => OnUi(async () => {
        using var owner = new ClipboardOwner();
        using var clipboard = new ClipboardScope();
        clipboard.SeedRichData();
        using var target = await ExternalTarget.StartAsync();
        var captured = await ClipboardDelivery.CaptureTargetAsync(target.Handle, CancellationToken.None);
        Assert.True(captured.Editable);
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Typr-keyboard-paste-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(System.IO.Path.Combine(directory, "settings"), System.IO.Path.Combine(directory, "local"), desktopIntegration: false);
        try {
            const string text = "Synthetic fresh keyboard repeat paste";
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("_latest", hidden)!.SetValue(window, text);
            var tracker = (ExternalTargetTracker)typeof(MainWindow).GetField("_externalTargets", hidden)!.GetValue(window)!;
            var stale = captured with { RuntimeId = [int.MinValue, 42] };
            typeof(ExternalTargetTracker).GetField("current", hidden)!.SetValue(tracker, stale);
            // Model a periodic refresh already in flight: RefreshAsync returns
            // Current immediately, even when its runtime identity is outdated.
            typeof(ExternalTargetTracker).GetField("refreshing", hidden)!.SetValue(tracker, 1);
            Assert.Same(stale, await tracker.RefreshAsync());
            var command = typeof(MainWindow).GetMethod("CompanionActionAsync", hidden)!;
            await (Task)command.Invoke(window, ["PasteLast", true])!;
            Assert.Empty(await target.ReadAsync());
            Assert.Same(stale, tracker.Current);
            await (Task)command.Invoke(window, ["PasteLast", false])!;
            Assert.Equal(text, await target.ReadAsync());
            Assert.Contains("Pasted and verified", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.Equal(target.Handle, NativeMethods.GetForegroundWindow());
            Assert.Equal("Synthetic clipboard seed", Clipboard.GetText());
            Assert.True(Clipboard.ContainsData(clipboard.Marker));
            Assert.False(window.IsVisible);
        }
        finally {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    });

    [Fact] public Task UiaDocumentAllowsWritableTextAndRejectsReadonlyAndPasswords() => OnUi(async () => {
        using var owner = new ClipboardOwner();
        var document = new RichTextBox { Document = new FlowDocument(new Paragraph(new Run("Synthetic document"))) };
        var password = new PasswordBox();
        var panel = new StackPanel(); panel.Children.Add(document); panel.Children.Add(password);
        owner.Window.Content = panel;
        await FocusOwnWindow(owner, document);
        var editable = await ClipboardDelivery.CaptureTargetAsync(owner.Handle, CancellationToken.None);
        var diagnostics = await Task.Run(() => {
            var element = AutomationElement.FocusedElement;
            element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern);
            return $"type={element.Current.ControlType.ProgrammaticName}; password={element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true)}; readOnly={(pattern as TextPattern)?.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute)}; foreground={NativeMethods.GetForegroundWindow() == owner.Handle}; focused={element.Current.HasKeyboardFocus}";
        });
        Assert.True(editable.Editable, "An editable UIA Document/TextPattern must be accepted. " + diagnostics);
        document.IsReadOnly = true;
        var readOnly = await ClipboardDelivery.CaptureTargetAsync(owner.Handle, CancellationToken.None);
        Assert.False(readOnly.Editable);
        var rejected = await new ClipboardDelivery(logDirectory).DeliverAsync("Must never arrive", editable, CancellationToken.None);
        Assert.False(rejected.Confirmed);
        password.Focus();
        await Task.Delay(50);
        var secret = await ClipboardDelivery.CaptureTargetAsync(owner.Handle, CancellationToken.None);
        Assert.True(secret.Password);
        Assert.False(secret.Editable);
        Assert.Null(secret.Value);
        Assert.False((await new ClipboardDelivery(logDirectory).DeliverAsync("Must never arrive", secret, CancellationToken.None)).Confirmed);
        Assert.Empty(password.Password);
    });

    async Task OnUi(Func<Task> action)
    {
        Task task = Task.CompletedTask;
        fixture.Run(() => task = action());
        await task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    static async Task FocusOwnWindow(ClipboardOwner owner, IInputElement element)
    {
        owner.Window.Show();
        var currentThread = NativeMethods.GetCurrentThreadId();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attached = foregroundThread != currentThread && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try { NativeMethods.SetForegroundWindow(owner.Handle); owner.Window.Activate(); }
        finally { if (attached) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false); }
        System.Windows.Input.Keyboard.Focus(element);
        await Task.Delay(100);
        Assert.Equal(owner.Handle, NativeMethods.GetForegroundWindow());
    }

    sealed class ClipboardOwner : IDisposable
    {
        readonly Window? previous = Application.Current.MainWindow;
        public Window Window { get; } = new() { Title = "Typr isolated delivery test", Width = 500, Height = 300, ShowInTaskbar = false };
        public nint Handle { get; }
        public ClipboardOwner() { Application.Current.MainWindow = Window; Handle = new WindowInteropHelper(Window).EnsureHandle(); }
        public void Dispose() { Window.Close(); Application.Current.MainWindow = previous; }
    }

    sealed class ClipboardScope : IDisposable
    {
        readonly ClipboardSnapshot original;
        bool seeded;
        public string Marker { get; } = "Typr.SyntheticClipboardTest." + Guid.NewGuid().ToString("N");
        public ClipboardScope() => Assert.True(SafeClipboard.TryCapture(out original!), "The existing clipboard must be safely materialized before a test changes it.");
        public void SeedRichData()
        {
            var data = new DataObject();
            data.SetText("Synthetic clipboard seed", TextDataFormat.UnicodeText);
            data.SetData(DataFormats.Html, "<html><body>Synthetic HTML seed</body></html>");
            data.SetFileDropList(new StringCollection { @"C:\TyprSynthetic\example.txt" });
            data.SetData(Marker, "synthetic marker");
            Clipboard.SetDataObject(data, true);
            seeded = true;
        }
        public void Dispose()
        {
            // A copy made by the user during a test always wins over restoration.
            if (seeded && Clipboard.ContainsData(Marker))
                Assert.NotEqual(ClipboardRestoreResult.BusyOrFailed,
                    SafeClipboard.RestoreIfOwned(original, NativeMethods.GetClipboardSequenceNumber()));
        }
    }

    sealed class ClipboardLock : IDisposable
    {
        readonly ManualResetEventSlim release = new();
        readonly Thread thread;
        public ClipboardLock()
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            thread = new Thread(() => {
                bool opened = OpenClipboard(0);
                ready.SetResult(opened);
                if (opened) { release.Wait(); CloseClipboard(); }
            }) { IsBackground = true };
            thread.Start();
            Assert.True(ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }
        public void Dispose() { release.Set(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); release.Dispose(); }
    }

    sealed class ExternalTarget : IDisposable
    {
        readonly Process process;
        readonly AutomationElement editor;
        public nint Handle { get; }
        ExternalTarget(Process process, nint handle, AutomationElement editor) { this.process = process; Handle = handle; this.editor = editor; }
        public static async Task<ExternalTarget> StartAsync()
        {
            // ProjectReference copies a self-contained app's runtimeconfig but not its
            // whole runtime. Use the test host's installed WindowsDesktop framework.
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            foreach (var argument in new[] { "exec", "--runtimeconfig", System.IO.Path.Combine(AppContext.BaseDirectory, "Typr.Windows.Tests.runtimeconfig.json"),
                "--depsfile", System.IO.Path.Combine(AppContext.BaseDirectory, "Typr.Windows.Tests.deps.json"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "Typr.dll"), "--paste-target" }) start.ArgumentList.Add(argument);
            var process = Process.Start(start)!;
            try {
                var deadline = Stopwatch.StartNew();
                while (process.MainWindowHandle == 0 && deadline.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(50); process.Refresh(); }
                nint handle = process.MainWindowHandle;
                Assert.NotEqual(0, handle);
                Assert.NotEqual(Environment.ProcessId, process.Id);
                NativeMethods.SetForegroundWindow(handle);
                var editor = await Task.Run(() => AutomationElement.FromHandle(handle).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Typr external paste validation target")));
                Assert.NotNull(editor);
                await Task.Run(editor.SetFocus);
                for (int i = 0; i < 20 && NativeMethods.GetForegroundWindow() != handle; i++) { NativeMethods.SetForegroundWindow(handle); await Task.Delay(50); }
                Assert.Equal(handle, NativeMethods.GetForegroundWindow());
                DeliveryTarget focused = new(handle, null, null, false, false);
                for (int i = 0; i < 20; i++) {
                    focused = await ClipboardDelivery.CaptureTargetAsync(handle, CancellationToken.None);
                    if (focused.Editable) break;
                    await Task.Delay(50);
                }
                Assert.True(focused.Editable, "The disposable target must have a confirmed writable field before sending any input.");
                return new ExternalTarget(process, handle, editor);
            }
            catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
        }
        public Task<string> ReadAsync() => Task.Run(() => ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        public void Dispose() { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) process.Kill(); } process.Dispose(); }
    }

    [DllImport("user32.dll")] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
}
