using System.Windows;

namespace Ava.Windows;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _registration;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--paste-target", StringComparer.OrdinalIgnoreCase))
        {
            MainWindow = new PasteTargetWindow();
            MainWindow.Show();
            return;
        }
        _mutex = new Mutex(true, @"Local\AvaVoice.Instance", out _ownsMutex);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AvaVoice.ShowWindow");
        if (!_ownsMutex)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }), null, Timeout.Infinite, false);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _registration?.Unregister(null);
        _showEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
