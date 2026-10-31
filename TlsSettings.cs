using System.Net;
using System.Security.Cryptography.X509Certificates;

// HTTPS configuration from environment variables set by the launcher (or the shell for direct runs).
sealed class TlsSettings {
    readonly string? path, password;
    readonly object sync = new();
    X509Certificate2? current;
    DateTime loadedWrite;
    public int Port { get; }
    public bool AllowPlainHttp { get; }
    public string[] AllowedHosts { get; }
    public bool Enabled => path != null;
    public TlsSettings(Func<string, string?> env) {
        path = string.IsNullOrWhiteSpace(env("MONITOR_TLS_CERT")) ? null : env("MONITOR_TLS_CERT");
        password = env("MONITOR_TLS_PASSWORD");
        Port = int.TryParse(env("MONITOR_TLS_PORT"), out var p) && p is >= 1 and <= 65535 ? p : 8766;
        AllowPlainHttp = env("MONITOR_ALLOW_HTTP") != "0";
        AllowedHosts = (env("MONITOR_ALLOWED_HOSTS") ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns).Select(h => h.ToLowerInvariant()).ToArray();
        if (Enabled) Certificate();
    }
    // Re-read the file when it changes so renewed certificates apply without restarting the server.
    public X509Certificate2 Certificate() {
        lock (sync) {
            var write = File.GetLastWriteTimeUtc(path!);
            if (current != null && write == loadedWrite) return current;
            try {
                var fresh = X509CertificateLoader.LoadPkcs12FromFile(path!, password);
                if (!fresh.HasPrivateKey) throw new InvalidOperationException("TLS certificate has no private key.");
                current = fresh; loadedWrite = write;
            } catch (Exception) when (current != null) { /* keep serving the previous certificate */ }
            return current ?? throw new InvalidOperationException("TLS certificate could not be loaded.");
        }
    }
    public bool HostAllowed(string host) {
        if (host == "localhost") return true;
        if (IPAddress.TryParse(host, out var address)) {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
        }
        return AllowedHosts.Contains(host.ToLowerInvariant());
    }
}
