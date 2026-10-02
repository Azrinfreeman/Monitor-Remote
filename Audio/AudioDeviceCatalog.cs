using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

record AudioDevice(string Id, string Name, string Direction, string State, bool Supported, bool Default, bool Communications,
    double? Volume, bool? Muted, string? Error = null);
record AudioSnapshot(string Revision, AudioDevice[] Devices, bool Simulated);
record AudioResult(string Id, bool Confirmed, AudioDevice? Device, string? Error = null);
interface IAudioDevices : IDisposable {
    AudioSnapshot Snapshot();
    AudioDevice Set(string id, double? value, bool? muted);
}
[SupportedOSPlatform("windows")]
sealed class AudioDeviceCatalog : IAudioDevices {
    sealed record Entry(string WindowsId, string Id, string Name, string Direction, string State, bool Default, bool Communications, IAudioEndpointVolume? Volume, AudioVolumeNotification? Callback);
    readonly IMMDeviceEnumerator enumerator;
    readonly AudioDeviceNotification notification;
    readonly Guid context = Guid.NewGuid();
    readonly List<Entry> entries = [];
    int dirty = 1;
    DateTime refreshed;
    public AudioDeviceCatalog() {
        enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CoreAudioInterop.EnumeratorClass, true)!)!;
        notification = new(() => Interlocked.Exchange(ref dirty, 1));
        Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(notification));
    }
    public AudioSnapshot Snapshot() {
        if (Interlocked.Exchange(ref dirty, 0) != 0 || DateTime.UtcNow - refreshed > TimeSpan.FromSeconds(2)) Refresh();
        var devices = entries.Select(Read).ToArray();
        var revisionData = devices.Select(d => new { d.Id, d.Name, d.Direction, d.State, d.Default, d.Communications, d.Supported });
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(revisionData))))[..24];
        return new(revision, devices, false);
    }
    void Refresh() {
        foreach (var entry in entries) Release(entry); entries.Clear();
        var defaults = new Dictionary<(int Flow, int Role), string>();
        for (var flow = 0; flow < 2; flow++) for (var role = 1; role < 3; role++) {
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) >= 0) {
                try { if (device.GetId(out var id) >= 0) defaults[(flow, role)] = id; }
                finally { Marshal.ReleaseComObject(device); }
            }
        }
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(2, 15, out var collection));
        try {
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            for (uint index = 0; index < Math.Min(count, 1024); index++) {
                Marshal.ThrowExceptionForHR(collection.Item(index, out var device));
                IAudioEndpointVolume? volume = null;
                try {
                    Marshal.ThrowExceptionForHR(device.GetId(out var id));
                    Marshal.ThrowExceptionForHR(device.GetState(out var state));
                    Marshal.ThrowExceptionForHR(((IMMEndpoint)device).GetDataFlow(out var flow));
                    var name = Friendly(device);
                    AudioVolumeNotification? callback = null;
                    if (state == 1) {
                        var iid = CoreAudioInterop.VolumeInterface;
                        if (device.Activate(ref iid, 23, IntPtr.Zero, out var instance) >= 0) {
                            volume = (IAudioEndpointVolume)instance;
                            callback = new();
                            if (volume.RegisterControlChangeNotify(callback) < 0) callback = null;
                        }
                    }
                    entries.Add(new(id, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32], name,
                        flow == 0 ? "output" : "input", state switch { 1 => "active", 2 => "disabled", 4 => "not present", 8 => "unplugged", _ => "unavailable" },
                        defaults.GetValueOrDefault((flow, 1)) == id, defaults.GetValueOrDefault((flow, 2)) == id, volume, callback));
                    volume = null;
                } finally { if (volume != null) Marshal.ReleaseComObject(volume); Marshal.ReleaseComObject(device); }
            }
        } finally { Marshal.ReleaseComObject(collection); refreshed = DateTime.UtcNow; }
    }
    static string Friendly(IMMDevice device) {
        if (device.OpenPropertyStore(0, out var properties) < 0) return "Audio device";
        try {
            var key = CoreAudioInterop.FriendlyName;
            if (properties.GetValue(ref key, out var value) < 0) return "Audio device";
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? "Audio device" : "Audio device"; }
            finally { CoreAudioInterop.PropVariantClear(ref value); }
        } finally { Marshal.ReleaseComObject(properties); }
    }
    static AudioDevice Read(Entry entry) {
        double? volume = null; bool? muted = null;
        if (entry.Volume != null && entry.Volume.GetMasterVolumeLevelScalar(out var scalar) >= 0 && entry.Volume.GetMute(out var mute) >= 0) { volume = Math.Round(scalar * 100, 1); muted = mute; }
        var supported = volume.HasValue && muted.HasValue;
        return new(entry.Id, entry.Name, entry.Direction, entry.State, supported, entry.Default, entry.Communications, volume, muted, supported ? null : "Volume control unavailable for this device.");
    }
    public AudioDevice Set(string id, double? value, bool? muted) {
        var entry = entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("Audio device disappeared. Refresh devices.");
        if (entry.Volume == null || entry.State != "active") throw new InvalidOperationException("Audio device is unavailable or its volume is unsupported.");
        var guid = context;
        if (value.HasValue) Marshal.ThrowExceptionForHR(entry.Volume.SetMasterVolumeLevelScalar((float)value.Value / 100, ref guid));
        if (muted.HasValue) Marshal.ThrowExceptionForHR(entry.Volume.SetMute(muted.Value, ref guid));
        return Read(entry);
    }
    static void Release(Entry entry) {
        if (entry.Volume == null) return;
        if (entry.Callback != null) entry.Volume.UnregisterControlChangeNotify(entry.Callback);
        Marshal.ReleaseComObject(entry.Volume);
    }
    public void Dispose() { enumerator.UnregisterEndpointNotificationCallback(notification); foreach (var entry in entries) Release(entry); entries.Clear(); Marshal.ReleaseComObject(enumerator); }
}

sealed class AudioDemoProvider : IAudioDevices {
    readonly Dictionary<string, AudioDevice> devices = new() {
        ["demo-speakers"] = new("demo-speakers", "Demo speakers", "output", "active", true, true, false, 45, false),
        ["demo-headphones"] = new("demo-headphones", "Demo headphones", "output", "active", true, false, true, 65, true),
        ["demo-microphone"] = new("demo-microphone", "Demo microphone", "input", "active", true, true, true, 75, false),
        ["demo-offline"] = new("demo-offline", "Demo disconnected HDMI", "output", "unplugged", false, false, false, null, null),
        ["demo-unsupported"] = new("demo-unsupported", "Demo read-only output", "output", "active", false, false, false, null, null)
    };
    public AudioSnapshot Snapshot() => new("demo-audio-v1", devices.Values.ToArray(), true);
    public AudioDevice Set(string id, double? value, bool? muted) {
        if (!devices.TryGetValue(id, out var device) || !device.Supported || device.State != "active") throw new InvalidOperationException("Audio device is unavailable or unsupported.");
        return devices[id] = device with { Volume = value ?? device.Volume, Muted = muted ?? device.Muted };
    }
    public void Dispose() { }
}
