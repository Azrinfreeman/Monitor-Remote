using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

sealed class Preferences {
    public bool AutoStart { get; set; } = true;
    public bool CloseExplained { get; set; }
    public int Port { get; set; } = 8765;
    public string? Address { get; set; }
    public string? TlsCertificatePath { get; set; }
    // The certificate password is stored encrypted for this Windows user (DPAPI); it is never written in plain text.
    public string? TlsPasswordProtected { get; set; }
    [JsonPropertyName("TlsCertificatePassword")] public string? LegacyTlsPassword { get; set; }
    [JsonIgnore] public string? TlsCertificatePassword {
        get {
            if (string.IsNullOrEmpty(TlsPasswordProtected)) return LegacyTlsPassword;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(TlsPasswordProtected), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
        }
        set {
            LegacyTlsPassword = null;
            TlsPasswordProtected = string.IsNullOrEmpty(value) ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }
    public int TlsPort { get; set; } = 8766;
    public bool AllowPlainHttp { get; set; } = true;
    public string? AllowedHosts { get; set; }
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorRemote", "desktop.json");
    public static Preferences Load() {
        try {
            var prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            if (prefs.Port is < 1 or > 65535) prefs.Port = 8765;
            if (!string.IsNullOrEmpty(prefs.LegacyTlsPassword) && string.IsNullOrEmpty(prefs.TlsPasswordProtected)) prefs.TlsCertificatePassword = prefs.LegacyTlsPassword;
            return prefs;
        } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save() {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool StartsAtSignIn() {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("MonitorRemote") is string;
    }
    public static void SetSignIn(bool enabled) {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("MonitorRemote", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("MonitorRemote", false);
    }
}
