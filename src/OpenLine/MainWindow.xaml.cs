using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OpenLine.Core.Audio;
using OpenLine.Core.Settings;
using OpenLine.Core.Shell;
using OpenLine.Core.ViewModels;
using Windows.Graphics;

namespace OpenLine;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainPage _page;
    private readonly DispatcherQueueTimer _timer;
    private readonly TrayIcon? _tray;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(new AudioEngine(), new SettingsStore(), new UiDispatcher(DispatcherQueue));
        _page = new MainPage(_vm);
        Content = _page;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_page.TitleBarElement);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "OpenLine.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        PlaceWindow();
        AppWindow.Closing += OnClosing;

        try
        {
            _tray = new TrayIcon(icon, "OpenLine");
            _tray.ShowRequested += ShowFromTray;
            _tray.ExitRequested += () => _ = ExitAsync();
        }
        catch (Exception)
        {
            _tray = null; // no tray: closing the window exits
        }

        // Drives meters, wires, saving, device recovery and app re-attach — even while hidden.
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) =>
        {
            _vm.Tick();
            if (AppWindow.IsVisible) _page.RenderFrame();
        };
        _timer.Start();

        _ = InitializeAsync();
    }

    public void ShowFromTray()
    {
        if (_exiting) return;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            _vm.ErrorMessage = $"OpenLine couldn't start audio: {ex.Message}";
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        if (_vm.CloseToTray && _tray is not null)
        {
            sender.Hide();
            return;
        }
        await ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        _tray?.Dispose();
        AppWindow.Hide();
        await _vm.DisposeAsync();
        Application.Current.Exit();
    }

    private void PlaceWindow()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1200 * scale), area.Width);
        int h = Math.Min((int)(800 * scale), area.Height);
        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(1000 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

internal sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action) => queue.TryEnqueue(() => action());
}
