using System.Globalization;

sealed class PcPowerService : BackgroundService {
    public const int WarningSeconds = 10, MaximumLatenessSeconds = 60;
    public const long MinimumDelaySeconds = 60, MaximumDelaySeconds = 365 * 24 * 60 * 60;
    readonly PairingState pairing;
    readonly PcPowerStore store;
    readonly IPcPowerExecutor executor;
    readonly TimeProvider clock;
    readonly Func<TimeZoneInfo> zone;
    readonly Func<CancellationToken, Task> prepare;
    readonly Action release;
    readonly HashSet<string> revoked = [];
    readonly Dictionary<string, (DateTimeOffset Start, int Count)> attempts = [];
    readonly SemaphoreSlim ticking = new(1);
    List<PcPowerTask> tasks = [];
    bool allowed, busy;
    string? storageError, warned;
    object Gate => pairing.PowerGate;
    public PcPowerService(PairingState pairing, PcPowerStore store, IPcPowerExecutor executor,
        Func<CancellationToken, Task>? prepare = null, Action? release = null, TimeProvider? clock = null,
        Func<TimeZoneInfo>? zone = null, bool allowed = false) {
        this.pairing = pairing; this.store = store; this.executor = executor;
        this.prepare = prepare ?? (_ => Task.CompletedTask); this.release = release ?? (() => { });
        this.clock = clock ?? TimeProvider.System; this.zone = zone ?? (() => { TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local; });
        this.allowed = allowed;
        try {
            tasks = store.Load().Select(t => t.State == "dispatching" ? t with { State = "unconfirmed", Revision = t.Revision + 1, Error = "Server stopped after dispatch. Not retried." } :
                t.State is "pending" or "paused" ? t with { State = t.DueAtUtc <= Now ? "missed" : "paused", Revision = t.Revision + 1 } : t).ToList();
            if (tasks.Count != 0) store.Save(tasks);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException) { StorageFailed(); }
        pairing.PcRevoked += OnRevoked;
    }
    DateTimeOffset Now => clock.GetUtcNow();
    PcPowerTask? Current => tasks.FirstOrDefault(t => t.Active);
    void EnsureStore() { if (storageError != null) throw new PcPowerException(503, "storage_unavailable", storageError); }
    void StorageFailed() { storageError = "Power task storage is unavailable. Check pc-power.json, its permissions and other running servers; then restart."; allowed = false; }
    void Commit(List<PcPowerTask> next) {
        next = next.Where(t => t.Active).Concat(next.Where(t => !t.Active).Take(20)).ToList();
        try { store.Save(next); tasks = next; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StorageFailed(); throw new PcPowerException(503, "storage_unavailable", storageError!); }
    }
    void Replace(PcPowerTask task) => Commit(tasks.Select(t => t.Id == task.Id ? task : t).ToList());
    public PcPowerSnapshot Snapshot() {
        lock (Gate) {
            var timezone = zone(); var now = Now;
            return new(storageError == null && (executor.Simulated || OperatingSystem.IsWindows()), allowed, executor.SleepSupported,
                Environment.MachineName, now, timezone.Id, timezone.DisplayName, TimeZoneInfo.ConvertTime(now, timezone), Current?.View(timezone),
                tasks.Where(t => !t.Active).Select(t => t.View(timezone)).ToArray(), storageError, executor.Simulated);
        }
    }
    public ManagementReply Status(ManagementReply reply) {
        lock (Gate) return reply with { AllowPcPower = allowed, PowerTask = Current?.View(), PowerError = storageError };
    }
    public void SetAllowed(bool value) {
        lock (Gate) {
            if (!value) allowed = false;
            EnsureStore();
            if (!value && Current is { State: "pending" or "paused" } task) Replace(task with { State = "cancelled", Revision = task.Revision + 1 });
            allowed = value; AuditLog.Write("pc-power-permission " + (value ? "on" : "off"));
        }
    }
    public PowerTaskView Create(string owner, CreatePcPowerTask request) {
        lock (Gate) {
            EnsureStore();
            var ownerId = pairing.SessionId(owner) ?? throw new PcPowerException(401, "pairing_required", "Pair with the PC again.");
            if (!executor.Simulated && !OperatingSystem.IsWindows()) throw new PcPowerException(503, "unsupported_platform", "PC power requires Windows.");
            if (!allowed) throw new PcPowerException(403, "permission_required", "Enable PC shutdown, restart and sleep in the PC app.");
            if (request.RequestId == null || !Guid.TryParseExact(request.RequestId, "D", out var requestId) || request.Action is not ("shutdown" or "restart" or "sleep"))
                throw new PcPowerException(400, "invalid_request", "Select a valid action and request ID.");
            request = request with { RequestId = requestId.ToString("D") };
            var existing = tasks.FirstOrDefault(t => t.RequestId == request.RequestId);
            if (existing != null) {
                if (existing.Request != request) throw new PcPowerException(409, "request_id_conflict", "This request ID was used for a different task.", existing.View());
                return existing.View();
            }
            if (revoked.Contains(ownerId)) throw new PcPowerException(403, "revoked", "This phone was revoked on the PC.");
            var now = Now;
            attempts.TryGetValue(ownerId, out var throttle);
            throttle = now - throttle.Start >= TimeSpan.FromMinutes(1) ? (now, 1) : (throttle.Start, throttle.Count + 1);
            attempts[ownerId] = throttle;
            if (throttle.Count > 10) throw new PcPowerException(429, "rate_limited", "Too many power tasks. Wait one minute; cancellation is still available.");
            if (attempts.Count > 256) foreach (var key in attempts.Where(a => now - a.Value.Start > TimeSpan.FromMinutes(1)).Select(a => a.Key).ToArray()) attempts.Remove(key);
            if (request.Action == "sleep" && !executor.SleepSupported) throw new PcPowerException(400, "sleep_unsupported", "Windows does not report a supported sleep state.");
            var due = ResolveDeadline(request, now, zone());
            if (Current != null || busy) throw new PcPowerException(409, "task_conflict", "Cancel the current power task before creating another.", Current?.View());
            var task = new PcPowerTask(Guid.NewGuid().ToString("D"), request.RequestId, ownerId, request, now, due);
            Commit(tasks.Where(t => !t.Active).Take(20).Prepend(task).ToList());
            AuditLog.Write($"pc-power-created {task.Id} {request.Action} {due:O}");
            return task.View();
        }
    }
    internal static DateTimeOffset ResolveDeadline(CreatePcPowerTask request, DateTimeOffset now, TimeZoneInfo timezone) {
        DateTimeOffset due;
        switch (request.Mode) {
            case "now" when request.DelaySeconds == null && request.LocalDateTime == null && request.TimeZoneId == null: return now.AddSeconds(WarningSeconds);
            case "delay" when request.DelaySeconds is >= MinimumDelaySeconds and <= MaximumDelaySeconds && request.LocalDateTime == null && request.TimeZoneId == null:
                return now.AddSeconds(request.DelaySeconds.Value);
            case "at" when request.DelaySeconds == null && request.LocalDateTime != null && request.TimeZoneId != null:
                if (request.TimeZoneId != timezone.Id) throw new PcPowerException(409, "time_zone_changed", "The PC time zone changed. Refresh and choose the time again.");
                if (!DateTime.TryParseExact(request.LocalDateTime, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) break;
                local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                if (timezone.IsInvalidTime(local) || timezone.IsAmbiguousTime(local)) throw new PcPowerException(400, "ambiguous_time", "This time is skipped or repeated by daylight saving. Choose another time.");
                due = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timezone));
                if (due - now < TimeSpan.FromSeconds(MinimumDelaySeconds) || due - now > TimeSpan.FromSeconds(MaximumDelaySeconds)) break;
                return due;
        }
        throw new PcPowerException(400, "invalid_time", "Use one timing mode: now, 1 minute to 365 days, or a future PC date/time within that range.");
    }
    public PowerTaskView Cancel(string id, long revision) {
        lock (Gate) {
            EnsureStore();
            var task = tasks.FirstOrDefault(t => t.Id == id) ?? throw new PcPowerException(409, "task_missing", "This power task is no longer available.", Current?.View());
            if (task.State == "cancelled") return task.View();
            if (task.State is not ("pending" or "paused")) throw new PcPowerException(409, "already_dispatched", "Already sent to Windows or finished; cancellation is no longer available.", task.View());
            if (task.Revision != revision) throw new PcPowerException(409, "stale_revision", "Task changed. Refresh before cancelling.", task.View());
            var cancelled = task with { State = "cancelled", Revision = task.Revision + 1 };
            Replace(cancelled); AuditLog.Write("pc-power-cancelled " + id); return cancelled.View();
        }
    }
    public void Resume(string id, long revision) {
        lock (Gate) {
            EnsureStore();
            if (!allowed) throw new PcPowerException(403, "permission_required", "Enable PC power permission before resuming.");
            var task = Current;
            if (task == null || task.Id != id || task.Revision != revision || task.State != "paused") throw new PcPowerException(409, "stale_revision", "Task changed. Refresh before resuming.");
            Replace(task with { State = task.DueAtUtc <= Now ? "missed" : "pending", Revision = task.Revision + 1 });
        }
    }
    void OnRevoked(string? id) {
        lock (Gate) {
            if (id != null) revoked.Add(id);
            if (Current is { State: "pending" or "paused" } task && (id == null || task.OwnerId == id)) {
                try { Replace(task with { State = "cancelled", Revision = task.Revision + 1 }); }
                catch (PcPowerException) { /* Storage failure disables dispatch; next launch restores paused. */ }
            }
        }
    }
    internal async Task Tick(CancellationToken stopping = default) {
        if (!await ticking.WaitAsync(0, stopping)) return;
        bool prepared = false;
        string? candidateId = null;
        try {
            PcPowerTask task;
            lock (Gate) {
                if (!allowed || storageError != null || Current is not { State: "pending" } pending || busy) return;
                task = pending;
                candidateId = task.Id;
                if (Now >= task.DueAtUtc.AddSeconds(-WarningSeconds) && warned != task.Id) {
                    warned = task.Id; Console.WriteLine($"PC power warning: {task.Request.Action} at {task.DueAtUtc:O}. Cancel from the phone or PC app.");
                }
                if (Now < task.DueAtUtc) return;
                if (Now - task.DueAtUtc > TimeSpan.FromSeconds(MaximumLatenessSeconds)) { Replace(task with { State = "missed", Revision = task.Revision + 1 }); return; }
            }
            prepared = true;
            using var cleanupTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            cleanupTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await prepare(cleanupTimeout.Token);
            lock (Gate) {
                if (!allowed || storageError != null || Current?.Id != task.Id || Current.Revision != task.Revision || revoked.Contains(task.OwnerId) || stopping.IsCancellationRequested) return;
                var now = Now;
                if (now < task.DueAtUtc) return;
                if (now - task.DueAtUtc > TimeSpan.FromSeconds(MaximumLatenessSeconds)) { Replace(task with { State = "missed", Revision = task.Revision + 1 }); return; }
                task = task with { State = "dispatching", DispatchedAtUtc = now, Revision = task.Revision + 1 };
                Replace(task); busy = true;
            }
            var result = await executor.Execute(task.Request.Action, stopping);
            lock (Gate) {
                Replace(task with { State = result.State, Error = result.Error, Simulated = result.Simulated, Revision = task.Revision + 1 });
                AuditLog.Write($"pc-power-{result.State} {task.Id} {task.Request.Action}");
            }
        } catch (Exception e) when (e is PcPowerException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) {
            lock (Gate) {
                if (storageError == null && Current is { } task && task.Id == candidateId) {
                    try { Replace(task with { State = task.State == "dispatching" ? "unconfirmed" : "failed", Error = e is OperationCanceledException ? "Power preparation stopped or timed out. Not retried." : e.Message, Revision = task.Revision + 1 }); }
                    catch (PcPowerException) { }
                }
            }
        } finally {
            if (prepared) release();
            lock (Gate) busy = false;
            ticking.Release();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock); while (await timer.WaitForNextTickAsync(stoppingToken)) await Tick(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    public override void Dispose() { pairing.PcRevoked -= OnRevoked; base.Dispose(); store.Dispose(); }
}
