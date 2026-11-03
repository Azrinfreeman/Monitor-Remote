using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// Native calls live only in an owned worker. Demo mode returns before privilege adjustment.
sealed class WindowsPcPower : IPcPowerExecutor {
    public bool Simulated => Environment.GetEnvironmentVariable("MONITOR_DEMO") == "1";
    public bool SleepSupported => Simulated || OperatingSystem.IsWindows() && NativePcPower.CanSleep();
    public async Task<PcPowerResult> Execute(string action, CancellationToken stopping) {
        if (Simulated) return new("requested", Simulated: true);
        if (!OperatingSystem.IsWindows()) return new("failed", "PC power requires Windows.");
        if (action is not ("shutdown" or "restart" or "sleep")) return new("failed", "Invalid PC power action.");
        return await RunOwnedWorker(action, stopping);
    }
    // Used by the checks executable to prove kill-on-close with a worker that cannot call power APIs.
    internal static Task<PcPowerResult> ProbeOwnership(CancellationToken stopping, Action<int> started) => RunOwnedWorker("probe", stopping, started);
    static async Task<PcPowerResult> RunOwnedWorker(string action, CancellationToken stopping, Action<int>? started = null) {
        using var job = new OwnedJob();
        var gate = Path.Combine(Path.GetTempPath(), "MonitorRemote-power-" + Guid.NewGuid().ToString("N") + ".ready");
        using var process = new Process { StartInfo = WorkerStartInfo(action, gate) };
        try {
            process.Start(); job.Add(process);
            File.WriteAllText(gate, "");
            var ready = await process.StandardOutput.ReadLineAsync(stopping).AsTask().WaitAsync(TimeSpan.FromSeconds(3), stopping);
            if (ready != "ready") return new("unconfirmed", "Power worker did not acknowledge dispatch. Not retried.");
            started?.Invoke(process.Id);
            var output = process.StandardOutput.ReadToEndAsync(stopping);
            var errors = process.StandardError.ReadToEndAsync(stopping);
            // SetSuspendState can remain inside Windows until resume. Keep ownership until then.
            // On stop/crash job disposal terminates the child, without claiming to undo its action.
            await process.WaitForExitAsync(stopping);
            await errors;
            if (process.ExitCode != 0) return new("unconfirmed", "Power worker exited without a reliable result. Not retried.");
            return JsonSerializer.Deserialize<PcPowerResult>(await output) ?? new("unconfirmed", "No power result. Not retried.");
        } catch (Exception e) when (e is IOException or Win32Exception or OperationCanceledException or JsonException or TimeoutException) {
            return new("unconfirmed", "Power dispatch ended without confirmation. Not retried.");
        } finally { if (File.Exists(gate)) File.Delete(gate); }
    }
    internal static ProcessStartInfo WorkerStartInfo(string action, string gate) {
        var executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(WindowsPcPower).Assembly.Location);
        info.ArgumentList.Add("--pc-power-worker"); info.ArgumentList.Add(action); info.ArgumentList.Add(gate);
        return info;
    }
    public static async Task RunWorker(string[] args) {
        if (args.Length != 2 || args[0] is not ("shutdown" or "restart" or "sleep" or "probe")) { Environment.ExitCode = 1; return; }
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(args[1]) && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (!File.Exists(args[1])) { Environment.ExitCode = 1; return; }
        Console.WriteLine("ready"); Console.Out.Flush();
        // Harmless ownership probe: no privilege adjustment or power operation.
        if (args[0] == "probe") { await Task.Delay(Timeout.Infinite); return; }
        if (Environment.GetEnvironmentVariable("MONITOR_DEMO") == "1") { Console.Write(JsonSerializer.Serialize(new PcPowerResult("requested", Simulated: true))); return; }
        try { NativePcPower.Request(args[0]); Console.Write(JsonSerializer.Serialize(new PcPowerResult("requested"))); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { Console.Write(JsonSerializer.Serialize(new PcPowerResult("failed", e.Message))); }
    }
}

static class NativePcPower {
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    // SYSTEM_POWER_CAPABILITIES: BOOLEAN sleep fields at documented byte offsets.
    [StructLayout(LayoutKind.Explicit, Size = 76)] struct PowerCapabilities {
        [FieldOffset(3)] public byte S1;
        [FieldOffset(4)] public byte S2;
        [FieldOffset(5)] public byte S3;
        [FieldOffset(20)] public byte ModernStandby;
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges privileges, uint length, IntPtr previous, IntPtr needed);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool GetPwrCapabilities(out PowerCapabilities capabilities);
    public static bool CanSleep() => GetPwrCapabilities(out var c) && (c.S1 != 0 || c.S2 != 0 || c.S3 != 0 || c.ModernStandby != 0);
    public static void Request(string action) {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("PC power requires Windows.");
        if (action == "sleep" && !CanSleep()) throw new InvalidOperationException("This PC does not report a supported sleep state.");
        const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x08, PrivilegeEnabled = 2;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                throw new InvalidOperationException("Windows policy denied shutdown privilege. No elevation was attempted.");
            // EWX_POWEROFF or EWX_REBOOT, planned application reason; deliberately no force flags.
            var accepted = action == "sleep" ? SetSuspendState(false, false, false) : ExitWindowsEx(action == "shutdown" ? 0x8u : 0x2u, 0x80040000);
            if (!accepted) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { CloseHandle(token); }
    }
}
