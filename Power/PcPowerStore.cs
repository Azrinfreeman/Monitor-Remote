using System.Text.Json;

// The lock covers one user's store, even if two server ports are running.
sealed class PcPowerStore : IDisposable {
    sealed record Document(int Version, PcPowerTask[] Tasks);
    readonly string path;
    FileStream? ownership;
    public PcPowerStore(string? path = null) {
        this.path = path ?? Environment.GetEnvironmentVariable("MONITOR_PC_POWER_FILE") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorRemote", "pc-power.json");
        this.path = Path.GetFullPath(this.path);
    }
    public PcPowerTask[] Load() {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ownership = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 128 * 1024) throw new IOException("Power task file is too large.");
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
        if (document is not { Version: 1, Tasks: not null } || document.Tasks.Length > 21)
            throw new IOException("Unsupported power task file. Preserve it and restore a valid backup.");
        var tasks = document.Tasks;
        if (tasks.Any(t => t == null || t.Request == null || !Guid.TryParse(t.Id, out _) || !Guid.TryParse(t.RequestId, out _) ||
            string.IsNullOrWhiteSpace(t.OwnerId) || t.Revision < 1 || t.Request.Action is not ("shutdown" or "restart" or "sleep") ||
            t.Request.Mode is not ("now" or "delay" or "at") || t.State is not ("pending" or "paused" or "dispatching" or "requested" or "failed" or "unconfirmed" or "cancelled" or "missed")) ||
            tasks.Count(t => t.Active) > 1 || tasks.Select(t => t.RequestId).Distinct().Count() != tasks.Length)
            throw new IOException("Invalid power task file. Preserve it and restore a valid backup.");
        return tasks;
    }
    public void Save(IReadOnlyCollection<PcPowerTask> tasks) {
        if (ownership == null) throw new IOException("Power task storage is not locked.");
        var temporary = path + ".tmp";
        try {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(file, new Document(1, tasks.ToArray()));
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => ownership?.Dispose();
}
