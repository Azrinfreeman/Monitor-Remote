using System.Diagnostics;
using System.Net.WebSockets;

static class FeatureApi {
    public static string Owner(HttpContext context) => context.Request.Cookies["session"] ?? "";
    public static IResult Error(Exception error) => Results.Json(new { error = error is FeatureException or InvalidOperationException ? error.Message : "Device operation failed. Refresh and retry." },
        statusCode: error is FeatureException feature ? feature.Status : 502);
    public static IResult Run(Func<IResult> action) { try { return action(); } catch (Exception e) when (e is FeatureException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return Error(e); } }
}
static class RemoteEndpoints {
    public static void Map(WebApplication app, RemoteSessionManager manager) {
        app.MapGet("/api/remote/displays", () => FeatureApi.Run(() => Results.Ok(new { capabilities = manager.Capabilities(),
            snapshot = manager.ViewingAllowed ? DesktopDisplayCatalog.Snapshot() : new DisplaySnapshot("", []) })));
        app.MapPost("/api/remote/session", (HttpContext ctx, StartRemote request) => FeatureApi.Run(() => {
            var current = manager.Start(FeatureApi.Owner(ctx), request);
            return Results.Ok(new { remoteSessionId = current.Id, display = current.Display, revision = current.Snapshot.Revision });
        }));
        app.MapPost("/api/remote/session/configure", (HttpContext ctx, ConfigureRemote request) => FeatureApi.Run(() => {
            manager.Configure(FeatureApi.Owner(ctx), request); return Results.Ok(new { controlEnabled = request.ControlEnabled });
        }));
        app.MapPost("/api/remote/session/stop", (HttpContext ctx, StopRemote request) => FeatureApi.Run(() => { manager.Stop(FeatureApi.Owner(ctx), request.RemoteSessionId); return Results.Ok(); }));
        app.Map("/api/remote/frames", ctx => Socket(ctx, manager, true));
        app.Map("/api/remote/control", ctx => Socket(ctx, manager, false));
        if (DesktopDisplayCatalog.Demo) app.MapGet("/api/remote/demo-state", () => manager.DemoStatus());
    }
    static async Task Socket(HttpContext ctx, RemoteSessionManager manager, bool frames) {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        RemoteSession? current = null;
        try {
            using var binding = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            binding.CancelAfter(3000);
            var request = await RemoteProtocol.Receive(socket, binding.Token);
            if (request?.Type != "bind") throw new FeatureException(400, "Bind this socket to its remote session first.");
            current = manager.Bind(FeatureApi.Owner(ctx), request.RemoteSessionId, frames);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, current.Token);
            if (frames) await Frames(socket, manager, current, lifetime.Token);
            else {
                while (!lifetime.IsCancellationRequested) {
                    var message = await RemoteProtocol.Receive(socket, lifetime.Token);
                    if (message == null) break;
                    object reply;
                    try { reply = manager.Handle(current, message); }
                    catch (InvalidOperationException error) { reply = new { type = "ack", sequence = message.Sequence, inserted = false, error = error.Message }; }
                    catch (FeatureException error) when (error.Status is 409 or 403 && manager.Valid(current)) {
                        // A rejected stale frame or disabled control is recoverable without replay.
                        reply = new { type = "ack", sequence = message.Sequence, inserted = false, error = error.Message };
                    }
                    using var send = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); send.CancelAfter(2000);
                    await RemoteProtocol.Send(socket, reply, send.Token);
                }
            }
        } catch (Exception error) when (error is FeatureException or IOException or InvalidOperationException or OperationCanceledException or WebSocketException or System.Text.Json.JsonException) {
            if (socket.State == WebSocketState.Open) {
                try {
                    using var timeout = new CancellationTokenSource(500);
                    await RemoteProtocol.Send(socket, new { type = "ended", error = error is FeatureException or InvalidOperationException ? error.Message : "Remote connection ended. Start viewing again." }, timeout.Token);
                } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
            }
        } finally {
            if (current != null) manager.End(current);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
                try { using var timeout = new CancellationTokenSource(500); await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", timeout.Token); }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException) { socket.Abort(); }
            }
        }
    }
    static async Task Frames(WebSocket socket, RemoteSessionManager manager, RemoteSession current, CancellationToken token) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Receiving also processes WebSocket ping/pong and close frames while JPEGs are sent.
        var receiving = ReceiveFrameChannel(socket, lifetime.Token);
        var producing = ProduceFrames(socket, manager, current, lifetime.Token);
        await Task.WhenAny(receiving, producing);
        lifetime.Cancel();
        await Task.WhenAll(receiving, producing);
    }
    static async Task ReceiveFrameChannel(WebSocket socket, CancellationToken token) {
        if (await RemoteProtocol.Receive(socket, token) != null)
            throw new FeatureException(400, "Use the control connection for input.");
    }
    static async Task ProduceFrames(WebSocket socket, RemoteSessionManager manager, RemoteSession current, CancellationToken token) {
        using var capture = new DesktopCaptureService();
        var adaptive = new AdaptiveQuality(current.Quality);
        while (manager.Valid(current) && !token.IsCancellationRequested) {
            var began = Stopwatch.GetTimestamp();
            var frame = await capture.Next(current.Display, current.Snapshot.Revision, adaptive.Current, token);
            if (!manager.Valid(current)) break;
            if (frame.Metadata.Revision != current.Snapshot.Revision) throw new InvalidOperationException("Display layout changed. Refresh displays.");
            var sequence = manager.Frame(current, began);
            var packet = RemoteProtocol.Encode(frame with { Metadata = frame.Metadata with { Sequence = sequence } });
            using var send = CancellationTokenSource.CreateLinkedTokenSource(token); send.CancelAfter(2000);
            var sending = Stopwatch.GetTimestamp();
            await socket.SendAsync(packet, WebSocketMessageType.Binary, true, send.Token);
            adaptive.Observe(Stopwatch.GetElapsedTime(sending));
            var delay = TimeSpan.FromMilliseconds(adaptive.Current == "low" ? 250 : adaptive.Current == "clear" ? 167 : 125) - Stopwatch.GetElapsedTime(began);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
        }
    }
}
