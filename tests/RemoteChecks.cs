using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.FirstOrDefault() == "--read-audio") {
    Environment.SetEnvironmentVariable("MONITOR_DEMO", null);
    using var audio = new AudioControlService();
    var snapshot = await audio.Run(devices => devices.Snapshot(), CancellationToken.None);
    Console.WriteLine($"Windows audio read only: {snapshot.Devices.Length} endpoints, {snapshot.Devices.Count(d => d.Direction == "output")} outputs, {snapshot.Devices.Count(d => d.Direction == "input")} inputs, {snapshot.Devices.Count(d => d.Supported)} support volume. No changes made.");
    return;
}
Environment.SetEnvironmentVariable("MONITOR_DEMO", "1");
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS: " + name); }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is FeatureException or InvalidOperationException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }

var clock = new CheckClock();
var pairing = new PairingState(clock);
Check(pairing.Pair("test", pairing.Status().Code!, out var owner) == 200, "test pairing");
using (var remote = new RemoteSessionManager(pairing, false, false, false)) {
    var snapshot = DesktopDisplayCatalog.Snapshot(); var display = snapshot.Displays[1];
    Check(NativeInput.Map(display, 0, 0) == (-1920, 0) && NativeInput.Map(display, 1, 1) == (-1, 1079), "negative-origin display mapping and inclusive bounds");
    Reject(() => NativeInput.Map(display, double.NaN, 0), "nonfinite coordinate rejection");
    Reject(() => remote.Start(owner!, new(display.Id, snapshot.Revision)), "viewing disabled by default");
    remote.Manage("view:on"); remote.Manage("input:on"); remote.Manage("audio:on");
    var current = remote.Start(owner!, new(display.Id, snapshot.Revision));
    remote.Configure(owner!, new(current.Id, true));
    Reject(() => remote.Start(owner!, new(display.Id, snapshot.Revision)), "single remote session ownership");
    Reject(remote.BeginPowerOff, "display off rejected during viewing");
    var frame = remote.Frame(current, Stopwatch.GetTimestamp());
    remote.Handle(current, new("button", 1, FrameSequence: frame, Revision: snapshot.Revision, Button: "left", Down: true));
    Check(((JsonElement)JsonSerializer.SerializeToElement(remote.DemoStatus())).GetProperty("held").GetInt32() == 1, "simulated drag button held");
    remote.Manage("input:off");
    Check(JsonSerializer.SerializeToElement(remote.DemoStatus()).GetProperty("held").GetInt32() == 0, "permission removal releases drag");
    Reject(() => remote.Handle(current, new("click", 2, FrameSequence: frame, Revision: snapshot.Revision, Button: "left")), "input permission enforced during active socket");
    remote.Manage("input:on"); remote.Configure(owner!, new(current.Id, true));
    Reject(() => remote.Handle(current, new("click", 3, FrameSequence: frame + 100, Revision: snapshot.Revision, Button: "left")), "unknown frame rejected");
    Reject(() => remote.Handle(current, new("click", 3, FrameSequence: frame, Revision: snapshot.Revision, Button: "left")), "duplicate command rejected without replay");
    var oldFrame = remote.Frame(current, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3);
    Reject(() => remote.Handle(current, new("click", 4, FrameSequence: oldFrame, Revision: snapshot.Revision, Button: "left")), "stale frame rejected");
    frame = remote.Frame(current, Stopwatch.GetTimestamp());
    remote.Handle(current, new("text", 5, FrameSequence: frame, Revision: snapshot.Revision, Text: "Hello 👋\n世界"));
    Check(true, "Unicode surrogate pairs and multiline input accepted in simulation");
    Reject(() => remote.Handle(current, new("text", 6, FrameSequence: frame, Revision: snapshot.Revision, Text: "\ud800")), "invalid surrogate rejected");
    Reject(() => remote.Handle(current, new("text", 7, FrameSequence: frame, Revision: snapshot.Revision, Text: new string('a', 1025))), "text length bounded");
    pairing.Renew(false); Check(remote.Active, "pairing renewal preserves sharing");
    pairing.Logout(owner!); Check(!remote.Active && !remote.AudioAllowed(owner!), "logout ends sharing and audio access");
}
pairing = new PairingState(clock); pairing.Pair("test", pairing.Status().Code!, out owner);
using (var remote = new RemoteSessionManager(pairing, true, true, true)) {
    var snapshot = DesktopDisplayCatalog.Snapshot(); remote.Start(owner!, new(snapshot.Displays[0].Id, snapshot.Revision));
    clock.Now = clock.Now.AddHours(13); await Task.Delay(250);
    Check(!remote.Active && !remote.AudioAllowed(owner!), "open remote session expires with pairing cookie");
}

