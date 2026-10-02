using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

sealed class DesktopCaptureService : IDisposable {
    readonly NamedPipeServerStream pipe;
    readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    Process? worker;
    bool connected;
    public DesktopCaptureService() {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Screen capture requires Windows.");
        var name = "MonitorRemote-Capture-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var path = Environment.ProcessPath!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(DesktopCaptureService).Assembly.Location);
        info.ArgumentList.Add("--capture-worker");
        info.Environment["MONITOR_CAPTURE_PIPE"] = name; info.Environment["MONITOR_CAPTURE_SECRET"] = secret;
        try {
            worker = Process.Start(info) ?? throw new IOException("Capture worker could not start.");
            _ = Drain(worker.StandardOutput); _ = Drain(worker.StandardError);
        } catch { pipe.Dispose(); throw; }
    }
    static async Task Drain(StreamReader reader) {
        try { while (await reader.ReadLineAsync() is not null) { } }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public async Task<CapturedFrame> Next(DesktopDisplay display, string revision, string quality, CancellationToken token) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(3000);
        if (!connected) { await pipe.WaitForConnectionAsync(timeout.Token); connected = true; }
        await ManagementProtocol.Write(pipe, new CaptureRequest(secret, display.Id, revision, quality), timeout.Token);
        return RemoteProtocol.Decode(await RemoteProtocol.ReadPacket(pipe, timeout.Token));
    }
    public void Dispose() {
        pipe.Dispose();
        if (worker != null) {
            try { if (!worker.HasExited) worker.Kill(true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            worker.Dispose(); worker = null;
        }
    }
}
