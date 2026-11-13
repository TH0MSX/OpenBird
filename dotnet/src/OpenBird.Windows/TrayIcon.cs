using System.Runtime.InteropServices;

namespace OpenBird.Windows;

/// <summary>
/// Notification-area icon with a native menu and balloon notifications.
/// Create and dispose it on the UI thread: its hidden window receives its
/// messages through that thread's message loop.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// One menu entry. <see cref="Shortcut"/> is shown right-aligned, <see cref="Default"/> draws it bold
    /// (what a double-click does), and <see cref="Children"/> makes it open a submenu.
    /// </summary>
    public sealed record MenuItem(string Text, Action OnClick, bool Separator = false, bool Checked = false, bool Enabled = true,
        bool Default = false, string? Shortcut = null, IReadOnlyList<MenuItem>? Children = null);

    private const uint WM_APP_TRAY = 0x8000 + 1;
    private const uint WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_NULL = 0x0000;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint MF_STRING = 0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800, MF_BYPOSITION = 0x400;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
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
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private readonly WndProc _wndProc;          // kept alive for as long as the window exists
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private bool _ownsIcon;
    private readonly uint _taskbarCreated;
    private readonly Action _onOpen;
    private readonly Func<IReadOnlyList<MenuItem>> _menu;
    private string _tooltip;
    private bool _disposed;

    public TrayIcon(string tooltip, string? iconPath, Action onOpen, Func<IReadOnlyList<MenuItem>> menu)
    {
        _tooltip = tooltip;
        _onOpen = onOpen;
        _menu = menu;
        _wndProc = HandleMessage;

        var instance = GetModuleHandleW(null);
        var className = "OpenBirdTray_" + Environment.ProcessId;
        var wc = new WNDCLASSW { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc), hInstance = instance, lpszClassName = className };
        if (RegisterClassW(ref wc) == 0) throw new InvalidOperationException($"Tray window class failed (Windows error {Marshal.GetLastWin32Error()}).");
        _hwnd = CreateWindowExW(0, className, "OpenBird tray", 0, 0, 0, 0, 0, new IntPtr(-3) /* message-only */, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"Tray window failed (Windows error {Marshal.GetLastWin32Error()}).");

        (_icon, _ownsIcon) = LoadIcon(iconPath);

        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        Add();
    }

    private static (IntPtr Icon, bool Owned) LoadIcon(string? iconPath)
    {
        if (iconPath is not null && File.Exists(iconPath))
        {
            var loaded = LoadImageW(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, 0, 0, 0x10 /* LR_LOADFROMFILE */ | 0x40 /* LR_DEFAULTSIZE */);
            if (loaded != IntPtr.Zero) return (loaded, true);
        }
        return (LoadIconW(IntPtr.Zero, new IntPtr(32512)), false);      // stock application icon
    }

    /// <summary>Swaps the notification-area icon, for example to show that recording is running.</summary>
    public void SetIcon(string? iconPath)
    {
        if (_disposed) return;
        var (icon, owned) = LoadIcon(iconPath);
        var (previous, hadOwned) = (_icon, _ownsIcon);
        (_icon, _ownsIcon) = (icon, owned);
        var data = Data(NIF_ICON);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
        if (hadOwned) DestroyIcon(previous);
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = _hwnd, uID = 1, uFlags = flags,
        uCallbackMessage = WM_APP_TRAY, hIcon = _icon, szTip = _tooltip, szInfo = "", szInfoTitle = "",
    };

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    public void SetTooltip(string tooltip)
    {
        if (_disposed || tooltip == _tooltip) return;
        _tooltip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        var data = Data(NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Shows a Windows notification from the tray icon.</summary>
    public void Notify(string title, string message)
    {
        if (_disposed) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        data.szInfo = message.Length > 250 ? message[..250] : message;
        data.dwInfoFlags = 1;   // NIIF_INFO
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private IntPtr HandleMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK) _onOpen();
            else if (mouse == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0) Add();     // Explorer restarted: put the icon back
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = _menu();
        var actions = new List<Action>();
        var menu = BuildMenu(items, actions);
        try
        {
            GetCursorPos(out var at);
            SetForegroundWindow(_hwnd);                                // so the menu closes when the user clicks elsewhere
            var chosen = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, at.X, at.Y, 0, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, UIntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= actions.Count) actions[(int)chosen - 1]();
        }
        finally { DestroyMenu(menu); }      // also destroys the submenus attached to it
    }

    private static IntPtr BuildMenu(IReadOnlyList<MenuItem> items, List<Action> actions)
    {
        var menu = CreatePopupMenu();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Separator) { AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null); continue; }
            var text = item.Shortcut is null ? item.Text : item.Text + "\t" + item.Shortcut;
            var flags = MF_STRING | (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED);
            if (item.Children is { Count: > 0 })
            {
                var sub = BuildMenu(item.Children, actions);
                AppendMenuW(menu, flags | MF_POPUP, (UIntPtr)(ulong)sub.ToInt64(), text);
                continue;
            }
            actions.Add(item.OnClick);
            AppendMenuW(menu, flags, (UIntPtr)(uint)actions.Count, text);
            if (item.Default) SetMenuDefaultItem(menu, (uint)i, 1);
        }
        return menu;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        DestroyWindow(_hwnd);
        if (_ownsIcon) DestroyIcon(_icon);
    }
}
