using System.Runtime.InteropServices;
using System.Text.Json;

static class Hardware {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Physical { public IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }
    delegate bool Callback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, Callback callback, IntPtr data);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] Physical[] physical);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr monitor);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr monitor, uint value);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint SetThreadExecutionState(uint flags);
    public static void Run(string[] args) {
        var monitors = new List<Physical>();
        try {
            if (Environment.GetEnvironmentVariable("MONITOR_DEMO") == "1") {
                Console.WriteLine(args[0] == "list" ? "[{\"id\":\"0\",\"name\":\"Demo monitor\",\"supported\":true,\"brightness\":50}]" : args[0] == "brightness" ? JsonSerializer.Serialize(new { requested = int.Parse(args[2]), confirmed = false, brightness = (int?)null, simulated = true }) : "{\"requested\":true,\"confirmed\":false,\"simulated\":true}");
                return;
            }
            if (!OperatingSystem.IsWindows()) throw new Exception("Monitor control requires Windows.");
            if (args[0] == "power") {
                if (args[1] == "wake") {
                    // ES_DISPLAY_REQUIRED turns the display on and resets its idle timer.
                    // Omit ES_CONTINUOUS: this worker exits immediately, and the normal
                    // Windows display timeout should apply after this one-time reset.
                    if (SetThreadExecutionState(0x00000002) == 0) throw new Exception("Windows display wake request failed.");
                } else if (args[1] == "off") {
                    if (SendMessageTimeout(new IntPtr(0xffff), 0x112, new IntPtr(0xf170), new IntPtr(2), 2, 2000, out _) == IntPtr.Zero) throw new Exception("Windows display power request failed.");
                } else throw new Exception("Use off or wake.");
                Console.WriteLine("{\"requested\":true,\"confirmed\":false}"); return;
            }
            Callback callback = (m, _, _, _) => { if (GetNumberOfPhysicalMonitorsFromHMONITOR(m, out var count) && count > 0) { var items = new Physical[count]; if (GetPhysicalMonitorsFromHMONITOR(m, count, items)) monitors.AddRange(items); } return true; };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new Exception("Cannot enumerate displays.");
            if (args[0] == "list") {
                var result = monitors.Select((m, index) => { var ok = GetMonitorBrightness(m.Handle, out var min, out var current, out var max) && max > min; return new { id = index.ToString(), name = m.Description, supported = ok, brightness = ok ? (int?)Math.Round(100d * (current - min) / (max - min)) : null }; }).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(result));
            } else {
                var index = int.Parse(args[1]); if (index < 0 || index >= monitors.Count) throw new Exception("Monitor disconnected. Refresh the monitor list.");
                var monitor = monitors[index];
                if (!GetMonitorBrightness(monitor.Handle, out var min, out _, out var max) || max <= min) throw new Exception("Brightness unavailable. Enable DDC/CI in the monitor menu; some docks and laptop displays are unsupported.");
                var value = min + (uint)Math.Round((max - min) * int.Parse(args[2]) / 100d);
                if (!SetMonitorBrightness(monitor.Handle, value)) throw new Exception("Monitor rejected brightness request.");
                var readback = GetMonitorBrightness(monitor.Handle, out var actualMin, out var actual, out var actualMax) && actualMax > actualMin;
                var confirmed = readback && actual == value;
                var brightness = readback ? (int?)Math.Round(100d * (actual - actualMin) / (actualMax - actualMin)) : null;
                Console.WriteLine(JsonSerializer.Serialize(new { requested = int.Parse(args[2]), confirmed, brightness }));
            }
        } catch (Exception e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
        finally { foreach (var m in monitors) DestroyPhysicalMonitor(m.Handle); }
    }
}
