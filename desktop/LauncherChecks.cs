using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

static class LauncherChecks {
    public static async Task<int> Run() {
        var results = new List<string>();
        void Check(bool condition, string message) {
            if (!condition) throw new Exception(message);
            results.Add("PASS: " + message);
        }
        var previous = Environment.GetEnvironmentVariable("MONITOR_DEMO");
        var previousPower = Environment.GetEnvironmentVariable("MONITOR_PC_POWER_FILE");
        var previousMuteMarker = Environment.GetEnvironmentVariable("MONITOR_MUTE_MARKER");
        var powerRoot = Path.Combine(Path.GetTempPath(), "MonitorRemote-LauncherChecks-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MONITOR_PC_POWER_FILE", Path.Combine(powerRoot, "pc-power.json"));
        Environment.SetEnvironmentVariable("MONITOR_MUTE_MARKER", Path.Combine(powerRoot, "mute-marker.txt"));
        Environment.SetEnvironmentVariable("MONITOR_DEMO", "1");
        using var server = new ServerController();
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new TcpListener(IPAddress.Any, port);
        listener.ExclusiveAddressUse = true;
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(3) };
        async Task<HttpResponseMessage> Pair(string code) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pair") {
                Content = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", http.BaseAddress!.ToString().TrimEnd('/'));
            request.Headers.Add("X-Monitor-Request", "1");
            return await http.SendAsync(request);
        }
        try {
            var status = await server.Start(port);
            Check(status.Running && status.Code?.Length == 6, "readiness reports authoritative pairing state");
            Check(!status.AllowViewing && !status.AllowInput && !status.AllowAudio, "remote permissions disabled on launch");
            Check(!status.AllowPcPower && status.PowerTask == null && status.PowerError == null, "PC power disabled with isolated empty storage");
            Check((await server.Send("pc-power:on")).AllowPcPower && !(await server.Send("pc-power:off")).AllowPcPower, "independent PC power permission switch");
            bool commandConflict = false;
            try { await server.Send("pc-power:cancel:missing:1"); } catch (ManagementCommandException) { commandConflict = true; }
            Check(commandConflict && (await server.Send("status")).Running, "expected power conflict preserves management connection");
            Check((await http.GetAsync("/")).IsSuccessStatusCode, "packaged static assets served");
            using (var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                using var timeout = new CancellationTokenSource(3000);
                await pipe.ConnectAsync(timeout.Token);
                await ManagementProtocol.Write(pipe, new ManagementRequest(1, "wrong", "revoke"), timeout.Token);
                var denied = await ManagementProtocol.Read<ManagementReply>(pipe, timeout.Token);
                Check(denied.Error == "Unauthorized" && denied.Code == null, "unauthorized management cannot access pairing code");
            }
            Check((await server.Send("status")).Code == status.Code, "unauthorized command did not rotate code");
            Check((await Pair(status.Code!)).IsSuccessStatusCode, "pairing through packaged server");
            Check((await server.Send("view:on")).AllowViewing && (await server.Send("input:on")).AllowInput && (await server.Send("audio:on")).AllowAudio, "local switches enable independent feature permissions");
            var audioResponse = await http.GetAsync("/api/audio/devices");
            Check(audioResponse.IsSuccessStatusCode && (await audioResponse.Content.ReadAsStringAsync()).Contains("demo-microphone"), "packaged synthetic audio endpoints available");
            var renewed = await server.Send("renew");
            Check(renewed.AllowViewing && renewed.AllowInput && renewed.AllowAudio, "renewal preserves local permissions");
            Check(renewed.Code != status.Code && renewed.Expires > status.Expires, "renewal rotates code and resets expiry");
            Check((await http.GetAsync("/api/status")).IsSuccessStatusCode, "renewal preserves existing sessions");
            Check((await Pair(status.Code!)).StatusCode == HttpStatusCode.Unauthorized, "old code invalid after renewal");
            var revoked = await server.Send("revoke");
            Check(revoked.Code != renewed.Code, "revocation rotates code");
            Check((await http.GetAsync("/api/status")).StatusCode == HttpStatusCode.Unauthorized, "revocation invalidates sessions");
            Check((await Pair(revoked.Code!)).IsSuccessStatusCode, "new code pairs after revocation");
            using (var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) {
                using var timeout = new CancellationTokenSource(3000);
                await pipe.ConnectAsync(timeout.Token);
                try { await pipe.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 4097)), timeout.Token); }
                catch (IOException) { /* The server may close immediately at the size limit. */ }
            }
            Check((await server.Send("status")).Running, "oversized message leaves management available");
            await server.Send("pc-power:on");
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/pc-power/tasks") {
                Content = new StringContent(JsonSerializer.Serialize(new { requestId = Guid.NewGuid().ToString("D"), action = "shutdown", mode = "delay", delaySeconds = 600 }), Encoding.UTF8, "application/json")
            }) {
                request.Headers.Add("Origin", http.BaseAddress!.ToString().TrimEnd('/')); request.Headers.Add("X-Monitor-Request", "1");
                Check((await http.SendAsync(request)).StatusCode == HttpStatusCode.Accepted && (await server.Send("status")).PowerTask?.State == "pending", "packaged power task appears in local management");
            }
            await server.Stop();
            listener.Start();
            Check(!server.Running, "graceful stop frees port and releases process");
            bool conflict = false;
            try { await server.Start(port); } catch { conflict = true; }
            Check(conflict && !server.Running, "occupied port reports failure and cleans up owned process");
            listener.Stop();
            var restarted = await server.Start(port);
            Check(!restarted.AllowViewing && !restarted.AllowInput && !restarted.AllowAudio, "restart resets local permissions");
            Check(!restarted.AllowPcPower && restarted.PowerTask?.State == "paused", "restart restores saved power task paused with permission off");
            var enabled = await server.Send("pc-power:on");
            Check(enabled.PowerTask?.State == "paused", "enabling power does not resume saved task");
            var saved = enabled.PowerTask!;
            var resumed = await server.Send($"pc-power:resume:{saved.Id}:{saved.Revision}");
            Check(resumed.PowerTask?.State == "pending", "local management explicitly resumes saved task");
            Check((await server.Send($"pc-power:cancel:{saved.Id}:{resumed.PowerTask!.Revision}")).PowerTask == null, "local management cancels resumed task");
            Check((await http.GetAsync("/api/status")).StatusCode == HttpStatusCode.Unauthorized, "restart revokes old sessions");
            server.Dispose();
            await Task.Delay(500);
            listener.Start();
            Check(true, "closing job terminates owned server and frees port");
            listener.Stop();
            results.Add("No real capture, mouse/keyboard injection, audio changes or physical monitor commands were issued. GUI, iPhone, sign-in startup and forced launcher crash need manual trials.");
            return 0;
        } catch (Exception error) { results.Add("FAIL: " + error); return 1; }
        finally {
            await server.Stop();
            Environment.SetEnvironmentVariable("MONITOR_DEMO", previous);
            Environment.SetEnvironmentVariable("MONITOR_PC_POWER_FILE", previousPower);
            Environment.SetEnvironmentVariable("MONITOR_MUTE_MARKER", previousMuteMarker);
            if (Path.GetDirectoryName(powerRoot) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(powerRoot).StartsWith("MonitorRemote-LauncherChecks-") && Directory.Exists(powerRoot)) Directory.Delete(powerRoot, true);
            var report = Environment.GetEnvironmentVariable("MONITOR_CHECK_REPORT") ?? Path.Combine(AppContext.BaseDirectory, "launcher-check-results.txt");
            File.WriteAllLines(report, results);
        }
    }
}
