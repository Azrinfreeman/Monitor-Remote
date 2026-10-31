using System.Net;
using System.Security.Cryptography;

sealed class PairingState {
    sealed record Pending(string Address, string Label, DateTimeOffset Requested, string? Token = null, bool Denied = false);
    sealed record Session(DateTimeOffset Expiry, string Id, string Address, string Label, bool Remote, DateTimeOffset Started);
    public static readonly TimeSpan LanLifetime = TimeSpan.FromHours(12), RemoteLifetime = TimeSpan.FromHours(2), ApprovalWindow = TimeSpan.FromMinutes(2);
    readonly TimeProvider clock;
    readonly Action<string>? audit;
    readonly object sync = new();
    readonly Dictionary<string, Session> sessions = new();
    readonly Dictionary<string, Pending> pending = new();
    readonly Dictionary<string, (DateTimeOffset Start, int Count)> attempts = new();
    string code = "";
    DateTimeOffset expires;
    public event Action<string?>? Revoked;
    public PairingState(TimeProvider? clock = null, Action<string>? audit = null) { this.clock = clock ?? TimeProvider.System; this.audit = audit; Renew(false); }
    // Loopback and private IPv4 count as the home network; everything else (tailnet, mobile data) needs approval on the PC.
    public static bool IsLan(IPAddress? address) {
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }
    public ManagementReply Status() { lock (sync) { Expire(); return new(1, true, code, expires, Pending: pending.Where(p => p.Value.Token == null && !p.Value.Denied).Select(p => new PendingPairing(p.Key, p.Value.Address, p.Value.Label, p.Value.Requested)).ToArray(),
        Sessions: sessions.Values.Select(s => new SessionView(s.Id, s.Address, s.Label, s.Remote, s.Started, s.Expiry)).ToArray()); } }
    public ManagementReply Renew(bool revoke) {
        ManagementReply reply;
        lock (sync) {
            var old = code;
            do { code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(); } while (code == old);
            expires = clock.GetUtcNow().AddMinutes(10);
            attempts.Clear();
            if (revoke) { sessions.Clear(); pending.Clear(); }
            reply = Status();
        }
        if (revoke) { audit?.Invoke("revoke-all"); Revoked?.Invoke(null); }
        return reply;
    }
    bool Throttled(string address, bool remote, DateTimeOffset now) {
        attempts.TryGetValue(address, out var old);
        var entry = now - old.Start > TimeSpan.FromMinutes(10) ? (now, 1) : (old.Start, old.Count + 1);
        attempts[address] = entry;
        return entry.Item2 > (remote ? 5 : 10);
    }
    string Issue(string address, string label, bool remote, DateTimeOffset now) {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[token] = new(now.Add(remote ? RemoteLifetime : LanLifetime), Convert.ToHexString(RandomNumberGenerator.GetBytes(4)), address, label, remote, now);
        return token;
    }
    // LAN clients: immediate token. Returns 200, 401 or 429.
    public int Pair(string address, string input, out string? token, string label = "") {
        lock (sync) {
            token = null;
            var now = clock.GetUtcNow();
            if (Throttled(address, false, now)) { audit?.Invoke($"pair-throttled {address}"); return 429; }
            if (now > expires || input != code) { audit?.Invoke($"pair-failed {address}"); return 401; }
            token = Issue(address, label, false, now);
            audit?.Invoke($"pair {address} lan");
            return 200;
        }
    }
    // Remote clients: a correct code only opens an approval request. Returns 202, 401 or 429.
    public int PairRemote(string address, string input, string label, out string? requestId) {
        lock (sync) {
            requestId = null;
            var now = clock.GetUtcNow();
            Expire();
            if (Throttled(address, true, now)) { audit?.Invoke($"pair-throttled {address} remote"); return 429; }
            if (now > expires || input != code) { audit?.Invoke($"pair-failed {address} remote"); return 401; }
            if (pending.Count(p => p.Value.Token == null && !p.Value.Denied) >= 3) return 429;
            requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            pending[requestId] = new(address, label.Length > 60 ? label[..60] : label, now);
            audit?.Invoke($"pair-requested {address} remote");
            return 202;
        }
    }
    public int Poll(string requestId, string address, out string? token) {
        lock (sync) {
            token = null;
            Expire();
            if (!pending.TryGetValue(requestId, out var entry) || entry.Address != address) return 404;
            if (entry.Denied) { pending.Remove(requestId); return 403; }
            if (entry.Token == null) return 202;
            token = entry.Token; pending.Remove(requestId);
            return 200;
        }
    }
    public bool Decide(string requestId, bool approve) {
        lock (sync) {
            Expire();
            if (!pending.TryGetValue(requestId, out var entry) || entry.Token != null || entry.Denied) return false;
            var now = clock.GetUtcNow();
            pending[requestId] = approve ? entry with { Token = Issue(entry.Address, entry.Label, true, now) } : entry with { Denied = true };
            audit?.Invoke($"{(approve ? "approved" : "denied")} {entry.Address} remote");
            return true;
        }
    }
    void Expire() {
        var now = clock.GetUtcNow();
        foreach (var key in pending.Where(p => now - p.Value.Requested > ApprovalWindow).Select(p => p.Key).ToArray()) {
            if (pending[key].Token is { } t) sessions.Remove(t);
            pending.Remove(key);
        }
    }
    public bool Valid(string token) { lock (sync) return sessions.TryGetValue(token, out var s) && s.Expiry > clock.GetUtcNow(); }
    public void Logout(string token) { lock (sync) { sessions.Remove(token); } audit?.Invoke("logout"); Revoked?.Invoke(token); }
    public bool RevokeSession(string id) {
        string? token;
        lock (sync) { token = sessions.FirstOrDefault(s => s.Value.Id == id).Key; if (token == null) return false; sessions.Remove(token); }
        audit?.Invoke("revoke-session " + id); Revoked?.Invoke(token);
        return true;
    }
}
