using System.Text;
using System.Text.Json;

record PendingPairing(string Id, string Address, string Label, DateTimeOffset Requested);
record SessionView(string Id, string Address, string Label, bool Remote, DateTimeOffset Started, DateTimeOffset Expires);
record ManagementRequest(int Version, string Secret, string Command);
record PowerTaskView(string Id, string RequestId, string Action, string Mode, DateTimeOffset DueAtUtc, long Revision,
    string State, DateTimeOffset? DispatchedAtUtc = null, string? Error = null, bool Simulated = false, DateTimeOffset? DueAtLocal = null);
record ManagementReply(int Version, bool Running, string? Code = null, DateTimeOffset? Expires = null, string? Error = null,
    bool AllowViewing = false, bool AllowInput = false, bool AllowAudio = false, string? RemoteState = null, string? DisplayName = null,
    PendingPairing[]? Pending = null, SessionView[]? Sessions = null, bool AllowSound = false, bool SoundActive = false,
    bool AllowPcPower = false, PowerTaskView? PowerTask = null, string? PowerError = null);
static class ManagementProtocol {
    public static async Task<T> Read<T>(Stream stream, CancellationToken token) {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < 4096) {
            if (await stream.ReadAsync(buffer, token) == 0) throw new IOException("Management connection closed.");
            if (buffer[0] == 10) return JsonSerializer.Deserialize<T>(bytes.ToArray()) ?? throw new IOException("Invalid management message.");
            bytes.Add(buffer[0]);
        }
        throw new IOException("Management message too large.");
    }
    public static async Task Write<T>(Stream stream, T message, CancellationToken token) {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n"), token);
        await stream.FlushAsync(token);
    }
}