var serverPath = args.FirstOrDefault() ?? Path.GetFullPath("bin/Debug/net10.0/MonitorRemote.dll");
await using (var fixture = await Fixture.Start(serverPath)) {
    Check(!(await fixture.Management("status")).AllowViewing, "server starts with feature permissions disabled");
    Check((await fixture.Http.GetAsync("/api/audio/devices")).StatusCode == HttpStatusCode.Unauthorized, "unauthorized audio discovery rejected");
    await fixture.Pair();
    Check((await fixture.Http.GetAsync("/api/audio/devices")).StatusCode == HttpStatusCode.Forbidden, "paired phone still needs local audio permission");
    await fixture.Management("audio:on");
    var audio = await fixture.Http.GetFromJsonAsync<AudioSnapshot>("/api/audio/devices", RemoteProtocol.Json);
    Check(audio?.Devices.Length == 5 && audio.Simulated, "all synthetic audio endpoints listed without hardware access");
    var revision = audio!.Revision;
    Check((await fixture.Post("/api/audio/devices/demo-speakers/volume", new { value = 101, revision })).StatusCode == HttpStatusCode.BadRequest, "audio percentage validation");
    Check((await fixture.Post("/api/audio/devices/demo-speakers/mute", new { revision })).StatusCode == HttpStatusCode.BadRequest, "explicit mute state required");
    Check((await fixture.Post("/api/audio/devices/demo-speakers/volume", new { value = 30, revision = "old" })).StatusCode == HttpStatusCode.Conflict, "stale audio catalog rejected");
    Check((await fixture.Post("/api/audio/devices/demo-headphones/volume", new { value = 30, revision, requireDefault = true })).StatusCode == HttpStatusCode.Conflict, "default target change cannot alter another device");
    var result = await fixture.Post("/api/audio/devices/demo-headphones/volume", new { value = 20, revision });
    var response = await result.Content.ReadFromJsonAsync<JsonElement>();
    Check(response.GetProperty("confirmed").GetBoolean() && response.GetProperty("results")[0].GetProperty("device").GetProperty("muted").GetBoolean(), "volume write confirmed and mute preserved");
    await fixture.Management("sound:on");
    using (var quiet = fixture.Socket()) {
        await quiet.ConnectAsync(fixture.SocketUrl("../audio/stream?mutePc=1&format=pcm16"), CancellationToken.None);
        var firstPcm = await Fixture.ReceiveBytes(quiet);
        var nextPcm = await Fixture.ReceiveBytes(quiet);
        Check(firstPcm.Length == 9 + 960 * 2 * 2 && firstPcm[0] == 2 && BitConverter.ToUInt32(firstPcm, 1) == 0 && BitConverter.ToUInt32(nextPcm, 1) == 1 && BitConverter.ToUInt32(nextPcm, 5) == 960, "PCM fallback carries complete ordered 48 kHz stereo frames");
        Check(BitConverter.ToInt16(firstPcm, 9 + 24 * 4) > 7000 && BitConverter.ToInt16(firstPcm, 9 + 24 * 4) == BitConverter.ToInt16(firstPcm, 11 + 24 * 4), "PCM fallback carries signed little-endian stereo sound samples");
        var during = await fixture.Http.GetFromJsonAsync<AudioSnapshot>("/api/audio/devices", RemoteProtocol.Json);
        Check(during!.Devices.First(d => d.Id == "demo-speakers").Muted == true, "listening can mute the PC speakers");
        await quiet.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
    var restored = false;
    for (var i = 0; i < 40 && !restored; i++) {
        await Task.Delay(50);
        var after = await fixture.Http.GetFromJsonAsync<AudioSnapshot>("/api/audio/devices", RemoteProtocol.Json);
        restored = after!.Devices.First(d => d.Id == "demo-speakers").Muted == false;
    }
    Check(restored, "PC speakers are unmuted again when listening ends");
    await fixture.Management("sound:off");
    var apps = await fixture.Http.GetFromJsonAsync<AudioSessionSnapshot>("/api/audio/sessions", RemoteProtocol.Json);
    Check(apps?.Sessions.Length == 3 && apps.Simulated, "synthetic application sessions listed without touching real apps");
    Check((await fixture.Post("/api/audio/sessions/demo-browser/volume", new { value = 101, revision = apps!.Revision })).StatusCode == HttpStatusCode.BadRequest, "application volume validation");
    Check((await fixture.Post("/api/audio/sessions/demo-browser/volume", new { value = 10, revision = "old" })).StatusCode == HttpStatusCode.Conflict, "stale application list rejected");
    Check((await fixture.Post("/api/audio/sessions/missing/volume", new { value = 10, revision = apps.Revision })).StatusCode == HttpStatusCode.NotFound, "ended application session rejected");
    var appResult = await (await fixture.Post("/api/audio/sessions/demo-browser/volume", new { value = 12, revision = apps.Revision })).Content.ReadFromJsonAsync<JsonElement>();
    Check(appResult.GetProperty("confirmed").GetBoolean() && appResult.GetProperty("session").GetProperty("volume").GetDouble() == 12, "application volume applied with readback");
    appResult = await (await fixture.Post("/api/audio/sessions/demo-music/mute", new { muted = true, revision = apps.Revision })).Content.ReadFromJsonAsync<JsonElement>();
    Check(appResult.GetProperty("confirmed").GetBoolean() && appResult.GetProperty("session").GetProperty("volume").GetDouble() == 35, "application mute keeps its volume");
    foreach (var format in new[] { "", "?format=pcm16" }) {
        using var soundDenied = fixture.Socket();
        var refused = false; try { await soundDenied.ConnectAsync(fixture.SocketUrl("../audio/stream" + format), CancellationToken.None); } catch (WebSocketException) { refused = true; }
        Check(refused, "sound stream refused until sound permission is enabled");
    }
    await fixture.Management("sound:on");
    using (var listen = fixture.Socket()) {
        await listen.ConnectAsync(fixture.SocketUrl("../audio/stream"), CancellationToken.None);
        var first = await Fixture.ReceiveBytes(listen); var second = await Fixture.ReceiveBytes(listen);
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 2); var pcm = new short[960 * 2];
        var samples = decoder.Decode(second.AsSpan(9), pcm, 960);
        Check(first[0] == 1 && BitConverter.ToUInt32(first, 1) == 0 && BitConverter.ToUInt32(second, 1) == 1 && BitConverter.ToUInt32(second, 5) == 960, "sound packets carry ordered sequence and timestamps");
        Check(samples == 960 && pcm.Any(x => Math.Abs(x) > 500), "sound packets decode as Opus audio");
        using var second_ = fixture.Socket(); var busy = false; try { await second_.ConnectAsync(fixture.SocketUrl("../audio/stream"), CancellationToken.None); } catch (WebSocketException) { busy = true; }
        Check(busy, "only one sound listener at a time");
        await fixture.Management("sound:off");
        var soundEnded = false; try { for (var i = 0; i < 200 && !soundEnded; i++) { var data = await Fixture.ReceiveBytes(listen); if (data.Length > 0 && data[0] == (byte)'{') soundEnded = true; } } catch (Exception e) when (e is WebSocketException or Exception) { soundEnded = true; }
        Check(soundEnded, "turning sound permission off ends the stream");
    }
    result = await fixture.Post("/api/audio/outputs/volume", new { value = 33, revision, deviceIds = new[] { "demo-speakers", "demo-headphones", "demo-unsupported" } });
    response = await result.Content.ReadFromJsonAsync<JsonElement>();
    Check(!response.GetProperty("confirmed").GetBoolean() && response.GetProperty("results").GetArrayLength() == 3 && response.GetProperty("results")[0].GetProperty("confirmed").GetBoolean(), "bulk audio partial failure reported per endpoint");
    audio = await fixture.Http.GetFromJsonAsync<AudioSnapshot>("/api/audio/devices", RemoteProtocol.Json);
    Check(audio!.Devices.Single(d => d.Id == "demo-microphone").Volume == 75, "all-output action leaves microphones unchanged");
    Check((await fixture.Post("/api/audio/outputs/mute", new { muted = true, revision, deviceIds = new[] { "demo-microphone" } })).StatusCode == HttpStatusCode.Conflict, "output bulk action rejects microphone target");
    await fixture.Management("audio:off");
    Check((await fixture.Post("/api/audio/devices/demo-speakers/volume", new { value = 30, revision })).StatusCode == HttpStatusCode.Forbidden, "revoked audio permission prevents writes");
    await fixture.Management("view:on"); await fixture.Management("input:on");
    var displays = await fixture.Http.GetFromJsonAsync<JsonElement>("/api/remote/displays");
    var display = displays.GetProperty("snapshot").GetProperty("displays")[0];
    var start = new { displayId = display.GetProperty("id").GetString(), revision = displays.GetProperty("snapshot").GetProperty("revision").GetString(), quality = "balanced" };
    response = await (await fixture.Post("/api/remote/session", start)).Content.ReadFromJsonAsync<JsonElement>();
    var id = response.GetProperty("remoteSessionId").GetString()!;
    Check((await fixture.Post("/api/remote/session", start)).StatusCode == HttpStatusCode.Conflict, "second session cannot take over");
    Check((await fixture.Post("/api/displays/power", new { state = "off" })).StatusCode == HttpStatusCode.Conflict, "server power protection while sharing");
    using var badOrigin = fixture.Socket("http://evil.invalid");
    bool denied = false;
    try { await badOrigin.ConnectAsync(fixture.SocketUrl("control"), new CancellationTokenSource(2000).Token); } catch (WebSocketException) { denied = true; }
    Check(denied, "wrong-Origin WebSocket rejected before upgrade");
    using var control = fixture.Socket(); using var frames = fixture.Socket();
    await control.ConnectAsync(fixture.SocketUrl("control"), CancellationToken.None);
    var bindBytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "bind", remoteSessionId = id });
    await control.SendAsync(bindBytes.AsMemory(0, 10), WebSocketMessageType.Text, false, CancellationToken.None);
    await control.SendAsync(bindBytes.AsMemory(10), WebSocketMessageType.Text, true, CancellationToken.None);
    await RemoteProtocol.Send(control, new { type = "heartbeat" }, CancellationToken.None);
    await frames.ConnectAsync(fixture.SocketUrl("frames"), CancellationToken.None);
    await RemoteProtocol.Send(frames, new { type = "bind", remoteSessionId = id }, CancellationToken.None);
    using var heartbeats = new CancellationTokenSource();
    var pulse = Task.Run(async () => {
        try { while (true) { await Task.Delay(750, heartbeats.Token); await RemoteProtocol.Send(control, new { type = "heartbeat" }, heartbeats.Token); } }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
    });
    var packet = await Fixture.ReceiveBytes(frames);
    var captured = RemoteProtocol.Decode(packet);
    Check(captured.Metadata.Width == 1280 && captured.Jpeg[0] == 255 && captured.Jpeg[1] == 216, "isolated capture worker supplies synthetic JPEG frame");
    var sustained = Stopwatch.StartNew();
    while (sustained.Elapsed < TimeSpan.FromSeconds(6)) {
        await Fixture.ReceiveBytes(control); // Processes automatic ping/pong as well as the lease reply.
        for (var n = 0; n < 7; n++) captured = RemoteProtocol.Decode(await Fixture.ReceiveBytes(frames));
    }
    Check(captured.Metadata.Sequence >= 30 && (await fixture.Management("status")).RemoteState == "Screen sharing active", "fragmented bind and sustained stream survive WebSocket keepalive");
    using (var duplicate = fixture.Socket()) {
        await duplicate.ConnectAsync(fixture.SocketUrl("control"), CancellationToken.None);
        await RemoteProtocol.Send(duplicate, new { type = "bind", remoteSessionId = id }, CancellationToken.None);
        var rejected = JsonSerializer.Deserialize<JsonElement>(await Fixture.ReceiveBytes(duplicate));
        Check(rejected.GetProperty("type").GetString() == "ended", "duplicate socket cannot replace the active control connection");
    }
    await fixture.Post("/api/remote/session/configure", new { remoteSessionId = id, controlEnabled = true });
    heartbeats.Cancel(); await pulse; // Single sender for the following input checks.
    await RemoteProtocol.Send(control, new { type = "button", sequence = 1, frameSequence = captured.Metadata.Sequence, revision = captured.Metadata.Revision, button = "left", down = true }, CancellationToken.None);
    JsonElement ack;
    do { ack = JsonSerializer.Deserialize<JsonElement>(await Fixture.ReceiveBytes(control)); } while (ack.GetProperty("type").GetString() == "heartbeat");
    Check(ack.GetProperty("inserted").GetBoolean(), "authenticated control socket acknowledges simulated mouse input");
    await fixture.Management("revoke"); await Task.Delay(300);
    await fixture.Pair();
    var demo = await fixture.Http.GetFromJsonAsync<JsonElement>("/api/remote/demo-state");
    Check(!demo.GetProperty("active").GetBoolean() && demo.GetProperty("held").GetInt32() == 0, "local revocation closes live session and releases held input");
    var raw = new HttpRequestMessage(HttpMethod.Post, "/api/audio/outputs/mute") { Content = JsonContent.Create(new { muted = true, revision, deviceIds = new[] { "demo-speakers" } }) };
    raw.Headers.Add("Origin", fixture.Http.BaseAddress!.GetLeftPart(UriPartial.Authority));
    Check((await fixture.Http.SendAsync(raw)).StatusCode == HttpStatusCode.Forbidden, "ordinary POST still requires custom header");
    response = await (await fixture.Post("/api/remote/session", start)).Content.ReadFromJsonAsync<JsonElement>(); id = response.GetProperty("remoteSessionId").GetString()!;
    using var noPulse = fixture.Socket(); await noPulse.ConnectAsync(fixture.SocketUrl("control"), CancellationToken.None);
    await RemoteProtocol.Send(noPulse, new { type = "bind", remoteSessionId = id }, CancellationToken.None);
    await Task.Delay(3400);
    demo = await fixture.Http.GetFromJsonAsync<JsonElement>("/api/remote/demo-state");
    Check(!demo.GetProperty("active").GetBoolean(), "heartbeat lease ends an abandoned remote session");
    response = await (await fixture.Post("/api/remote/session", start)).Content.ReadFromJsonAsync<JsonElement>(); id = response.GetProperty("remoteSessionId").GetString()!;
    using var oversized = fixture.Socket(); await oversized.ConnectAsync(fixture.SocketUrl("control"), CancellationToken.None);
    await RemoteProtocol.Send(oversized, new { type = "bind", remoteSessionId = id }, CancellationToken.None);
    await oversized.SendAsync(Encoding.UTF8.GetBytes(new string('x', 9000)), WebSocketMessageType.Text, true, CancellationToken.None);
    var ended = JsonSerializer.Deserialize<JsonElement>(await Fixture.ReceiveBytes(oversized));
    Check(ended.GetProperty("type").GetString() == "ended" && !(await fixture.Management("status")).RemoteState!.Contains("active"), "oversized control message terminates its session");
}
Check(PairingState.IsLan(System.Net.IPAddress.Parse("192.168.1.9")) && PairingState.IsLan(System.Net.IPAddress.Loopback) && !PairingState.IsLan(System.Net.IPAddress.Parse("100.64.1.2")) && !PairingState.IsLan(System.Net.IPAddress.Parse("8.8.8.8")), "Tailnet and public peers are not treated as LAN");
var events = new List<string>();
var remotePairing = new PairingState(clock, events.Add);
Check(remotePairing.PairRemote("100.64.1.2", "000000", "phone", out _) == 401, "Remote wrong code is rejected");
Check(remotePairing.PairRemote("100.64.1.2", remotePairing.Status().Code!, "phone", out var requestId) == 202 && requestId != null, "Remote correct code only opens an approval request");
Check(remotePairing.Poll(requestId!, "100.64.1.2", out var early) == 202 && early == null, "Remote client waits while unapproved");
Check(remotePairing.Poll(requestId!, "100.64.1.3", out _) == 404, "Approval request is bound to the requesting address");
string? remoteToken = null;
Check(remotePairing.Decide(requestId!, true) && remotePairing.Poll(requestId!, "100.64.1.2", out remoteToken) == 200 && remotePairing.Valid(remoteToken!), "Approval issues a session once");
Check(remotePairing.Poll(requestId!, "100.64.1.2", out _) == 404, "Approved request cannot be reused");
var view = remotePairing.Status().Sessions!.Single();
Check(view.Remote && view.Expires - view.Started == PairingState.RemoteLifetime, "Remote sessions use the shorter lifetime");
Check(remotePairing.RevokeSession(view.Id) && !remotePairing.Valid(remoteToken!), "Single session revoke works");
remotePairing.PairRemote("100.64.9.9", remotePairing.Status().Code!, "x", out var denyId);
Check(remotePairing.Decide(denyId!, false) && remotePairing.Poll(denyId!, "100.64.9.9", out _) == 403, "Denied request is refused");
for (var i = 0; i < 5; i++) remotePairing.PairRemote("100.64.7.7", "000000", "x", out _);
Check(remotePairing.PairRemote("100.64.7.7", remotePairing.Status().Code!, "x", out _) == 429, "Remote pairing locks out after five failures");
Check(events.Any(e => e.StartsWith("approved")) && events.Any(e => e.StartsWith("denied")) && !events.Any(e => e.Contains(remoteToken!)), "Audit records decisions and no tokens");
Check(PairingState.PeerAllowed(System.Net.IPAddress.Parse("192.168.1.20"), false) && PairingState.PeerAllowed(System.Net.IPAddress.IPv6Loopback, false) && PairingState.PeerAllowed(System.Net.IPAddress.Parse("::ffff:10.0.0.5"), false), "local-only policy accepts the PC and private-network devices");
Check(!PairingState.PeerAllowed(System.Net.IPAddress.Parse("8.8.8.8"), false) && !PairingState.PeerAllowed(System.Net.IPAddress.Parse("100.64.1.2"), false) && !PairingState.PeerAllowed(null, false), "local-only policy refuses internet, tailnet and unknown peers");
Check(PairingState.PeerAllowed(System.Net.IPAddress.Parse("100.64.1.2"), true), "remote access stays an explicit opt-in");
var adapt = new AdaptiveQuality("clear");
for (var i = 0; i < 4; i++) adapt.Observe(TimeSpan.FromMilliseconds(300));
Check(adapt.Current == "balanced" && adapt.Reduced, "slow frame delivery steps quality down");
for (var i = 0; i < 12; i++) adapt.Observe(TimeSpan.FromMilliseconds(300));
Check(adapt.Current == "low", "quality never drops below low");
for (var i = 0; i < 200; i++) adapt.Observe(TimeSpan.FromMilliseconds(5));
Check(adapt.Current == "clear" && !adapt.Reduced, "healthy link restores the chosen quality, never above it");
var capped = new AdaptiveQuality("balanced"); for (var i = 0; i < 300; i++) capped.Observe(TimeSpan.FromMilliseconds(5));
Check(capped.Current == "balanced", "adaptive quality does not exceed the requested level");
var tlsOff = new TlsSettings(_ => null);
Check(!tlsOff.Enabled && tlsOff.AllowPlainHttp && tlsOff.HostAllowed("192.168.1.5") && tlsOff.HostAllowed("localhost") && !tlsOff.HostAllowed("8.8.8.8") && !tlsOff.HostAllowed("phone.ts.net"), "TLS off keeps the private-address host rule");
var tlsHosts = new TlsSettings(k => k == "MONITOR_ALLOWED_HOSTS" ? "Phone.Example.ts.net, bad!host" : null);
Check(tlsHosts.HostAllowed("phone.example.ts.net") && !tlsHosts.HostAllowed("evil.example") && tlsHosts.AllowedHosts.Length == 1, "Allowed hostnames are explicit and validated");
Console.WriteLine($"PASS: {checks} checks. No real desktop capture, input injection or audio changes.");

