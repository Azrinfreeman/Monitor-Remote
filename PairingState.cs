using System.Security.Cryptography;

sealed class PairingState {
    readonly TimeProvider clock;
    readonly object sync = new();
    readonly Dictionary<string, DateTimeOffset> sessions = new();
    readonly Dictionary<string, (DateTimeOffset Start, int Count)> attempts = new();
    string code = "";
    DateTimeOffset expires;
    public event Action<string?>? Revoked;
    public PairingState(TimeProvider? clock = null) { this.clock = clock ?? TimeProvider.System; Renew(false); }
    public ManagementReply Status() { lock (sync) return new(1, true, code, expires); }
    public ManagementReply Renew(bool revoke) {
        ManagementReply reply;
        lock (sync) {
            var old = code;
            do { code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(); } while (code == old);
            expires = clock.GetUtcNow().AddMinutes(10);
            attempts.Clear();
            if (revoke) sessions.Clear();
            reply = Status();
        }
        if (revoke) Revoked?.Invoke(null);
        return reply;
    }
    public int Pair(string address, string input, out string? token) {
        lock (sync) {
            token = null;
            var now = clock.GetUtcNow();
            attempts.TryGetValue(address, out var old);
            var entry = now - old.Start > TimeSpan.FromMinutes(10) ? (now, 1) : (old.Start, old.Count + 1);
            attempts[address] = entry;
            if (entry.Item2 > 10) return 429;
            if (now > expires || input != code) return 401;
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions[token] = now.AddHours(12);
            return 200;
        }
    }
    public bool Valid(string token) { lock (sync) return sessions.TryGetValue(token, out var expiry) && expiry > clock.GetUtcNow(); }
    public void Logout(string token) { lock (sync) sessions.Remove(token); Revoked?.Invoke(token); }
}
