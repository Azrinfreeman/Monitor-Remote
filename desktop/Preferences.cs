using System.Text.Json;
using Microsoft.Win32;

sealed class Preferences {
    public bool AutoStart { get; set; } = true;
    public bool CloseExplained { get; set; }
    public int Port { get; set; } = 8765;
    public string? Address { get; set; }
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorRemote", "desktop.json");
    public static Preferences Load() {
        try {
            var prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            if (prefs.Port is < 1 or > 65535) prefs.Port = 8765;
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