sealed class CheckClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
sealed class Fixture : IAsyncDisposable {
    readonly Process process;
    readonly string pipe, secret;
    readonly CookieContainer cookies = new();
    public readonly HttpClient Http;
    Fixture(Process process, string pipe, string secret, int port) {
        this.process = process; this.pipe = pipe; this.secret = secret;
        Http = new(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
    }
    public static async Task<Fixture> Start(string serverPath) {
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var pipe = "MonitorRemote-Tests-" + Guid.NewGuid().ToString("N");
        var secret = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : serverPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(serverPath);
        info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(Directory.GetCurrentDirectory());
        info.Environment["MONITOR_PORT"] = port.ToString(); info.Environment["MONITOR_DEMO"] = "1"; info.Environment["MONITOR_MANAGEMENT_PIPE"] = pipe; info.Environment["MONITOR_MANAGEMENT_SECRET"] = secret;
        info.Environment.Remove("MONITOR_LAUNCH_GATE");
        var process = Process.Start(info)!;
        _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        var fixture = new Fixture(process, pipe, secret, port);
        try {
            for (var i = 0; i < 50; i++) { try { if ((await fixture.Management("status")).Running) return fixture; } catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException) { } await Task.Delay(100); }
            throw new Exception("Test server failed to start.");
        } catch { await fixture.DisposeAsync(); throw; }
    }
    public async Task<ManagementReply> Management(string command) {
        using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(2000); await connection.ConnectAsync(timeout.Token);
        await ManagementProtocol.Write(connection, new ManagementRequest(1, secret, command), timeout.Token);
        return await ManagementProtocol.Read<ManagementReply>(connection, timeout.Token);
    }
    public async Task Pair() { var status = await Management("status"); var result = await Post("/api/pair", new { code = status.Code }); result.EnsureSuccessStatusCode(); }
    public Task<HttpResponseMessage> Post(string path, object data) {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(data) };
        request.Headers.Add("Origin", Http.BaseAddress!.GetLeftPart(UriPartial.Authority)); request.Headers.Add("X-Monitor-Request", "1"); return Http.SendAsync(request);
    }
    public ClientWebSocket Socket(string? origin = null) {
        var socket = new ClientWebSocket(); socket.Options.Cookies = cookies; socket.Options.SetRequestHeader("Origin", origin ?? Http.BaseAddress!.GetLeftPart(UriPartial.Authority)); return socket;
    }
    public Uri SocketUrl(string kind) => new(Http.BaseAddress!.ToString().Replace("http://", "ws://") + "api/remote/" + kind);
    public static async Task<byte[]> ReceiveBytes(WebSocket socket) {
        using var timeout = new CancellationTokenSource(5000); using var stream = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var result = await socket.ReceiveAsync(buffer, timeout.Token); if (result.MessageType == WebSocketMessageType.Close) throw new Exception("Socket closed before expected data."); stream.Write(buffer, 0, result.Count); if (stream.Length > RemoteProtocol.MaxFrame + 4100) throw new Exception("Oversized test frame."); if (result.EndOfMessage) return stream.ToArray(); }
    }
    public async ValueTask DisposeAsync() {
        try { if (!process.HasExited) await Management("shutdown"); } catch (Exception e) when (e is IOException or OperationCanceledException) { }
        using var timeout = new CancellationTokenSource(4000);
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
        Http.Dispose(); process.Dispose();
    }
}
