using System.Collections.Concurrent;
using System.Runtime.InteropServices;

sealed class AudioControlService : IDisposable {
    readonly BlockingCollection<Action> queue = new(64);
    readonly Thread thread;
    IAudioDevices? devices;
    Exception? startupError;
    bool initialized;
    public AudioControlService() {
        thread = new(Loop) { IsBackground = true, Name = "Monitor Remote audio" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    void Loop() {
        try { foreach (var action in queue.GetConsumingEnumerable()) action(); }
        finally {
            try { devices?.Dispose(); }
            catch (COMException) { }
            finally { if (initialized && OperatingSystem.IsWindows()) CoreAudioInterop.CoUninitialize(); }
        }
    }
    IAudioDevices Devices() {
        if (devices != null) return devices;
        if (startupError != null) throw new InvalidOperationException("Windows audio service is unavailable. Restart the PC app.", startupError);
        try {
            if (DesktopDisplayCatalog.Demo) return devices = new AudioDemoProvider();
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Audio control requires Windows.");
            Marshal.ThrowExceptionForHR(CoreAudioInterop.CoInitializeEx(IntPtr.Zero, 0)); initialized = true;
            return devices = new AudioDeviceCatalog();
        } catch (Exception e) { startupError = e; throw; }
    }
    public Task<T> Run<T>(Func<IAudioDevices, T> action, CancellationToken cancellation) {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            if (!queue.TryAdd(() => {
                try { cancellation.ThrowIfCancellationRequested(); completion.TrySetResult(action(Devices())); }
                catch (Exception e) { completion.TrySetException(e); }
            })) completion.SetException(new FeatureException(503, "Audio controller busy. Try again."));
        } catch (InvalidOperationException) { completion.TrySetException(new FeatureException(503, "Audio controller is stopping.")); }
        return completion.Task;
    }
    public void Dispose() { queue.CompleteAdding(); thread.Join(1000); }
}
record AudioChange(double? Value = null, bool? Muted = null, string? Revision = null, bool RequireDefault = false, string[]? DeviceIds = null);
static class AudioEndpoints {
    public static void Map(WebApplication app, AudioControlService audio, RemoteSessionManager manager) {
        app.MapGet("/api/audio/devices", (Delegate)((HttpContext ctx) => Read(ctx, audio, manager)));
        app.MapPost("/api/audio/devices/{id}/volume", (HttpContext ctx, string id, AudioChange request) => Change(ctx, audio, manager, request, id, false));
        app.MapPost("/api/audio/devices/{id}/mute", (HttpContext ctx, string id, AudioChange request) => Change(ctx, audio, manager, request, id, true));
        app.MapPost("/api/audio/outputs/volume", (HttpContext ctx, AudioChange request) => Change(ctx, audio, manager, request, null, false));
        app.MapGet("/api/audio/sessions", (Delegate)((HttpContext ctx) => ReadSessions(ctx, audio, manager)));
        app.MapPost("/api/audio/sessions/{id}/volume", (HttpContext ctx, string id, AudioChange request) => ChangeSession(ctx, audio, manager, request, id, false));
        app.MapPost("/api/audio/sessions/{id}/mute", (HttpContext ctx, string id, AudioChange request) => ChangeSession(ctx, audio, manager, request, id, true));
        app.MapPost("/api/audio/outputs/mute", (HttpContext ctx, AudioChange request) => Change(ctx, audio, manager, request, null, true));
    }
    static void Authorize(HttpContext ctx, RemoteSessionManager manager) {
        ctx.RequestAborted.ThrowIfCancellationRequested();
        if (!manager.AudioAllowed(FeatureApi.Owner(ctx))) throw new FeatureException(403, "Enable Allow audio volume control in the PC app, then reconnect.");
    }
    static async Task<IResult> ReadSessions(HttpContext ctx, AudioControlService audio, RemoteSessionManager manager) {
        try {
            Authorize(ctx, manager);
            return Results.Ok(await audio.Run(devices => { Authorize(ctx, manager); return devices.Sessions(); }, ctx.RequestAborted));
        } catch (Exception e) when (e is FeatureException or InvalidOperationException or COMException or OperationCanceledException) { return FeatureApi.Error(e); }
    }
    static async Task<IResult> ChangeSession(HttpContext ctx, AudioControlService audio, RemoteSessionManager manager, AudioChange request, string id, bool mute) {
        try {
            Authorize(ctx, manager);
            if (mute ? !request.Muted.HasValue : !request.Value.HasValue || !double.IsFinite(request.Value.Value) || request.Value < 0 || request.Value > 100)
                throw new FeatureException(400, mute ? "Provide an explicit mute state." : "Volume must be 0–100.");
            return Results.Ok(await audio.Run(devices => {
                Authorize(ctx, manager);
                var snapshot = devices.Sessions();
                if (request.Revision != snapshot.Revision) throw new FeatureException(409, "Applications changed. Refresh before changing volume.");
                if (!snapshot.Sessions.Any(s => s.Id == id)) throw new FeatureException(404, "Application audio session ended.");
                try {
                    var actual = devices.SetSession(id, mute ? null : request.Value, mute ? request.Muted : null);
                    var confirmed = mute ? actual.Muted == request.Muted : actual.Volume.HasValue && Math.Abs(actual.Volume.Value - request.Value!.Value) <= 1;
                    return new { session = actual, confirmed, error = confirmed ? null : "Requested setting differs from Windows readback.", simulated = snapshot.Simulated };
                } catch (Exception e) when (e is InvalidOperationException or COMException) {
                    return new { session = (AudioSession?)null, confirmed = false, error = "The application rejected the change or its sound ended. Refresh applications.", simulated = snapshot.Simulated };
                }
            }, ctx.RequestAborted));
        } catch (Exception e) when (e is FeatureException or InvalidOperationException or COMException or OperationCanceledException) { return FeatureApi.Error(e); }
    }
    static async Task<IResult> Read(HttpContext ctx, AudioControlService audio, RemoteSessionManager manager) {
        try {
            Authorize(ctx, manager);
            return Results.Ok(await audio.Run(devices => { Authorize(ctx, manager); return devices.Snapshot(); }, ctx.RequestAborted));
        } catch (Exception e) when (e is FeatureException or InvalidOperationException or COMException or OperationCanceledException) { return FeatureApi.Error(e); }
    }
    static async Task<IResult> Change(HttpContext ctx, AudioControlService audio, RemoteSessionManager manager, AudioChange request, string? id, bool mute) {
        try {
            Authorize(ctx, manager);
            if (mute ? !request.Muted.HasValue : !request.Value.HasValue || !double.IsFinite(request.Value.Value) || request.Value < 0 || request.Value > 100)
                throw new FeatureException(400, mute ? "Provide an explicit mute state." : "Volume must be 0–100.");
            return Results.Ok(await audio.Run(devices => {
                Authorize(ctx, manager);
                var snapshot = devices.Snapshot();
                if (request.Revision != snapshot.Revision) throw new FeatureException(409, "Audio device list changed. Refresh before changing volume.");
                var ids = id == null ? request.DeviceIds : [id];
                if (ids == null || ids.Length == 0 || ids.Length > 1024 || ids.Distinct().Count() != ids.Length) throw new FeatureException(400, "Select audio devices.");
                if (id != null && !snapshot.Devices.Any(d => d.Id == id)) throw new FeatureException(404, "Audio device unavailable.");
                if (request.RequireDefault && (id == null || !snapshot.Devices.Any(d => d.Id == id && d.Default && d.Direction == "output"))) throw new FeatureException(409, "Default output changed. Refresh PC volume.");
                if (id == null && ids.Any(deviceId => snapshot.Devices.All(d => d.Id != deviceId || d.Direction != "output" || d.State != "active")))
                    throw new FeatureException(409, "Playback device selection changed. Refresh devices.");
                var results = new List<AudioResult>();
                foreach (var deviceId in ids) {
                    Authorize(ctx, manager);
                    try {
                        var actual = devices.Set(deviceId, mute ? null : request.Value, mute ? request.Muted : null);
                        var confirmed = actual.Supported && (mute ? actual.Muted == request.Muted : actual.Volume.HasValue && Math.Abs(actual.Volume.Value - request.Value!.Value) <= 1);
                        results.Add(new(deviceId, confirmed, actual, confirmed ? null : "Requested setting differs from Windows readback."));
                    } catch (Exception e) when (e is InvalidOperationException or COMException) { results.Add(new(deviceId, false, null, "Device unavailable or rejected the change. Refresh and check its setting.")); }
                }
                return new { results, confirmed = results.All(r => r.Confirmed), simulated = snapshot.Simulated };
            }, ctx.RequestAborted));
        } catch (Exception e) when (e is FeatureException or InvalidOperationException or COMException or OperationCanceledException) { return FeatureApi.Error(e); }
    }
}
