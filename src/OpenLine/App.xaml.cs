using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OpenLine;

public partial class App : Application
{
    private const string MutexName = @"Local\OpenLine.SingleInstance";
    private const string ShowEventName = @"Local\OpenLine.Show";

    private MainWindow? _window;
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One instance only: two copies would both feed the same virtual cable.
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Exit();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _window = new MainWindow();

        var ui = DispatcherQueue.GetForCurrentThread();
        var signal = _showSignal;
        var window = _window;
        new Thread(() =>
        {
            while (signal.WaitOne()) ui.TryEnqueue(window.ShowFromTray);
        }) { IsBackground = true, Name = "OpenLine show signal" }.Start();

        bool startInTray = Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTray) _window.Activate();
    }
}
