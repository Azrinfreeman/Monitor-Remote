// Append-only local record of pairing, approval, revocation and permission events. Never records secrets or typed content.
static class AuditLog {
    static readonly object Sync = new();
    static string FilePath => Environment.GetEnvironmentVariable("MONITOR_AUDIT_FILE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorRemote", "audit.log");
    public static void Write(string message) {
        try {
            lock (Sync) {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* auditing must never break the server */ }
    }
}
