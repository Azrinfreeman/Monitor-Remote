using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using Concentus;
using Concentus.Enums;

// 48 kHz stereo is Opus's native rate; 20 ms frames keep latency low.
static class SoundFormat {
    public const int Rate = 48000, Channels = 2, FrameSamples = 960, MaxPacket = 1275, HeaderBytes = 9;
}
interface ISoundSource : IDisposable {
    void Start();
    // Fills one 20 ms interleaved stereo frame. Missing audio is silence, so a quiet PC still produces a steady clock.
    void ReadFrame(short[] frame);
}
sealed class DemoSoundSource : ISoundSource {
    long position;
    public void Start() { }
    public void ReadFrame(short[] frame) {
        for (var i = 0; i < SoundFormat.FrameSamples; i++, position++) {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * position / SoundFormat.Rate) * 8000);
            frame[i * 2] = frame[i * 2 + 1] = sample;
        }
    }
    public void Dispose() { }
}
// Captures whatever Windows plays on the default output (WASAPI loopback). Nothing is stored; samples live in a 200 ms ring.
sealed class LoopbackSoundSource : ISoundSource {
    readonly NAudio.Wave.WasapiLoopbackCapture capture = new();
    readonly object sync = new();
    readonly short[] ring = new short[SoundFormat.Rate / 5 * 2];
    int count;
    double position; float lastLeft, lastRight; bool haveLast;
    public LoopbackSoundSource() {
        var format = capture.WaveFormat;
        if (format.Encoding != NAudio.Wave.WaveFormatEncoding.IeeeFloat && !(format.Encoding == NAudio.Wave.WaveFormatEncoding.Extensible && format.BitsPerSample == 32) || format.Channels < 1)
            throw new InvalidOperationException("The default playback device uses a sound format that cannot be streamed.");
        capture.DataAvailable += (_, e) => Append(e.Buffer, e.BytesRecorded);
    }
    public void Start() => capture.StartRecording();
    void Append(byte[] buffer, int bytes) {
        var format = capture.WaveFormat;
        var channels = format.Channels;
        var step = (double)format.SampleRate / SoundFormat.Rate;
        var frames = bytes / (4 * channels);
        lock (sync) {
            for (var i = 0; i < frames; i++) {
                var left = BitConverter.ToSingle(buffer, i * channels * 4);
                var right = channels > 1 ? BitConverter.ToSingle(buffer, i * channels * 4 + 4) : left;
                if (!haveLast) { lastLeft = left; lastRight = right; haveLast = true; }
                // Linear resampling to 48 kHz: emit output samples that fall between the previous and current input frame.
                while (position < 1) {
                    Push(lastLeft + (left - lastLeft) * (float)position, lastRight + (right - lastRight) * (float)position);
                    position += step;
                }
                position -= 1; lastLeft = left; lastRight = right;
            }
        }
    }
    void Push(float left, float right) {
        if (count + 2 > ring.Length) { Array.Copy(ring, 2, ring, 0, count - 2); count -= 2; } // drop oldest: latency never grows
        ring[count++] = ToShort(left); ring[count++] = ToShort(right);
    }
    static short ToShort(float value) => (short)(Math.Clamp(value, -1f, 1f) * short.MaxValue);
    public void ReadFrame(short[] frame) {
        var wanted = SoundFormat.FrameSamples * 2;
        lock (sync) {
            var take = Math.Min(wanted, count);
            Array.Copy(ring, 0, frame, 0, take);
            Array.Copy(ring, take, ring, 0, count - take); count -= take;
            Array.Clear(frame, take, wanted - take);
        }
    }
    public void Dispose() { try { capture.StopRecording(); } catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { } capture.Dispose(); }
}
// One listener at a time. Ends on permission off, session revoke, Stop remote session, or socket close.
sealed class SoundStreamService : IDisposable {
    readonly object sync = new();
    CancellationTokenSource? current;
    readonly RemoteSessionManager manager;
    readonly PairingState pairing;
    readonly AudioControlService audio;
    public SoundStreamService(RemoteSessionManager manager, PairingState pairing, AudioControlService audio) {
        this.manager = manager; this.pairing = pairing; this.audio = audio;
        manager.SoundStopRequested += Stop;
    }
    public void Stop() { lock (sync) current?.Cancel(); }
    public static void Map(WebApplication app, SoundStreamService service) => app.Map("/api/audio/stream", service.Handle);
    async Task Handle(HttpContext ctx) {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var owner = FeatureApi.Owner(ctx);
        if (!manager.SoundAllowed(owner)) { ctx.Response.StatusCode = 403; return; }
        var mutePc = ctx.Request.Query["mutePc"] == "1";
        if (mutePc && !manager.AudioAllowed(owner)) { ctx.Response.StatusCode = 403; return; }
        CancellationTokenSource lifetime;
        lock (sync) {
            if (current != null) { ctx.Response.StatusCode = 409; return; }
            lifetime = current = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        }
        manager.SoundListening = true;
        ISoundSource? source = null;
        string? mutedDevice = null;
        try {
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            try {
                source = DesktopDisplayCatalog.Demo ? new DemoSoundSource() : OperatingSystem.IsWindows() ? new LoopbackSoundSource() : throw new InvalidOperationException("Sound streaming requires Windows.");
                source.Start();
                if (mutePc) mutedDevice = await MutePc();
                await Pump(socket, source, owner, lifetime.Token);
                if (socket.State == WebSocketState.CloseReceived) {
                    using var timeout = new CancellationTokenSource(500);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                }
            } catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or OperationCanceledException or WebSocketException or IOException) {
                if (socket.State == WebSocketState.Open) {
                    try {
                        using var timeout = new CancellationTokenSource(500);
                        var text = error is OperationCanceledException or WebSocketException ? "Sound stream ended." : error.Message;
                        await socket.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"ended\",\"error\":{System.Text.Json.JsonSerializer.Serialize(text)}}}"), WebSocketMessageType.Text, true, timeout.Token);
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ended", timeout.Token);
                    } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
                }
            }
        } finally {
            source?.Dispose();
            if (mutedDevice != null) await Unmute(mutedDevice);
            lock (sync) { current = null; }
            lifetime.Dispose();
            manager.SoundListening = false;
        }
    }
    // Optional: silence the PC speakers while listening. The previous state is restored when the stream ends, and a marker file
    // lets the next server start undo the mute if the process died mid-stream.
    static string MarkerPath => Environment.GetEnvironmentVariable("MONITOR_MUTE_MARKER") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorRemote", "unmute-pending.txt");
    async Task<string?> MutePc() {
        try {
            return await audio.Run(devices => {
                var device = devices.Snapshot().Devices.FirstOrDefault(d => d.Default && d.Direction == "output" && d.State == "active" && d.Supported);
                if (device == null || device.Muted == true) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath, device.Id);
                devices.Set(device.Id, null, true);
                return device.Id;
            }, CancellationToken.None);
        } catch (Exception e) when (e is FeatureException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException) { return null; }
    }
    async Task Unmute(string id) {
        try {
            await audio.Run(devices => { devices.Snapshot(); devices.Set(id, null, false); return 0; }, CancellationToken.None);
            File.Delete(MarkerPath);
        } catch (Exception e) when (e is FeatureException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException) { }
    }
    public async Task RecoverMute() {
        try { if (File.Exists(MarkerPath) && File.ReadAllText(MarkerPath).Trim() is { Length: > 0 } id) await Unmute(id); }
        catch (IOException) { }
    }
    async Task Pump(WebSocket socket, ISoundSource source, string owner, CancellationToken token) {
        var encoder = OpusCodecFactory.CreateEncoder(SoundFormat.Rate, SoundFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 96000;
        var pcm = new short[SoundFormat.FrameSamples * SoundFormat.Channels];
        var packet = new byte[SoundFormat.HeaderBytes + SoundFormat.MaxPacket];
        // The client closing its side must end the stream promptly; nothing else is expected from it.
        var closed = Task.Run(async () => {
            var buffer = new byte[256];
            try { while (socket.State == WebSocketState.Open && (await socket.ReceiveAsync(buffer, token)).MessageType != WebSocketMessageType.Close) { } } catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
        }, CancellationToken.None);
        using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        uint sequence = 0, timestamp = 0;
        while (await clock.WaitForNextTickAsync(token)) {
            if (closed.IsCompleted) return;
            if (!manager.SoundAllowed(owner) || !pairing.Valid(owner)) throw new InvalidOperationException("Sound streaming was turned off or this phone was disconnected.");
            source.ReadFrame(pcm);
            var length = encoder.Encode(pcm, SoundFormat.FrameSamples, packet.AsSpan(SoundFormat.HeaderBytes), SoundFormat.MaxPacket);
            packet[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), sequence++);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), timestamp); timestamp += SoundFormat.FrameSamples;
            using var send = CancellationTokenSource.CreateLinkedTokenSource(token); send.CancelAfter(1000);
            await socket.SendAsync(packet.AsMemory(0, SoundFormat.HeaderBytes + length), WebSocketMessageType.Binary, true, send.Token);
        }
    }
    public void Dispose() { manager.SoundStopRequested -= Stop; Stop(); }
}
