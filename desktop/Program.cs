using System.IO.Pipes;
using System.Security.Principal;

static class Program {
    [STAThread]
    static int Main(string[] args) {
        if (args.Contains("--check")) return LauncherChecks.Run().GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        var user = WindowsIdentity.GetCurrent().User!.Value;
        var name = "MonitorRemoteDesktop-" + user;
        using var mutex = new Mutex(true, @"Local\" + name, out var first);
        if (!first) {
            try {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(2000); pipe.WriteByte(1);
            } catch (Exception e) when (e is IOException or TimeoutException) { MessageBox.Show("Monitor Remote is already opening. Restore it from the tray."); }
            return 0;
        }
        using var form = new MainForm();
        using var cancellation = new CancellationTokenSource();
        var activation = Task.Run(async () => {
            while (!cancellation.IsCancellationRequested) {
                try {
                    using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellation.Token);
                    if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(form.RestoreWindow);
                } catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
        Application.Run(form);
        cancellation.Cancel();
        activation.GetAwaiter().GetResult();
        mutex.ReleaseMutex();
        return 0;
    }
}
