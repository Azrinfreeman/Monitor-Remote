using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace MonitorRemoteDesktop;

public sealed partial class MainWindow : Window {
    sealed record AddressChoice(string Address, string Name) { public override string ToString() => Name + " — " + Address; }
    static readonly Windows.UI.Color Green = Windows.UI.Color.FromArgb(255, 48, 209, 88), Orange = Windows.UI.Color.FromArgb(255, 255, 159, 10),
        Red = Windows.UI.Color.FromArgb(255, 255, 69, 58), Gray = Windows.UI.Color.FromArgb(255, 142, 142, 147);
    readonly Preferences prefs = Preferences.Load();
    readonly ServerController server = new();
    readonly DispatcherQueueTimer timer;
    readonly Tray tray;
    bool busy, exiting, ready, applyingPermissions, remoteActive, approving, soundActive, shown;
    int activePort;
    DateTimeOffset? expires;

    public MainWindow() {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var scale = (double)GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96;
        AppWindow.Resize(new SizeInt32((int)(980 * scale), (int)(860 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsMaximizable = false; }

        tray = new Tray(WinRT.Interop.WindowNative.GetWindowHandle(this), Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), "Monitor Remote");
        tray.Activate = RestoreWindow;
        tray.Items = () => [(1, "Show window", true), (0, "", true), (2, "Start server", !busy && !exiting && !server.Running), (3, "Stop server", !busy && !exiting && server.Running),
            (4, "Stop remote session", ready && remoteActive && !busy && !exiting), (0, "", true), (5, "Open controller", ready), (6, "Exit", true)];
        tray.Command = id => DispatcherQueue.TryEnqueue(async () => {
            switch (id) {
                case 1: RestoreWindow(); break;
                case 2: await Execute(Start); break;
                case 3: await Execute(Stop); break;
                case 4: await Execute(async () => Apply(await server.Send("remote:stop"))); break;
                case 5: OpenController(); break;
                case 6: await ExitApp(); break;
            }
        });

        timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += async (_, _) => await Tick();

        PortBox.Value = prefs.Port;
        AutoStartToggle.IsOn = prefs.AutoStart;
        SignInToggle.IsOn = Preferences.StartsAtSignIn();

        StartButton.Click += async (_, _) => await Execute(Start);
        StopButton.Click += async (_, _) => await Execute(Stop);
        RestartButton.Click += async (_, _) => await Execute(async () => { await Stop(); await Start(); });
        RenewButton.Click += async (_, _) => await Execute(async () => { Apply(await server.Send("renew")); Log("Pairing code renewed; paired phones remain connected."); });
        RevokeButton.Click += async (_, _) => await Execute(async () => { Apply(await server.Send("revoke")); Log("All phone sessions revoked and pairing code renewed."); });
        foreach (var (toggle, command) in new[] { (ViewingToggle, "view"), (InputToggle, "input"), (AudioToggle, "audio"), (SoundToggle, "sound") })
            toggle.Toggled += async (_, _) => {
                if (applyingPermissions) return;
                await Execute(async () => Apply(await server.Send(command + (toggle.IsOn ? ":on" : ":off"))));
            };
        RemoteStopButton.Click += async (_, _) => await Execute(async () => Apply(await server.Send("remote:stop")));
        CopyButton.Click += (_, _) => Copy(UrlBox.Text);
        CopyLogsButton.Click += (_, _) => Copy(DiagnosticsBox.Text);
        OpenButton.Click += (_, _) => OpenController();
        ExitButton.Click += async (_, _) => await ExitApp();
        RefreshAddressesButton.Click += (_, _) => RefreshAddresses();
        AddressBox.SelectionChanged += (_, _) => { prefs.Address = (AddressBox.SelectedItem as AddressChoice)?.Address; Save(); UpdateUrl(); };
        AutoStartToggle.Toggled += (_, _) => { prefs.AutoStart = AutoStartToggle.IsOn; Save(); };
        PortBox.ValueChanged += (_, args) => { if (!double.IsNaN(args.NewValue)) { prefs.Port = (int)args.NewValue; Save(); } };
        var updatingSignIn = false;
        SignInToggle.Toggled += (_, _) => {
            if (updatingSignIn) return;
            try { Preferences.SetSignIn(SignInToggle.IsOn); }
            catch (Exception error) { updatingSignIn = true; SignInToggle.IsOn = !SignInToggle.IsOn; updatingSignIn = false; Log("Could not update sign-in setting: " + error.Message); }
        };
        server.Exited += () => DispatcherQueue.TryEnqueue(() => {
            if (!busy && !exiting) { Clear(); SetStatus("Server exited — press Start to retry", Red); Log("Owned server exited."); UpdateControls(); }
        });
        AppWindow.Closing += (_, e) => {
            if (exiting) return;
            e.Cancel = true;
            if (!prefs.CloseExplained) {
                tray.Balloon("Monitor Remote", "Still running in the system tray. Use Exit app to stop it.");
                prefs.CloseExplained = true; Save();
            }
            AppWindow.Hide();
        };
        Activated += async (_, _) => {
            if (shown) return;
            shown = true; RefreshAddresses(); timer.Start();
            if (prefs.AutoStart) await Execute(Start);
        };
        Clear(); SetStatus("Stopped", Gray);
        UpdateControls();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    public void RestoreWindow() { AppWindow.Show(); if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore(); Activate(); }

    void SetStatus(string text, Windows.UI.Color dot) { StatusText.Text = text; StatusDot.Background = new SolidColorBrush(dot); }
    void Copy(string text) {
        try { if (text.Length > 0) { var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package); } }
        catch { Log("Clipboard unavailable. Try again."); }
    }
    void Save() { try { prefs.Save(); } catch { Log("Could not save preferences."); } }
    void Log(string message) {
        var lines = DiagnosticsBox.Text.Split('\r', '\n').Where(l => l.Length > 0).Append(DateTime.Now.ToString("HH:mm:ss") + " " + message).TakeLast(80);
        DiagnosticsBox.Text = string.Join("\r\n", lines);
        DiagnosticsBox.SelectionStart = DiagnosticsBox.Text.Length;
    }
    async Task Tick() {
        if (expires != null) ExpiryText.Text = expires > DateTimeOffset.UtcNow ? $"Expires in {Math.Ceiling((expires.Value - DateTimeOffset.UtcNow).TotalSeconds)} seconds" : "Expired — choose New pairing code";
        if (!busy && ready && !exiting) {
            busy = true;
            try { Apply(await server.Send("status")); }
            catch { Clear(); SetStatus("Connection to server lost — restart to recover", Red); Log("Management connection lost."); }
            finally { busy = false; UpdateControls(); }
        }
    }
    async Task Execute(Func<Task> action) {
        if (busy || exiting) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (Exception error) { Clear(); SetStatus("Error — retry Start or Restart", Red); Log(error is OperationCanceledException ? "Server request timed out. Check the port and restart." : error.Message); }
        finally { busy = false; UpdateControls(); }
    }
    async Task Start() {
        SetStatus("Starting…", Orange);
        if (!server.Running) activePort = prefs.Port;
        Apply(await server.Start(activePort, prefs));
        Log("Server ready on port " + activePort + ".");
    }
    async Task Stop() {
        SetStatus("Stopping…", Orange); Clear();
        await server.Stop();
        SetStatus("Stopped", Gray); Log("Server stopped; phone sessions disconnected.");
    }
    async Task ExitApp() {
        if (busy || exiting) return;
        exiting = true; timer.Stop(); UpdateControls();
        try { await server.Stop(); }
        finally { tray.Dispose(); Close(); Application.Current.Exit(); }
    }
    void Apply(ManagementReply reply) {
        ready = reply.Running;
        SetStatus(ready ? "Running" : "Stopped", ready ? Green : Gray);
        CodeText.Text = reply.Code is { Length: 6 } c ? c[..3] + " " + c[3..] : "—";
        expires = reply.Expires;
        applyingPermissions = true;
        ViewingToggle.IsOn = reply.AllowViewing; InputToggle.IsOn = reply.AllowInput; AudioToggle.IsOn = reply.AllowAudio; SoundToggle.IsOn = reply.AllowSound;
        applyingPermissions = false;
        soundActive = reply.SoundActive;
        remoteActive = reply.RemoteState is "Remote control active" or "Screen sharing active" || soundActive;
        var session = (reply.RemoteState ?? "Idle") + (reply.DisplayName == null ? "" : " · " + reply.DisplayName) + (soundActive ? " · Streaming PC sound" : "");
        SessionText.Text = session; RemoteStatus.Text = session == "Idle" ? "Remote viewing idle" : session;
        var trayText = "Monitor Remote · " + (reply.RemoteState ?? "Idle") + (soundActive ? " · sound" : "");
        tray.SetTip(trayText);
        UpdateUrl();
        if (reply.Pending is { Length: > 0 } && !approving) { approving = true; _ = Approve(reply.Pending[0]); }
    }
    async Task Approve(PendingPairing request) {
        try {
            RestoreWindow();
            var dialog = new ContentDialog {
                XamlRoot = Content.XamlRoot, Title = "Approve connection?", PrimaryButtonText = "Approve", CloseButtonText = "Deny", DefaultButton = ContentDialogButton.Close,
                Content = new StackPanel { Spacing = 6, Children = {
                    new TextBlock { Text = "A device outside your home network wants to control this PC.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Address: " + request.Address },
                    new TextBlock { Text = "Device: " + request.Label, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Approve only if this is you.", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } } }
            };
            var close = DispatcherQueue.GetForCurrentThread().CreateTimer();
            close.Interval = TimeSpan.FromSeconds(110); close.IsRepeating = false; close.Tick += (_, _) => dialog.Hide(); close.Start();
            var answer = await dialog.ShowAsync();
            close.Stop();
            try { Apply(await server.Send((answer == ContentDialogResult.Primary ? "approve:" : "deny:") + request.Id)); Log((answer == ContentDialogResult.Primary ? "Approved" : "Denied") + " remote device " + request.Address + "."); }
            catch (IOException) { Log("Approval request expired."); }
        } catch (Exception error) { Log("Approval prompt failed: " + error.Message); }
        finally { approving = false; }
    }
    void Clear() {
        ready = false; expires = null; CodeText.Text = "—"; ExpiryText.Text = ""; UrlBox.Text = ""; QrImage.Opacity = 0; QrImage.Source = null;
        applyingPermissions = true; ViewingToggle.IsOn = InputToggle.IsOn = AudioToggle.IsOn = SoundToggle.IsOn = false; applyingPermissions = false;
        remoteActive = false; soundActive = false; SessionText.Text = "Idle"; RemoteStatus.Text = "Remote viewing idle"; tray.SetTip("Monitor Remote");
    }
    void UpdateControls() {
        var idle = !busy && !exiting;
        StartButton.IsEnabled = idle && !server.Running;
        StopButton.IsEnabled = idle && server.Running;
        RestartButton.IsEnabled = idle;
        RenewButton.IsEnabled = RevokeButton.IsEnabled = idle && ready;
        CopyButton.IsEnabled = ready && UrlBox.Text.Length > 0; OpenButton.IsEnabled = ready;
        ViewingToggle.IsEnabled = AudioToggle.IsEnabled = SoundToggle.IsEnabled = ready && idle;
        InputToggle.IsEnabled = ready && ViewingToggle.IsOn && idle;
        RemoteStopButton.IsEnabled = ready && remoteActive && idle;
    }
    void RefreshAddresses() {
        var selected = prefs.Address;
        try {
            var choices = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(a => { var b = a.Address.GetAddressBytes(); return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31; })
                    .Select(a => new AddressChoice(a.Address.ToString(), n.Name))).ToArray();
            AddressBox.Items.Clear();
            foreach (var choice in choices) AddressBox.Items.Add(choice);
            AddressBox.SelectedIndex = Array.FindIndex(choices, a => a.Address == selected);
            if (AddressBox.SelectedIndex < 0 && choices.Length > 0) AddressBox.SelectedIndex = 0;
            if (choices.Length == 0) Log("No private LAN IPv4 address found. Connect the PC to home Wi-Fi or Ethernet, then refresh.");
        } catch { Log("Could not enumerate network adapters."); }
        UpdateUrl(); UpdateControls();
    }
    void UpdateUrl() {
        var secureHost = string.IsNullOrWhiteSpace(prefs.TlsCertificatePath) ? null : prefs.AllowedHosts?.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var text = !ready ? "" : secureHost != null ? $"https://{secureHost}:{prefs.TlsPort}" : AddressBox.SelectedItem is AddressChoice choice ? $"http://{choice.Address}:{activePort}" : "";
        if (UrlBox.Text == text) return;
        UrlBox.Text = text;
        if (text.Length == 0) { QrImage.Opacity = 0; QrImage.Source = null; return; }
        _ = ShowQr(text);
    }
    async Task ShowQr(string text) {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        if (UrlBox.Text != text) return;
        QrImage.Source = image; QrImage.Opacity = 1;
    }
    void OpenController() {
        if (!ready) return;
        try { Process.Start(new ProcessStartInfo($"http://127.0.0.1:{activePort}") { UseShellExecute = true }); }
        catch { Log("Could not open browser. Copy the phone URL instead."); }
    }
}
