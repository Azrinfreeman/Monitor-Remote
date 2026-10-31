using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

if (args.FirstOrDefault() == "--worker") { Hardware.Run(args.Skip(1).ToArray()); return; }
if (args.FirstOrDefault() == "--capture-worker") { await DesktopCaptureWorker.Run(); return; }
DesktopDisplayCatalog.InitializeDpi();
var launchGate = Environment.GetEnvironmentVariable("MONITOR_LAUNCH_GATE");
if (launchGate != null) {
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!File.Exists(launchGate) && DateTime.UtcNow < deadline) await Task.Delay(25);
    if (!File.Exists(launchGate)) return;
}
var permissionArguments = new[] { "--allow-viewing", "--allow-input", "--allow-audio", "--allow-sound" };
var builder = WebApplication.CreateBuilder(args.Where(a => !permissionArguments.Contains(a)).ToArray());
var port = int.TryParse(Environment.GetEnvironmentVariable("MONITOR_PORT"), out var p) ? p : 8765;
var tls = new TlsSettings(Environment.GetEnvironmentVariable);
var allowRemote = Environment.GetEnvironmentVariable("MONITOR_ALLOW_REMOTE") == "1";
builder.WebHost.ConfigureKestrel(options => {
    options.Limits.MaxRequestBodySize = 65536;
    if (!tls.Enabled || tls.AllowPlainHttp) options.Listen(IPAddress.Any, port);
    if (tls.Enabled) options.Listen(IPAddress.Any, tls.Port, listen => listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => tls.Certificate()));
});
var app = builder.Build();
var pairing = new PairingState(audit: AuditLog.Write);
using var remote = new RemoteSessionManager(pairing, args.Contains("--allow-viewing"), args.Contains("--allow-input"), args.Contains("--allow-audio"), args.Contains("--allow-sound"));
using var audio = new AudioControlService();
using var sound = new SoundStreamService(remote, pairing, audio);
_ = sound.RecoverMute();
app.Lifetime.ApplicationStopping.Register(() => remote.Manage("remote:stop"));
var gate = new SemaphoreSlim(1);
var pipeName = Environment.GetEnvironmentVariable("MONITOR_MANAGEMENT_PIPE");
var pipeSecret = Environment.GetEnvironmentVariable("MONITOR_MANAGEMENT_SECRET");
if (pipeName == null) Console.WriteLine($"Pairing code: {pairing.Status().Code} (valid for 10 minutes). Restart to revoke sessions / generate a new code.");
foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()).Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) { if (!tls.Enabled || tls.AllowPlainHttp) Console.WriteLine($"Phone URL: http://{ip}:{port}"); if (tls.Enabled) Console.WriteLine($"Phone URL: https://{ip}:{tls.Port}"); }
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(1), KeepAliveTimeout = TimeSpan.FromSeconds(3) });
app.Use(async (ctx, next) => {
    if (!PairingState.PeerAllowed(ctx.Connection.RemoteIpAddress, allowRemote)) { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers["Cache-Control"] = "no-store";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    var host = ctx.Request.Host.Host;
    if (!tls.HostAllowed(host)) { ctx.Response.StatusCode = 403; return; }
    var secure = ctx.Request.IsHttps;
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    ctx.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' {(secure ? "wss" : "ws")}://{ctx.Request.Host}; frame-ancestors 'none'";
    var upgrade = ctx.WebSockets.IsWebSocketRequest && (ctx.Request.Path == "/api/remote/frames" || ctx.Request.Path == "/api/remote/control" || ctx.Request.Path == "/api/audio/stream");
    if (ctx.WebSockets.IsWebSocketRequest && (!upgrade || ctx.Request.Headers.Origin != origin)) { ctx.Response.StatusCode = 403; return; }
    if (!upgrade && ctx.Request.Method != "GET" && (ctx.Request.Headers.Origin != origin || ctx.Request.Headers["X-Monitor-Request"] != "1")) { ctx.Response.StatusCode = 403; return; }
    if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/pair") && (!ctx.Request.Cookies.TryGetValue("session", out var token) || !pairing.Valid(token))) { ctx.Response.StatusCode = 401; return; }
    await next();
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapPost("/api/pair", (HttpContext ctx, Pair input) => {
    var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var label = ctx.Request.Headers.UserAgent.ToString();
    if (!PairingState.IsLan(ctx.Connection.RemoteIpAddress)) {
        var pendingResult = pairing.PairRemote(key, input.Code, label, out var requestId);
        return pendingResult == 202 ? Results.Json(new { pending = true, id = requestId }, statusCode: 202) : Results.StatusCode(pendingResult);
    }
    var result = pairing.Pair(key, input.Code, out var token, label);
    if (result != 200) return Results.StatusCode(result);
    return PairedResponse(ctx, token!, PairingState.LanLifetime);
});
app.MapPost("/api/pair/poll", (HttpContext ctx, Poll input) => {
    var result = pairing.Poll(input.Id ?? "", ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", out var token);
    return result == 200 ? PairedResponse(ctx, token!, PairingState.RemoteLifetime) : result == 202 ? Results.Json(new { pending = true }, statusCode: 202) : Results.StatusCode(result);
});
IResult PairedResponse(HttpContext ctx, string token, TimeSpan lifetime) {
    ctx.Response.Cookies.Append("session", token, new CookieOptions { HttpOnly = true, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Strict, MaxAge = lifetime, IsEssential = true });
    return Results.Ok(new { paired = true });
}
app.MapPost("/api/logout", (HttpContext ctx) => { if (ctx.Request.Cookies.TryGetValue("session", out var token)) pairing.Logout(token); ctx.Response.Cookies.Delete("session"); return Results.Ok(); });
app.MapGet("/api/status", () => Results.Ok(new { online = true, simulated = Environment.GetEnvironmentVariable("MONITOR_DEMO") == "1", capabilities = remote.Capabilities() }));
RemoteEndpoints.Map(app, remote);
AudioEndpoints.Map(app, audio, remote);
SoundStreamService.Map(app, sound);
app.MapGet("/api/monitors", () => Command("list"));
app.MapPost("/api/monitors/{id}/brightness", (string id, Brightness input) => int.TryParse(id, out var index) && index >= 0 && input.Value is >= 0 and <= 100 ? Command("brightness", id, input.Value.ToString()) : Task.FromResult<IResult>(Results.BadRequest(new { error = "Brightness must be 0–100 and monitor ID must be valid." })));
app.MapPost("/api/displays/power", async (Power input) => {
    if (input.State is not ("off" or "wake")) return Results.BadRequest(new { error = "Use off or wake." });
    try {
        if (input.State == "off") remote.BeginPowerOff();
        try { return await Command("power", input.State); }
        finally { if (input.State == "off") remote.EndPowerOff(); }
    } catch (FeatureException error) { return FeatureApi.Error(error); }
});
async Task<IResult> Command(params string[] command) {
    if (!await gate.WaitAsync(TimeSpan.FromSeconds(2))) return Results.Json(new { error = "Controller busy. Try again." }, statusCode: 503);
    try {
        var executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Hardware).Assembly.Location);
        info.ArgumentList.Add("--worker"); foreach (var item in command) info.ArgumentList.Add(item);
        using var worker = Process.Start(info)!;
        var output = worker.StandardOutput.ReadToEndAsync(); var errors = worker.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await worker.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { worker.Kill(true); await worker.WaitForExitAsync(); return Results.Json(new { error = "Monitor operation timed out. Check its cable and DDC/CI setting." }, statusCode: 504); }
        if (worker.ExitCode != 0) return Results.Json(new { error = (await errors).Trim() }, statusCode: 502);
        return Results.Content(await output, "application/json");
    } finally { gate.Release(); }
}
await app.StartAsync();
var management = pipeName != null && !string.IsNullOrEmpty(pipeSecret) ? LocalManagement.Run(app, pairing, remote, pipeName, pipeSecret) : Task.CompletedTask;
await app.WaitForShutdownAsync();
await management;
record Pair(string Code);
record Poll(string? Id);
record Brightness(int Value);
record Power(string State);
