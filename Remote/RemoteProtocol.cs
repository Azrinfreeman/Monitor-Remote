using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;

record FrameMetadata(int Version, long Sequence, string DisplayId, string Revision, int X, int Y, int NativeWidth, int NativeHeight,
    int Width, int Height, int CursorX, int CursorY, bool CursorVisible);
record CapturedFrame(FrameMetadata Metadata, byte[] Jpeg);
record CaptureRequest(string Secret, string DisplayId, string Revision, string Quality);
record StartRemote(string DisplayId, string Revision, string Quality = "balanced");
record ConfigureRemote(string RemoteSessionId, bool ControlEnabled);
record StopRemote(string RemoteSessionId);
record ControlMessage(string Type, long Sequence = 0, string? RemoteSessionId = null, long FrameSequence = 0, string? Revision = null,
    double X = 0, double Y = 0, double Dx = 0, double Dy = 0, string? Button = null, bool Down = false, bool Double = false,
    string? Key = null, string[]? Modifiers = null, string? Text = null);

static class RemoteProtocol {
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxFrame = 2 * 1024 * 1024;
    public static byte[] Encode(CapturedFrame frame) {
        var header = JsonSerializer.SerializeToUtf8Bytes(frame.Metadata, Json);
        if (header.Length > 4096 || frame.Jpeg.Length > MaxFrame) throw new IOException("Capture frame too large.");
        var packet = new byte[4 + header.Length + frame.Jpeg.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, header.Length);
        header.CopyTo(packet.AsSpan(4)); frame.Jpeg.CopyTo(packet.AsSpan(4 + header.Length));
        return packet;
    }
    public static CapturedFrame Decode(byte[] packet) {
        if (packet.Length < 5) throw new IOException("Invalid frame.");
        var size = BinaryPrimitives.ReadInt32LittleEndian(packet);
        if (size < 1 || size > 4096 || size + 4 >= packet.Length) throw new IOException("Invalid frame header.");
        var metadata = JsonSerializer.Deserialize<FrameMetadata>(packet.AsSpan(4, size), Json) ?? throw new IOException("Missing frame metadata.");
        return new(metadata, packet.AsSpan(4 + size).ToArray());
    }
    public static async Task WritePacket(Stream stream, byte[] packet, CancellationToken token) {
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, packet.Length);
        await stream.WriteAsync(length, token); await stream.WriteAsync(packet, token); await stream.FlushAsync(token);
    }
    public static async Task<byte[]> ReadPacket(Stream stream, CancellationToken token) {
        byte[] length = new byte[4]; await stream.ReadExactlyAsync(length, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 5 || size > MaxFrame + 4100) throw new IOException("Invalid capture packet length.");
        var packet = new byte[size]; await stream.ReadExactlyAsync(packet, token); return packet;
    }
    public static async Task<ControlMessage?> Receive(WebSocket socket, CancellationToken token) {
        var buffer = new byte[8193]; var count = 0;
        while (true) {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || (count += result.Count) > 8192) throw new IOException("Control message must be JSON and at most 8 KiB.");
            if (result.EndOfMessage) return JsonSerializer.Deserialize<ControlMessage>(buffer.AsSpan(0, count), Json) ?? throw new IOException("Invalid control message.");
        }
    }
    public static Task Send(WebSocket socket, object message, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Json), WebSocketMessageType.Text, true, token);
}
