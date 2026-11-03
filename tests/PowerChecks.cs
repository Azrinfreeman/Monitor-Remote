using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

static class PowerChecks {
    sealed class Executor : IPcPowerExecutor {
        public bool SleepSupported { get; set; } = true;
        public bool Simulated => true;
        public int Calls;
        public Func<Task<PcPowerResult>>? OnExecute;
        public Task<PcPowerResult> Execute(string action, CancellationToken stopping) { Calls++; return OnExecute?.Invoke() ?? Task.FromResult(new PcPowerResult("requested", Simulated: true)); }
    }
    sealed class Setup : IDisposable {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "MonitorRemote-PowerChecks-" + Guid.NewGuid().ToString("N"));
        public readonly CheckClock Clock = new();
        public readonly Executor Executor = new();
        public readonly PairingState Pairing;
        public readonly string Owner;
        public PcPowerService Power;
        public int Released;
        public string PathName => Path.Combine(Root, "pc-power.json");
        public Setup(Func<CancellationToken, Task>? prepare = null, bool allowed = true) {
            Pairing = new(Clock); Pairing.Pair("power-test", Pairing.Status().Code!, out var owner); Owner = owner!;
            Power = new(Pairing, new(PathName), Executor, prepare, () => Released++, Clock, () => TimeZoneInfo.Utc, allowed);
        }
        public void Restart() { Power.Dispose(); Power = new(Pairing, new(PathName), Executor, clock: Clock, zone: () => TimeZoneInfo.Utc); }
        public PowerTaskView Create(string action = "shutdown", string mode = "now", long? delay = null) => Power.Create(Owner, Request(action, mode, delay));
        public void Dispose() {
            Power.Dispose();
            if (System.IO.Path.GetDirectoryName(Root) == System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()) && System.IO.Path.GetFileName(Root).StartsWith("MonitorRemote-PowerChecks-") && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    static CreatePcPowerTask Request(string action = "shutdown", string mode = "now", long? delay = null) => new(Guid.NewGuid().ToString("D"), action, mode, delay);
    public static async Task Run(string serverPath) {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("PC power: " + name); checks++; Console.WriteLine("PASS: PC power " + name); }
        void Reject(Action action, int status, string name) {
            try { action(); } catch (PcPowerException e) { Check(e.Status == status, name); return; }
            throw new Exception("PC power expected rejection: " + name);
        }
        using (var s = new Setup(allowed: false)) {
            Reject(() => s.Create(), 403, "default-off permission");
            s.Power.SetAllowed(true);
            Reject(() => s.Power.Create("not-paired", Request()), 401, "admission checks pairing");
            Reject(() => s.Create("execute-shell"), 400, "action whitelist");
            Reject(() => s.Create(mode: "delay", delay: 59), 400, "minimum duration");
            Reject(() => s.Create(mode: "delay", delay: PcPowerService.MaximumDelaySeconds + 1), 400, "maximum duration");
            Reject(() => s.Power.Create(s.Owner, Request() with { DelaySeconds = 60 }), 400, "mixed timing fields");
            var request = Request(); var task = s.Power.Create(s.Owner, request);
            Check(task.DueAtUtc == s.Clock.Now.AddSeconds(10), "immediate action includes ten-second cancellation window");
            Check(s.Power.Create(s.Owner, request).Id == task.Id, "duplicate request returns same task");
            Reject(() => s.Power.Create(s.Owner, request with { Action = "restart" }), 409, "changed payload cannot reuse request ID");
            Reject(() => s.Create(), 409, "single PC-wide pending task");
            Reject(() => s.Power.Cancel(task.Id, task.Revision + 1), 409, "stale cancellation revision");
            s.Power.Cancel(task.Id, task.Revision); s.Power.Cancel(task.Id, task.Revision);
            s.Clock.Now = s.Clock.Now.AddMinutes(1); await s.Power.Tick();
            Check(s.Executor.Calls == 0 && s.Power.Snapshot().History[0].State == "cancelled", "idempotent cancellation prevents dispatch");
        }
        foreach (var action in new[] { "shutdown", "restart", "sleep" }) foreach (var mode in new[] { "now", "delay", "at" }) {
            using var s = new Setup();
            var request = Request(action, mode, mode == "delay" ? 60 : null);
            if (mode == "at") request = request with { LocalDateTime = s.Clock.Now.AddMinutes(2).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss"), TimeZoneId = "UTC" };
            var task = s.Power.Create(s.Owner, request);
            s.Clock.Now = task.DueAtUtc.AddMilliseconds(-1); await s.Power.Tick(); Check(s.Executor.Calls == 0, action + "/" + mode + " does not dispatch early");
            s.Clock.Now = task.DueAtUtc; await s.Power.Tick(); await s.Power.Tick();
            Check(s.Executor.Calls == 1 && s.Power.Snapshot().History[0].State == "requested", action + "/" + mode + " dispatches once without browser");
            Reject(() => s.Power.Cancel(task.Id, task.Revision), 409, "dispatch cancellation cutoff " + action + "/" + mode);
        }
        using (var s = new Setup()) {
            var request = Request(mode: "delay", delay: 86400); var task = s.Power.Create(s.Owner, request);
            s.Clock.Now = task.DueAtUtc; s.Pairing.Logout(s.Owner); await s.Power.Tick();
            Check(s.Executor.Calls == 1, "logout and cookie expiry do not revoke committed scheduling");
        }
        using (var s = new Setup()) {
            var task = s.Create(mode: "delay", delay: 86400); s.Clock.Now = task.DueAtUtc;
            Check(!s.Pairing.Valid(s.Owner), "long task outlives cookie"); await s.Power.Tick(); Check(s.Executor.Calls == 1, "expired cookie does not prevent committed task");
        }
        foreach (var revokeAll in new[] { false, true }) {
            using var s = new Setup(); var task = s.Create();
            if (revokeAll) s.Pairing.Renew(true); else s.Pairing.RevokeSession(s.Pairing.SessionId(s.Owner)!);
            s.Clock.Now = task.DueAtUtc; await s.Power.Tick(); Check(s.Executor.Calls == 0 && s.Power.Snapshot().History[0].State == "cancelled", "PC revocation cancels " + (revokeAll ? "all" : "owner"));
        }
        using (var s = new Setup()) {
            s.Create(); s.Pairing.Pair("other", s.Pairing.Status().Code!, out var other); s.Pairing.RevokeSession(s.Pairing.SessionId(other!)!);
            Check(s.Power.Snapshot().Current != null, "unrelated phone revocation preserves task");
            s.Pairing.Renew(false); Check(s.Power.Snapshot().Current != null, "pairing code renewal preserves task");
            s.Power.SetAllowed(false); Check(s.Power.Snapshot().Current == null, "permission off cancels task");
        }
        foreach (var adjustment in new[] { -30, 61, 0 }) {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var s = new Setup(async token => { entered.SetResult(); await proceed.Task.WaitAsync(token); });
            var task = s.Create(); s.Clock.Now = task.DueAtUtc;
            var tick = s.Power.Tick(); await entered.Task; s.Clock.Now = task.DueAtUtc.AddSeconds(adjustment); proceed.SetResult(); await tick;
            Check(s.Executor.Calls == (adjustment == 0 ? 1 : 0), "final clock check after cleanup " + adjustment);
            Check(s.Released == 1, "transition guard released " + adjustment);
            Check(adjustment < 0 ? s.Power.Snapshot().Current?.State == "pending" : s.Power.Snapshot().History[0].State == (adjustment > 60 ? "missed" : "requested"), "clock outcome " + adjustment);
        }
        foreach (var revoke in new[] { false, true }) {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var s = new Setup(async token => { entered.SetResult(); await proceed.Task.WaitAsync(token); });
            var task = s.Create(); s.Clock.Now = task.DueAtUtc; var tick = s.Power.Tick(); await entered.Task;
            if (revoke) s.Pairing.RevokeSession(s.Pairing.SessionId(s.Owner)!); else s.Power.Cancel(task.Id, task.Revision);
            proceed.SetResult(); await tick; Check(s.Executor.Calls == 0, "cancellation/revocation during cleanup prevents execution " + revoke);
        }
        using (var s = new Setup()) {
            // Hold the exact shared admission gate while a creator queues; revoke first.
            Task<PowerTaskView> creation;
            lock (s.Pairing.PowerGate) {
                creation = Task.Run(() => s.Create());
                s.Pairing.RevokeSession(s.Pairing.SessionId(s.Owner)!);
            }
            try { await creation; throw new Exception("Revoked creator was admitted."); } catch (PcPowerException e) { Check(e.Status == 401 && s.Power.Snapshot().Current == null, "revocation before admission cannot leave an orphan task"); }
        }
        using (var s = new Setup()) {
            var task = s.Create(mode: "delay", delay: 600); s.Restart();
            Check(!s.Power.Snapshot().AllowPower && s.Power.Snapshot().Current?.State == "paused", "restart resets permission and pauses task");
            s.Power.SetAllowed(true); await s.Power.Tick(); Check(s.Executor.Calls == 0 && s.Power.Snapshot().Current?.State == "paused", "enabling permission does not silently resume");
            var restored = s.Power.Snapshot().Current!; s.Power.Resume(restored.Id, restored.Revision);
            s.Clock.Now = task.DueAtUtc; await s.Power.Tick(); Check(s.Executor.Calls == 1, "local resume re-arms original deadline");
        }
        using (var s = new Setup()) {
            var task = s.Create(); s.Clock.Now = task.DueAtUtc.AddSeconds(1); s.Restart();
            Check(s.Power.Snapshot().Current == null && s.Power.Snapshot().History[0].State == "missed", "overdue task never executes on startup");
        }
        using (var s = new Setup()) {
            var task = s.Create(); s.Power.Dispose();
            using (var store = new PcPowerStore(s.PathName)) { var tasks = store.Load(); store.Save(tasks.Select(t => t with { State = "dispatching" }).ToArray()); }
            s.Power = new(s.Pairing, new(s.PathName), s.Executor, clock: s.Clock);
            s.Power.SetAllowed(true); s.Clock.Now = task.DueAtUtc; await s.Power.Tick();
            Check(s.Executor.Calls == 0 && s.Power.Snapshot().History[0].State == "unconfirmed", "crash after dispatch commit never replays");
        }
        using (var s = new Setup()) {
            using var competing = new PcPowerService(s.Pairing, new(s.PathName), s.Executor);
            Check(!competing.Snapshot().Available, "second server cannot share task storage");
            s.Power.Dispose(); File.WriteAllText(s.PathName, "{broken"); s.Restart();
            Check(!s.Power.Snapshot().Available && File.ReadAllText(s.PathName) == "{broken", "corrupt store is preserved and disables feature");
        }
        using (var s = new Setup()) {
            s.Executor.SleepSupported = false; Reject(() => s.Create("sleep"), 400, "unsupported sleep has no fallback");
            var task = s.Create(); s.Executor.OnExecute = () => Task.FromResult(new PcPowerResult("unconfirmed", "simulated lost result"));
            s.Clock.Now = task.DueAtUtc; await s.Power.Tick(); await s.Power.Tick(); Check(s.Executor.Calls == 1 && s.Power.Snapshot().History[0].State == "unconfirmed", "uncertain dispatch never retries");
        }
        using (var s = new Setup()) {
            var task = s.Create();
            // A writer holding the atomic-replace destination prevents persistence before dispatch.
            using (var locked = new FileStream(s.PathName, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                s.Clock.Now = task.DueAtUtc; await s.Power.Tick();
                Check(s.Executor.Calls == 0 && !s.Power.Snapshot().Available, "storage write failure prevents native dispatch");
            }
        }
        foreach (var lateness in new[] { 60, 61 }) {
            using var s = new Setup(); var task = s.Create(); s.Clock.Now = task.DueAtUtc.AddSeconds(lateness); await s.Power.Tick();
            Check(s.Executor.Calls == (lateness == 60 ? 1 : 0), "resume lateness boundary " + lateness);
        }
        using (var s = new Setup(_ => throw new InvalidOperationException("Input cleanup failed."))) {
            var task = s.Create(); s.Clock.Now = task.DueAtUtc; await s.Power.Tick();
            Check(s.Executor.Calls == 0 && s.Released == 1 && s.Power.Snapshot().History[0].State == "failed", "failed preparation releases guard without dispatch");
        }
        using (var s = new Setup()) {
            bool blocked = false;
            var input = new NativeInput(count => blocked ? 0 : (uint)count);
            using var remote = new RemoteSessionManager(s.Pairing, true, true, true, input: input);
            var display = DesktopDisplayCatalog.Snapshot().Displays[0];
            input.Apply(new("button", Button: "left", Down: true), display, [display]); blocked = true;
            bool refused = false;
            try { remote.BeginPcPowerTransition(); } catch (InvalidOperationException) { refused = true; }
            Check(refused && input.HeldCount != 0, "held input release failure blocks PC transition");
            blocked = false; input.ReleaseAll(); remote.EndPcPowerTransition();
        }
        using (var s = new Setup()) {
            var previousMarker = Environment.GetEnvironmentVariable("MONITOR_MUTE_MARKER");
            Directory.CreateDirectory(s.Root); var marker = Path.Combine(s.Root, "mute-marker.txt"); File.WriteAllText(marker, "demo-speakers");
            Environment.SetEnvironmentVariable("MONITOR_MUTE_MARKER", marker);
            using var audio = new AudioControlService();
            using var remote = new RemoteSessionManager(s.Pairing, false, false, true);
            using var sound = new SoundStreamService(remote, s.Pairing, audio);
            using var unblock = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedAudio = audio.Run(_ => { entered.SetResult(); unblock.Wait(); return 0; }, CancellationToken.None);
            try {
                await entered.Task; using var timeout = new CancellationTokenSource(100);
                bool cancelled = false;
                try { await sound.StopForPower(timeout.Token).WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && File.Exists(marker), "blocked mute recovery honors deadline and preserves marker");
            } finally { unblock.Set(); await blockedAudio; Environment.SetEnvironmentVariable("MONITOR_MUTE_MARKER", previousMarker); }
        }
        using (var s = new Setup()) {
            for (var i = 0; i < 10; i++) { var task = s.Create(); s.Power.Cancel(task.Id, task.Revision); }
            Reject(() => s.Create(), 429, "create throttle cannot block cancellation");
            s.Clock.Now = s.Clock.Now.AddMinutes(1); var future = s.Create(); s.Power.Cancel(future.Id, future.Revision);
        }
        // Neither the clean-stop nor crash probe has an action string capable of native power.
        foreach (var crash in new[] { false, true }) {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(typeof(PowerChecks).Assembly.Location); info.ArgumentList.Add("--power-probe-host");
            using var host = Process.Start(info)!;
            var line = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (!int.TryParse(line, out var workerId)) throw new Exception("Harmless worker probe did not start: " + line);
            using var worker = Process.GetProcessById(workerId);
            if (crash) host.Kill(); else { await host.StandardInput.WriteLineAsync("stop"); await host.StandardInput.FlushAsync(); }
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(worker.HasExited, "server-owned job reaps harmless worker on " + (crash ? "crash" : "stop"));
        }
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Reject(() => PcPowerService.ResolveDeadline(Request(mode: "at") with { LocalDateTime = "2026-03-08T02:30:00", TimeZoneId = eastern.Id }, now, eastern), 400, "DST skipped time rejected");
        Reject(() => PcPowerService.ResolveDeadline(Request(mode: "at") with { LocalDateTime = "2026-11-01T01:30:00", TimeZoneId = eastern.Id }, now, eastern), 400, "DST repeated time rejected");
        Reject(() => PcPowerService.ResolveDeadline(Request(mode: "at") with { LocalDateTime = "2026-02-01T12:00:00", TimeZoneId = "UTC" }, now, eastern), 409, "time-zone change requires refreshed form");
        Check(PcPowerService.ResolveDeadline(Request(mode: "at") with { LocalDateTime = "2026-02-01T12:00:00", TimeZoneId = eastern.Id }, now, eastern).Hour == 17, "date is interpreted in PC zone instead of phone zone");
        using var isolatedPersonalStore = new Setup();
        var personalSentinel = Path.Combine(isolatedPersonalStore.Root, "personal-store.json");
        Directory.CreateDirectory(isolatedPersonalStore.Root); File.WriteAllText(personalSentinel, "do not alter this stand-in personal store");
        var previousPowerFile = Environment.GetEnvironmentVariable("MONITOR_PC_POWER_FILE");
        Environment.SetEnvironmentVariable("MONITOR_PC_POWER_FILE", personalSentinel);
        try {
        await using (var fixture = await Fixture.Start(serverPath)) {
            Check((await fixture.Http.GetAsync("/api/pc-power")).StatusCode == HttpStatusCode.Unauthorized, "HTTP pairing required");
            await fixture.Pair();
            Check((await fixture.Post("/api/pc-power/tasks", Request())).StatusCode == HttpStatusCode.Forbidden, "HTTP default-off permission enforced");
            Check((await fixture.Management("pc-power:on")).AllowPcPower, "local management enables power");
            using var wrongOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/pc-power/tasks") { Content = JsonContent.Create(Request()) }; wrongOrigin.Headers.Add("Origin", "http://evil.example"); wrongOrigin.Headers.Add("X-Monitor-Request", "1");
            Check((await fixture.Http.SendAsync(wrongOrigin)).StatusCode == HttpStatusCode.Forbidden, "wrong-Origin power POST rejected");
            var request = Request(mode: "delay", delay: 600);
            var response = await fixture.Post("/api/pc-power/tasks", request); Check(response.StatusCode == HttpStatusCode.Accepted, "HTTP creation persists before acceptance");
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(); var task = body.GetProperty("task");
            Check((await fixture.Post("/api/pc-power/tasks", request)).StatusCode == HttpStatusCode.Accepted, "HTTP duplicate reconciles");
            var conflict = await fixture.Post("/api/pc-power/tasks", Request()); var error = await conflict.Content.ReadFromJsonAsync<JsonElement>();
            Check(conflict.StatusCode == HttpStatusCode.Conflict && error.GetProperty("code").GetString() == "task_conflict" && error.GetProperty("current").GetProperty("id").GetString() == task.GetProperty("id").GetString(), "HTTP conflict supplies current task and code");
            var statusText = await fixture.Http.GetStringAsync("/api/pc-power"); Check(!statusText.Contains("OwnerId") && !statusText.Contains("ownerId"), "HTTP status excludes owner identity");
            Check((await fixture.Post("/api/pc-power/tasks/" + task.GetProperty("id").GetString() + "/cancel", new { revision = task.GetProperty("revision").GetInt64() })).IsSuccessStatusCode, "HTTP cancellation");
            var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/pc-power/tasks") { Content = JsonContent.Create(new { requestId = Guid.NewGuid(), action = "shutdown", mode = "now", command = "arbitrary" }) };
            malformed.Headers.Add("Origin", fixture.Http.BaseAddress!.GetLeftPart(UriPartial.Authority)); malformed.Headers.Add("X-Monitor-Request", "1");
            Check((await fixture.Http.SendAsync(malformed)).StatusCode == HttpStatusCode.BadRequest, "unknown payload fields rejected");
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.MuteMarkerPath)!);
            File.WriteAllText(fixture.MuteMarkerPath, "demo-speakers"); File.SetAttributes(fixture.MuteMarkerPath, FileAttributes.ReadOnly);
            try {
                Check((await fixture.Post("/api/pc-power/tasks", Request())).StatusCode == HttpStatusCode.Accepted, "read-only mute marker test accepted for simulated preparation");
                await Task.Delay(TimeSpan.FromSeconds(12));
                var afterFailure = await fixture.Http.GetFromJsonAsync<JsonElement>("/api/pc-power");
                Check(afterFailure.GetProperty("available").GetBoolean() && afterFailure.GetProperty("history")[0].GetProperty("state").GetString() == "failed" && File.Exists(fixture.MuteMarkerPath), "mute-marker access failure prevents dispatch and leaves HTTP server available");
            } finally { if (File.Exists(fixture.MuteMarkerPath)) File.SetAttributes(fixture.MuteMarkerPath, FileAttributes.Normal); }
        }
        Check(File.ReadAllText(personalSentinel) == "do not alter this stand-in personal store", "server fixture overrides inherited personal-store path without altering it");
        } finally { Environment.SetEnvironmentVariable("MONITOR_PC_POWER_FILE", previousPowerFile); }
        Console.WriteLine($"PASS: {checks} PC power checks. Fake execution only; no PC power changes.");
    }
}
