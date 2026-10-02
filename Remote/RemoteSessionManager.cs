using System.Diagnostics;

sealed class FeatureException(int status, string message) : Exception(message) { public int Status { get; } = status; }
sealed class RemoteSession {
    public readonly string Id = Guid.NewGuid().ToString("N");
    public readonly string Owner;
    public readonly DesktopDisplay Display;
    public readonly DisplaySnapshot Snapshot;
    public readonly string Quality;
    public readonly CancellationTokenSource Cancellation = new();
    public readonly CancellationToken Token;
    public bool ControlEnabled, FramesBound, ControlBound;
    public long Heartbeat = Stopwatch.GetTimestamp(), Sequence, CommandSequence, RateStart = Stopwatch.GetTimestamp();
    public int RateCount;
    public readonly Dictionary<long, long> Frames = [];
    public RemoteSession(string owner, DesktopDisplay display, DisplaySnapshot snapshot, string quality) {
        Owner = owner; Display = display; Snapshot = snapshot; Quality = quality; Token = Cancellation.Token;
    }
}
sealed class RemoteSessionManager : IDisposable {
    readonly object sync = new();
    readonly PairingState pairing;
    readonly NativeInput input = new();
    readonly CancellationTokenSource stopping = new();
    readonly Task watch;
    RemoteSession? session;
    bool allowViewing, allowInput, allowAudio;
    int poweringOff;
    public RemoteSessionManager(PairingState pairing, bool viewing, bool controlling, bool audio) {
        this.pairing = pairing; allowViewing = viewing; allowInput = viewing && controlling; allowAudio = audio;
        pairing.Revoked += OnRevoked;
        watch = Watch();
    }
    public object Capabilities() { lock (sync) return new { remoteDesktop = true, audio = true, allowViewing, allowInput, allowAudio, active = session != null }; }
    public bool Active { get { lock (sync) return session != null; } }
    public void BeginPowerOff() { lock (sync) { if (session != null) throw new FeatureException(409, "Stop remote viewing before turning displays off."); poweringOff++; } }
    public void EndPowerOff() { lock (sync) poweringOff = Math.Max(0, poweringOff - 1); }
    public bool AudioAllowed(string owner) { lock (sync) return allowAudio && pairing.Valid(owner); }
    public bool ViewingAllowed { get { lock (sync) return allowViewing; } }
    public ManagementReply Status() {
        lock (sync) return pairing.Status() with { AllowViewing = allowViewing, AllowInput = allowInput, AllowAudio = allowAudio,
            RemoteState = session == null ? "Idle" : session.ControlEnabled ? "Remote control active" : "Screen sharing active", DisplayName = session?.Display.Name };
    }
    public ManagementReply Manage(string command) {
        lock (sync) {
            switch (command) {
                case "view:on": allowViewing = true; break;
                case "view:off": allowViewing = allowInput = false; StopLocked(); break;
                case "input:on": if (!allowViewing) return Status() with { Error = "Enable screen viewing first." }; allowInput = true; break;
                case "input:off": allowInput = false; if (session != null) session.ControlEnabled = false; input.ReleaseAll(); break;
                case "audio:on": allowAudio = true; break;
                case "audio:off": allowAudio = false; break;
                case "remote:stop": StopLocked(); break;
                default: return Status() with { Error = "Unknown command" };
            }
            return Status();
        }
    }
    public RemoteSession Start(string owner, StartRemote request) {
        if (request.Quality is not ("low" or "balanced" or "clear")) throw new FeatureException(400, "Select a valid viewing quality.");
        var snapshot = DesktopDisplayCatalog.Snapshot();
        var display = snapshot.Displays.FirstOrDefault(d => d.Id == request.DisplayId);
        if (display == null || request.Revision != snapshot.Revision) throw new FeatureException(409, "Display list changed. Refresh and select a display.");
        if (!DesktopDisplayCatalog.Interactive()) throw new FeatureException(409, "Unlock Windows and return to its normal desktop first.");
        lock (sync) {
            if (!pairing.Valid(owner)) throw new FeatureException(401, "Pair with the PC again.");
            if (!allowViewing) throw new FeatureException(403, "Enable Allow screen viewing in the PC app.");
            if (poweringOff != 0) throw new FeatureException(503, "A display-off request is in progress. Wake displays before viewing.");
            if (session != null) throw new FeatureException(409, "Another remote session is active. Stop it on the PC or its phone first.");
            if (input.HeldCount != 0) throw new FeatureException(503, "Previous input cleanup is pending. Return to the normal Windows desktop and retry.");
            return session = new(owner, display, snapshot, request.Quality);
        }
    }
    public RemoteSession Bind(string owner, string? id, bool frames) {
        lock (sync) {
            var current = Owned(owner, id);
            if (frames ? current.FramesBound : current.ControlBound) throw new FeatureException(409, "This remote connection is already attached.");
            if (frames) current.FramesBound = true; else current.ControlBound = true;
            return current;
        }
    }
    public void Configure(string owner, ConfigureRemote request) {
        lock (sync) {
            var current = Owned(owner, request.RemoteSessionId);
            if (request.ControlEnabled && !allowInput) throw new FeatureException(403, "Enable Allow mouse and keyboard control in the PC app.");
            current.ControlEnabled = request.ControlEnabled;
            if (!request.ControlEnabled) input.ReleaseAll();
        }
    }
    public void Stop(string owner, string? id) {
        lock (sync) {
            if (session == null) return;
            Owned(owner, id); StopLocked();
        }
    }
    RemoteSession Owned(string owner, string? id) {
        if (!pairing.Valid(owner)) throw new FeatureException(401, "Pairing session expired.");
        if (session == null || session.Id != id || session.Owner != owner || !allowViewing) throw new FeatureException(403, "Remote session unavailable.");
        return session;
    }
    public bool Valid(RemoteSession current) { lock (sync) return session == current && allowViewing && pairing.Valid(current.Owner) && !current.Token.IsCancellationRequested; }
    public long Frame(RemoteSession current, long capturedAt) {
        lock (sync) {
            Owned(current.Owner, current.Id);
            var sequence = ++current.Sequence; current.Frames[sequence] = capturedAt;
            foreach (var old in current.Frames.Keys.Where(k => k < sequence - 32).ToArray()) current.Frames.Remove(old);
            return sequence;
        }
    }
    public object Handle(RemoteSession current, ControlMessage message) {
        lock (sync) {
            Owned(current.Owner, current.Id);
            if (Stopwatch.GetElapsedTime(current.RateStart).TotalSeconds >= 1) { current.RateStart = Stopwatch.GetTimestamp(); current.RateCount = 0; }
            if (++current.RateCount > 120) throw new FeatureException(429, "Remote input rate exceeded.");
            if (message.Type == "heartbeat") {
                current.Heartbeat = Stopwatch.GetTimestamp();
                return new { type = "heartbeat", controlEnabled = current.ControlEnabled, allowInput };
            }
            if (message.Sequence <= current.CommandSequence) throw new FeatureException(409, "Duplicate or outdated input rejected.");
            current.CommandSequence = message.Sequence;
            if (message.Type == "release") { input.ReleaseAll(); return new { type = "ack", sequence = message.Sequence, inserted = true }; }
            if (!current.ControlEnabled || !allowInput) throw new FeatureException(403, "Remote input is disabled.");
            if (message.Revision != current.Snapshot.Revision || !current.Frames.TryGetValue(message.FrameSequence, out var capturedAt) || Stopwatch.GetElapsedTime(capturedAt).TotalSeconds > 2)
                throw new FeatureException(409, "The displayed frame is stale. Wait for a fresh screen before controlling it.");
            var actual = DesktopDisplayCatalog.Snapshot();
            if (actual.Revision != current.Snapshot.Revision) throw new FeatureException(409, "Display layout changed. Start a new viewing session.");
            try { input.Apply(message, current.Display, actual.Displays); }
            catch { input.ReleaseAll(); throw; }
            return new { type = "ack", sequence = message.Sequence, inserted = true, simulated = DesktopDisplayCatalog.Demo };
        }
    }
    public object DemoStatus() { lock (sync) return new { held = input.HeldCount, operations = input.Operations, active = session != null }; }
    public void End(RemoteSession current) { lock (sync) if (session == current) StopLocked(); }
    void StopLocked() {
        if (session != null) { var old = session; session = null; old.Cancellation.Cancel(); old.Cancellation.Dispose(); }
        input.ReleaseAll();
    }
    void OnRevoked(string? owner) { lock (sync) if (session != null && (owner == null || session.Owner == owner)) StopLocked(); }
    async Task Watch() {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(stopping.Token)) {
                lock (sync) {
                    if (session != null && (!pairing.Valid(session.Owner) || Stopwatch.GetElapsedTime(session.Heartbeat).TotalSeconds > 3 || !DesktopDisplayCatalog.Interactive())) StopLocked();
                    if (session == null && input.HeldCount != 0 && DesktopDisplayCatalog.Interactive()) input.ReleaseAll();
                }
            }
        } catch (OperationCanceledException) { }
    }
    public void Dispose() { pairing.Revoked -= OnRevoked; stopping.Cancel(); lock (sync) StopLocked(); stopping.Dispose(); }
}
