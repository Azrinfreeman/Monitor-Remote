using System.Runtime.InteropServices;

namespace MonitorRemoteDesktop;

// Minimal notification-area icon over Shell_NotifyIcon, subclassing the main window to receive its callback messages.
sealed class Tray : IDisposable {
    const uint WM_APP_TRAY = 0x8000 + 1, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const uint MF_STRING = 0, MF_GRAYED = 1, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_BOTTOMALIGN = 0x20;
    const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, NIIF_INFO = 1;
    delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData {
        public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    readonly IntPtr window, icon;
    readonly SubclassProc proc;
    bool added;
    public Func<IReadOnlyList<(int Id, string Text, bool Enabled)>> Items { get; set; } = () => [];
    public Action<int>? Command { get; set; }
    public Action? Activate { get; set; }

    public Tray(IntPtr window, string iconPath, string tip) {
        this.window = window;
        icon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
        proc = Handle;
        SetWindowSubclass(window, proc, (UIntPtr)1, UIntPtr.Zero);
        var data = Build(tip, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        added = Shell_NotifyIconW(NIM_ADD, ref data);
    }
    NotifyIconData Build(string tip, uint flags) => new() {
        cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = window, uID = 1, uFlags = flags, uCallbackMessage = WM_APP_TRAY,
        hIcon = icon, szTip = tip.Length > 127 ? tip[..127] : tip, szInfo = "", szInfoTitle = ""
    };
    public void SetTip(string tip) { if (!added) return; var data = Build(tip, NIF_TIP); Shell_NotifyIconW(NIM_MODIFY, ref data); }
    public void Balloon(string title, string text) {
        if (!added) return;
        var data = Build("", NIF_INFO); data.szInfoTitle = title; data.szInfo = text; data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }
    IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data) {
        if (message == WM_APP_TRAY) {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WM_LBUTTONDBLCLK) Activate?.Invoke();
            else if (mouse == WM_RBUTTONUP || mouse == WM_CONTEXTMENU) ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    void ShowMenu() {
        var menu = CreatePopupMenu();
        try {
            foreach (var (itemId, text, enabled) in Items()) {
                if (itemId == 0) AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                else AppendMenuW(menu, MF_STRING | (enabled ? 0 : MF_GRAYED), (UIntPtr)itemId, text);
            }
            GetCursorPos(out var point);
            SetForegroundWindow(window);
            var chosen = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, point.X, point.Y, 0, window, IntPtr.Zero);
            if (chosen > 0) Command?.Invoke(chosen);
        } finally { DestroyMenu(menu); }
    }
    public void Dispose() {
        if (added) { var data = Build("", 0); Shell_NotifyIconW(NIM_DELETE, ref data); added = false; }
        RemoveWindowSubclass(window, proc, (UIntPtr)1);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }
}
