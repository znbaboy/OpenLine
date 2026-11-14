using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenLine.Core.Shell;

/// <summary>
/// Minimal notification-area icon. Create it on the UI thread: its hidden window is serviced by
/// that thread's message loop, so events arrive on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WmAppTray = 0x8000 + 1;
    private const uint WmNull = 0x0000;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const uint NimAdd = 0, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmBottomAlign = 0x20, TpmNoNotify = 0x80, TpmReturnCmd = 0x100;
    private const uint ImageIcon = 1, LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;
    private const int CmdShow = 1, CmdExit = 2;

    private readonly WndProc _proc;
    private readonly string _className;
    private readonly IntPtr _instance;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly string _tooltip;
    private readonly uint _taskbarCreated;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public TrayIcon(string iconPath, string tooltip)
    {
        _proc = WindowProc;
        _tooltip = tooltip;
        _className = "OpenLineTray_" + Environment.ProcessId;
        _instance = GetModuleHandleW(IntPtr.Zero);

        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = _instance,
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

        _hwnd = CreateWindowExW(0, _className, "OpenLine", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadImageW(IntPtr.Zero, iconPath, ImageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
        AddIcon();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = NewData();
        Shell_NotifyIconW(NimDelete, ref data);
        DestroyWindow(_hwnd);
        UnregisterClassW(_className, _instance);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private void AddIcon()
    {
        var data = NewData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = WmAppTray;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        Shell_NotifyIconW(NimAdd, ref data);
    }

    private NotifyIconData NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmAppTray)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            if (evt == WmLButtonUp) ShowRequested?.Invoke();
            else if (evt == WmRButtonUp) ShowMenu();
            return IntPtr.Zero;
        }
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            AddIcon(); // Explorer restarted
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MfString, CmdShow, "Show OpenLine");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, CmdExit, "Exit");
            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
            int cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmNoNotify | TpmBottomAlign, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (cmd == CmdShow) ShowRequested?.Invoke();
            else if (cmd == CmdExit) ExitRequested?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(IntPtr name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
}

/// <summary>Per-user "start with Windows" via the Run key. Portable-friendly: re-points if the folder moves.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenLine";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If enabled but pointing at an old location, update it.</summary>
    public static void Repair()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            key.SetValue(ValueName, Command);
    }
}
