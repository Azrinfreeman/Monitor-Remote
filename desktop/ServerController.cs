using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

sealed class ManagementCommandException(string message) : Exception(message);
sealed class ServerController : IDisposable {
    Process? process;
    OwnedJob? job;
    string pipe = "", secret = "";
    public bool Running => process is { HasExited: false };
    public event Action? Exited;
    internal string PipeName => pipe;
    public string ServerPath => Path.Combine(AppContext.BaseDirectory, "server", "MonitorRemote.exe");
    public async Task<ManagementReply> Start(int port, Preferences? prefs = null) {
        if (Running) return await Send("status");
        Dispose();
        if (!File.Exists(ServerPath)) throw new FileNotFoundException("Bundled server missing. Run package.ps1 and launch the packaged desktop app.", ServerPath);
        pipe = "MonitorRemote-" + Guid.NewGuid().ToString("N");
        secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var info = new ProcessStartInfo(ServerPath) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(ServerPath)!
        };
        info.ArgumentList.Add("--contentRoot");
        info.ArgumentList.Add(Path.GetDirectoryName(ServerPath)!);
        info.Environment["MONITOR_PORT"] = port.ToString();
        if (!string.IsNullOrWhiteSpace(prefs?.TlsCertificatePath)) {
            info.Environment["MONITOR_TLS_CERT"] = prefs.TlsCertificatePath;
            info.Environment["MONITOR_TLS_PASSWORD"] = prefs.TlsCertificatePassword ?? "";
            info.Environment["MONITOR_TLS_PORT"] = prefs.TlsPort.ToString();
            info.Environment["MONITOR_ALLOW_HTTP"] = prefs.AllowPlainHttp ? "1" : "0";
        }
        if (!string.IsNullOrWhiteSpace(prefs?.AllowedHosts)) info.Environment["MONITOR_ALLOWED_HOSTS"] = prefs.AllowedHosts;
        info.Environment["MONITOR_MANAGEMENT_PIPE"] = pipe;
        info.Environment["MONITOR_MANAGEMENT_SECRET"] = secret;
        // The child waits for job assignment before it can create workers.
        var releaseFile = Path.Combine(Path.GetTempPath(), "MonitorRemote-" + Guid.NewGuid().ToString("N") + ".ready");
        info.Environment["MONITOR_LAUNCH_GATE"] = releaseFile;
        try {
            job = new OwnedJob();
            process = Process.Start(info) ?? throw new IOException("Could not start server.");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Exited?.Invoke();
            job.Add(process);
            File.WriteAllText(releaseFile, "");
            _ = Drain(process.StandardOutput);
            _ = Drain(process.StandardError);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!timeout.IsCancellationRequested && Running) {
                try { return await Send("status", timeout.Token); }
                catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException) {
                    await Task.Delay(100, timeout.Token);
                }
            }
            throw new IOException("Server did not become ready. The port may be occupied; choose another port or stop the other server.");
        } catch {
            await Stop();
            throw;
        } finally {
            if (File.Exists(releaseFile)) File.Delete(releaseFile);
        }
    }
    async Task Drain(StreamReader reader) {
        try {
            // Retain no raw server output: it can contain request URLs and credentials.
            while (await reader.ReadLineAsync() is not null) { }
        } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public async Task<ManagementReply> Send(string command, CancellationToken cancellation = default) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        await ManagementProtocol.Write(client, new ManagementRequest(1, secret, command), timeout.Token);
        var reply = await ManagementProtocol.Read<ManagementReply>(client, timeout.Token);
        if (reply.Version != 1) throw new IOException("Unsupported management response.");
        if (reply.Error != null) throw new ManagementCommandException(reply.Error);
        return reply;
    }
    public async Task Stop() {
        if (Running) {
            try { await Send("shutdown"); } catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await process!.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (Running) process!.Kill(true); await process!.WaitForExitAsync(); }
        }
        Dispose();
    }
    public void Dispose() {
        job?.Dispose(); job = null;
        process?.Dispose(); process = null;
        secret = ""; pipe = "";
    }
}
