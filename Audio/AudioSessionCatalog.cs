using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

record AudioSession(string Id, string DeviceId, string DeviceName, string Name, uint ProcessId, bool System, bool Active, double? Volume, bool? Muted);
record AudioSessionSnapshot(string Revision, AudioSession[] Sessions, bool Simulated);

// Partial extension of AudioDeviceCatalog: per-application volume and mute through the Core Audio session APIs.
sealed partial class AudioDeviceCatalog {
    sealed record SessionEntry(AudioSession Session, ISimpleAudioVolume Volume);
    readonly List<SessionEntry> sessionEntries = [];
    static readonly Guid SessionManagerInterface = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    // Enumerates sessions on every active playback device. COM references stay alive until the next call so SetSession can use them.
    public AudioSessionSnapshot Sessions() {
        Snapshot();
        ReleaseSessions();
        foreach (var device in entries.Where(e => e.Direction == "output" && e.State == "active")) {
            IMMDevice? endpoint = null; IAudioSessionManager2? manager = null; IAudioSessionEnumerator? list = null;
            try {
                if (enumerator.GetDevice(device.WindowsId, out endpoint) < 0) continue;
                var iid = SessionManagerInterface;
                if (endpoint.Activate(ref iid, 23, IntPtr.Zero, out var instance) < 0) continue;
                manager = (IAudioSessionManager2)instance;
                if (manager.GetSessionEnumerator(out list) < 0 || list.GetCount(out var count) < 0) continue;
                for (var index = 0; index < Math.Min(count, 256); index++) {
                    if (list.GetSession(index, out var control) < 0) continue;
                    var keep = false;
                    try {
                        var control2 = (IAudioSessionControl2)control;
                        var volume = (ISimpleAudioVolume)control;
                        if (control2.GetState(out var state) < 0 || state == 2) continue;
                        control2.GetSessionInstanceIdentifier(out var instanceId);
                        control2.GetProcessId(out var pid);
                        var system = control2.IsSystemSoundsSession() == 0;
                        if (volume.GetMasterVolume(out var level) < 0 || volume.GetMute(out var muted) < 0) continue;
                        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.WindowsId + "|" + instanceId)))[..32];
                        sessionEntries.Add(new(new(id, device.Id, device.Name, SessionName(control2, pid, system), pid, system, state == 1, Math.Round(level * 100, 1), muted), volume));
                        keep = true;
                    } finally { if (!keep) Marshal.ReleaseComObject(control); }
                }
            } finally {
                if (list != null) Marshal.ReleaseComObject(list);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (endpoint != null) Marshal.ReleaseComObject(endpoint);
            }
        }
        var sessions = sessionEntries.Select(e => e.Session).OrderByDescending(x => x.Active).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sessions.Select(x => new { x.Id, x.Name, x.DeviceId }).OrderBy(x => x.Id)))))[..24];
        return new(revision, sessions, false);
    }
    static string SessionName(IAudioSessionControl2 control, uint pid, bool system) {
        if (system) return "System sounds";
        try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); if (process.ProcessName.Length > 0) return process.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return control.GetDisplayName(out var display) >= 0 && !string.IsNullOrWhiteSpace(display) ? display : "Application";
    }
    public AudioSession SetSession(string id, double? value, bool? muted) {
        var entry = sessionEntries.FirstOrDefault(e => e.Session.Id == id) ?? throw new InvalidOperationException("Application audio session ended. Refresh applications.");
        var guid = context;
        if (value.HasValue) Marshal.ThrowExceptionForHR(entry.Volume.SetMasterVolume((float)value.Value / 100, ref guid));
        if (muted.HasValue) Marshal.ThrowExceptionForHR(entry.Volume.SetMute(muted.Value, ref guid));
        Marshal.ThrowExceptionForHR(entry.Volume.GetMasterVolume(out var level));
        Marshal.ThrowExceptionForHR(entry.Volume.GetMute(out var isMuted));
        var updated = entry.Session with { Volume = Math.Round(level * 100, 1), Muted = isMuted };
        sessionEntries[sessionEntries.IndexOf(entry)] = entry with { Session = updated };
        return updated;
    }
    void ReleaseSessions() { foreach (var entry in sessionEntries) Marshal.ReleaseComObject(entry.Volume); sessionEntries.Clear(); }
}
