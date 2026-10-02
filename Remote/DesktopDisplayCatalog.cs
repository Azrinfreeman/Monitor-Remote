using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

record DesktopDisplay(string Id, string Name, int X, int Y, int Width, int Height, bool Primary);
record DisplaySnapshot(string Revision, DesktopDisplay[] Displays);

static class DesktopDisplayCatalog {
    public static bool Demo => Environment.GetEnvironmentVariable("MONITOR_DEMO") == "1";
    public static void InitializeDpi() {
        if (OperatingSystem.IsWindows()) SetProcessDpiAwarenessContext(new IntPtr(-4));
    }
    public static DisplaySnapshot Snapshot() {
        DesktopDisplay[] displays;
        if (Demo) displays = [new("demo-primary", "Demo desktop", 0, 0, 1280, 720, true), new("demo-left", "Demo second display", -1920, 0, 1920, 1080, false)];
        else {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Desktop viewing requires Windows.");
            var list = new List<DesktopDisplay>();
            MonitorCallback callback = (handle, _, _, _) => {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(handle, ref info) && info.Monitor.Right > info.Monitor.Left && info.Monitor.Bottom > info.Monitor.Top)
                    list.Add(new(info.Device, info.Device.Replace(@"\\.\", "") + ((info.Flags & 1) != 0 ? " · Primary" : ""),
                        info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0));
                return true;
            };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new InvalidOperationException("Windows display discovery failed.");
            displays = list.OrderByDescending(d => d.Primary).ThenBy(d => d.Id, StringComparer.Ordinal).ToArray();
        }
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(displays))))[..24];
        return new(revision, displays);
    }
    public static bool Interactive() {
        if (Demo) return true;
        if (!OperatingSystem.IsWindows()) return false;
        if (!WTSQuerySessionInformation(IntPtr.Zero, Process.GetCurrentProcess().SessionId, 8, out var state, out var bytes)) return false;
        try { if (bytes < 4 || Marshal.ReadInt32(state) != 0) return false; }
        finally { WTSFreeMemory(state); }
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && name.ToString() == "Default";
        } finally { CloseDesktop(desktop); }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfo {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr handle, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int size, out int needed);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")] static extern bool WTSQuerySessionInformation(IntPtr server, int session, int info, out IntPtr buffer, out int size);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buffer);
}
