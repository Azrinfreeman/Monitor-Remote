using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MonitorRemoteDesktop;

public partial class App : Application {
    public static DispatcherQueue? UiQueue { get; private set; }
    public static MainWindow? Main { get; private set; }
    public App() { InitializeComponent(); }
    // Startup failures are written next to the preferences so a user can report them; nothing sensitive is logged.
    static void Report(Exception error) {
        try {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorRemote", "launcher-error.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + error);
        } catch (IOException) { }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args) {
        UiQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += (_, e) => Report(e.Exception);
        try { Main = new MainWindow(); Main.Activate(); }
        catch (Exception error) { Report(error); throw; }
    }
}
