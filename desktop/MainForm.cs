using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

sealed class MainForm : Form {
    readonly ServerController server = new();
    readonly Preferences prefs = Preferences.Load();
    readonly Label status = new() { AutoSize = true, Text = "Stopped", Font = new Font("Segoe UI", 14, FontStyle.Bold) };
    readonly Label pairing = new() { AutoSize = true, Text = "Pairing code: —", Font = new Font("Segoe UI", 18, FontStyle.Bold) };
    readonly Label expiry = new() { AutoSize = true };
    readonly TextBox url = new() { ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "Phone URL" };
    readonly ComboBox addresses = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, AccessibleName = "Wi-Fi or Ethernet address" };
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Width = 85, AccessibleName = "Server port" };
    readonly PictureBox qr = new() { Size = new Size(210, 210), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, AccessibleName = "QR code for phone URL" };
    readonly TextBox diagnostics = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 80, Dock = DockStyle.Fill, AccessibleName = "Diagnostics" };
    readonly Button start = new() { Text = "Start", AutoSize = true };
    readonly Button stop = new() { Text = "Stop", AutoSize = true };
    readonly Button restart = new() { Text = "Restart", AutoSize = true };
    readonly Button renew = new() { Text = "New pairing code", AutoSize = true };
    readonly Button revoke = new() { Text = "Disconnect all phones", AutoSize = true };
    readonly Button copy = new() { Text = "Copy URL", AutoSize = true };
    readonly Button open = new() { Text = "Open local controller", AutoSize = true };
    readonly CheckBox allowViewing = new() { Text = "Allow screen viewing", AutoSize = true };
    readonly CheckBox allowInput = new() { Text = "Allow mouse and keyboard control", AutoSize = true };
    readonly CheckBox allowAudio = new() { Text = "Allow audio volume control", AutoSize = true };
    readonly Label remoteStatus = new() { AutoSize = true, Text = "Remote viewing idle" };
    readonly Button remoteStop = new() { Text = "Stop remote session", AutoSize = true };
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem trayStart = new("Start server"), trayStop = new("Stop server");
    readonly ToolStripMenuItem trayRemoteStop = new("Stop remote session");
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    bool busy, exiting, ready, applyingPermissions, remoteActive;
    int activePort;
    DateTimeOffset? expires;
    record AddressChoice(string Address, string Name) { public override string ToString() => Name + " — " + Address; }
    public MainForm() {
        Text = "Monitor Remote";
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("MonitorRemote.AppIcon")!)
            Icon = new Icon(iconStream, 32, 32);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(650, 760);
        MinimumSize = new Size(590, 650);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20), ColumnCount = 1, RowCount = 0 };
        void Row(Control control) { layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.Controls.Add(control, 0, layout.RowCount++); control.Margin = new Padding(0, 0, 0, 12); }
        FlowLayoutPanel Group(params Control[] controls) {
            var group = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            group.Controls.AddRange(controls); return group;
        }
        Row(status);
        Row(Group(start, stop, restart));
        Row(new Label { AutoSize = true, MaximumSize = new Size(580, 0), Text = "Stop or restart disconnects phones. Scan with iPhone Camera on the same Wi-Fi, open Safari, then enter the code below." });
        var refresh = new Button { Text = "Refresh addresses", AutoSize = true };
        Row(Group(addresses, refresh));
        Row(url);
        Row(Group(copy, open));
        Row(qr); Row(pairing); Row(expiry);
        Row(Group(renew, revoke));
        Row(new Label { Text = "Phone access (resets when the server restarts)", AutoSize = true });
        Row(allowViewing); Row(allowInput); Row(allowAudio);
        Row(Group(remoteStatus, remoteStop));
        port.Value = prefs.Port;
        Row(Group(new Label { Text = "Port (on next start)", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, port));
        var auto = new CheckBox { Text = "Start server when this app opens", AutoSize = true, Checked = prefs.AutoStart };
        var signIn = new CheckBox { Text = "Open this app at Windows sign-in", AutoSize = true, Checked = Preferences.StartsAtSignIn() };
        Row(auto); Row(signIn);
        Row(new Label { AutoSize = true, MaximumSize = new Size(580, 0), Text = "Use trusted home Wi-Fi. Allow Private networks in Windows Firewall. Guest Wi-Fi, VPNs and a sleeping PC can block connection. HTTP is unencrypted; do not forward the port. Closing hides this window to the tray; Exit stops the server." });
        Row(diagnostics);
        var copyLogs = new Button { Text = "Copy diagnostics", AutoSize = true };
        var exit = new Button { Text = "Exit app", AutoSize = true };
        Row(Group(copyLogs, exit));
        Controls.Add(layout);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show window", null, (_, _) => RestoreWindow());
        menu.Items.Add(trayStart); menu.Items.Add(trayStop);
        menu.Items.Add(trayRemoteStop);
        menu.Items.Add("Open controller", null, (_, _) => OpenController());
        menu.Items.Add("Exit", null, async (_, _) => await ExitApp());
        tray = new NotifyIcon { Icon = Icon, Text = "Monitor Remote", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => RestoreWindow();
        start.Click += async (_, _) => await Execute(Start);
        stop.Click += async (_, _) => await Execute(Stop);
        restart.Click += async (_, _) => await Execute(async () => { await Stop(); await Start(); });
        trayStart.Click += async (_, _) => await Execute(Start);
        trayStop.Click += async (_, _) => await Execute(Stop);
        renew.Click += async (_, _) => await Execute(async () => { Apply(await server.Send("renew")); Log("Pairing code renewed; paired phones remain connected."); });
        revoke.Click += async (_, _) => await Execute(async () => { Apply(await server.Send("revoke")); Log("All phone sessions revoked and pairing code renewed."); });
        foreach (var (control, command) in new[] { (allowViewing, "view"), (allowInput, "input"), (allowAudio, "audio") })
            control.CheckedChanged += async (_, _) => {
                if (applyingPermissions) return;
                await Execute(async () => Apply(await server.Send(command + (control.Checked ? ":on" : ":off"))));
            };
        remoteStop.Click += async (_, _) => await Execute(async () => Apply(await server.Send("remote:stop")));
        trayRemoteStop.Click += async (_, _) => await Execute(async () => Apply(await server.Send("remote:stop")));
        copy.Click += (_, _) => Copy(url.Text);
        copyLogs.Click += (_, _) => Copy(diagnostics.Text);
        open.Click += (_, _) => OpenController();
        exit.Click += async (_, _) => await ExitApp();
        refresh.Click += (_, _) => RefreshAddresses();
        addresses.SelectedIndexChanged += (_, _) => { prefs.Address = (addresses.SelectedItem as AddressChoice)?.Address; Save(); UpdateUrl(); };
        auto.CheckedChanged += (_, _) => { prefs.AutoStart = auto.Checked; Save(); };
        port.ValueChanged += (_, _) => { prefs.Port = (int)port.Value; Save(); };
        bool updatingSignIn = false;
        signIn.CheckedChanged += (_, _) => {
            if (updatingSignIn) return;
            try { Preferences.SetSignIn(signIn.Checked); }
            catch (Exception error) {
                updatingSignIn = true;
                signIn.Checked = !signIn.Checked;
                updatingSignIn = false;
                Log("Could not update sign-in setting: " + error.Message);
            }
        };
        server.Exited += () => {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(() => {
                if (!busy && !exiting) { Clear(); status.Text = "Server exited — press Start to retry"; Log("Owned server exited."); UpdateControls(); }
            });
        };
        Shown += async (_, _) => { RefreshAddresses(); timer.Start(); if (prefs.AutoStart) await Execute(Start); };
        timer.Tick += async (_, _) => {
            if (expires != null) expiry.Text = expires > DateTimeOffset.UtcNow ? $"Code expires in {Math.Ceiling((expires.Value - DateTimeOffset.UtcNow).TotalSeconds)} seconds" : "Code expired — choose New pairing code";
            if (!busy && ready && !exiting) {
                busy = true;
                try { Apply(await server.Send("status")); }
                catch { Clear(); status.Text = "Connection to server lost — restart to recover"; Log("Management connection lost."); }
                finally { busy = false; UpdateControls(); }
            }
        };
        FormClosing += (_, e) => {
            if (exiting) return;
            e.Cancel = true;
            if (!prefs.CloseExplained) {
                MessageBox.Show(this, "Monitor Remote will keep running in the system tray. Use Exit app to stop it.", Text);
                prefs.CloseExplained = true; Save();
            }
            Hide();
        };
        UpdateControls();
    }
    void Copy(string text) { try { if (text.Length > 0) Clipboard.SetText(text); } catch { Log("Clipboard unavailable. Try again."); } }
    void Save() { try { prefs.Save(); } catch { Log("Could not save preferences."); } }
    void Log(string message) {
        var lines = diagnostics.Lines.Append(DateTime.Now.ToString("HH:mm:ss") + " " + message).TakeLast(80);
        diagnostics.Lines = lines.ToArray();
    }
    public void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    async Task Execute(Func<Task> action) {
        if (busy || exiting) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (Exception error) { Clear(); status.Text = "Error — retry Start or Restart"; Log(error is OperationCanceledException ? "Server request timed out. Check the port and restart." : error.Message); }
        finally { busy = false; UpdateControls(); }
    }
    async Task Start() {
        status.Text = "Starting…";
        if (!server.Running) activePort = prefs.Port;
        Apply(await server.Start(activePort, prefs));
        Log("Server ready on port " + activePort + ".");
    }
    async Task Stop() {
        status.Text = "Stopping…"; Clear();
        await server.Stop();
        status.Text = "Stopped"; Log("Server stopped; phone sessions disconnected.");
    }
    async Task ExitApp() {
        if (busy || exiting) return;
        exiting = true; timer.Stop(); UpdateControls();
        try { await server.Stop(); }
        finally { tray.Visible = false; Close(); }
    }
    void Apply(ManagementReply reply) {
        ready = reply.Running;
        status.Text = ready ? "Running" : "Stopped";
        pairing.Text = "Pairing code: " + (reply.Code ?? "—");
        expires = reply.Expires;
        applyingPermissions = true;
        allowViewing.Checked = reply.AllowViewing; allowInput.Checked = reply.AllowInput; allowAudio.Checked = reply.AllowAudio;
        applyingPermissions = false;
        remoteActive = reply.RemoteState is "Remote control active" or "Screen sharing active";
        remoteStatus.Text = (reply.RemoteState ?? "Idle") + (reply.DisplayName == null ? "" : " · " + reply.DisplayName);
        tray.Text = "Monitor Remote · " + (reply.RemoteState ?? "Idle");
        UpdateUrl();
    }
    void Clear() {
        ready = false; expires = null; pairing.Text = "Pairing code: —"; expiry.Text = ""; url.Clear();
        applyingPermissions = true; allowViewing.Checked = allowInput.Checked = allowAudio.Checked = false; applyingPermissions = false;
        remoteActive = false; remoteStatus.Text = "Idle"; tray.Text = "Monitor Remote";
        qr.Image?.Dispose(); qr.Image = null;
    }
    void UpdateControls() {
        start.Enabled = trayStart.Enabled = !busy && !exiting && !server.Running;
        stop.Enabled = trayStop.Enabled = !busy && !exiting && server.Running;
        restart.Enabled = !busy && !exiting;
        renew.Enabled = revoke.Enabled = !busy && !exiting && ready;
        copy.Enabled = ready && url.Text.Length > 0; open.Enabled = ready;
        allowViewing.Enabled = allowAudio.Enabled = ready && !busy && !exiting;
        allowInput.Enabled = ready && allowViewing.Checked && !busy && !exiting;
        remoteStop.Enabled = trayRemoteStop.Enabled = ready && remoteActive && !busy && !exiting;
    }
    void RefreshAddresses() {
        var selected = prefs.Address;
        try {
            var choices = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(a => { var b = a.Address.GetAddressBytes(); return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31; })
                    .Select(a => new AddressChoice(a.Address.ToString(), n.Name))).ToArray();
            addresses.Items.Clear(); addresses.Items.AddRange(choices);
            addresses.SelectedIndex = Array.FindIndex(choices, a => a.Address == selected);
            if (addresses.SelectedIndex < 0 && choices.Length > 0) addresses.SelectedIndex = 0;
            if (choices.Length == 0) Log("No private LAN IPv4 address found. Connect the PC to home Wi-Fi or Ethernet, then refresh.");
        } catch { Log("Could not enumerate network adapters."); }
        UpdateUrl(); UpdateControls();
    }
    void UpdateUrl() {
        var secureHost = string.IsNullOrWhiteSpace(prefs.TlsCertificatePath) ? null : prefs.AllowedHosts?.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var text = !ready ? "" : secureHost != null ? $"https://{secureHost}:{prefs.TlsPort}" : addresses.SelectedItem is AddressChoice choice ? $"http://{choice.Address}:{activePort}" : "";
        if (url.Text == text) return;
        url.Text = text;
        qr.Image?.Dispose(); qr.Image = null;
        if (text.Length > 0) {
            using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            using var encoder = new QRCode(data);
            qr.Image = encoder.GetGraphic(6);
        }
    }
    void OpenController() {
        if (!ready) return;
        try { Process.Start(new ProcessStartInfo($"http://127.0.0.1:{activePort}") { UseShellExecute = true }); }
        catch { Log("Could not open browser. Copy the phone URL instead."); }
    }
    protected override void Dispose(bool disposing) {
        if (disposing) { timer.Dispose(); tray.Dispose(); qr.Image?.Dispose(); server.Dispose(); }
        base.Dispose(disposing);
    }
}
