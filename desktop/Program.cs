using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MonitorRemoteDesktop;

static class Program {
    [STAThread]
    static int Main(string[] args) {
        if (args.Contains("--check")) return LauncherChecks.Run().GetAwaiter().GetResult();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var user = WindowsIdentity.GetCurrent().User!.Value;
        var name = "MonitorRemoteDesktop-" + user;
        using var mutex = new Mutex(true, @"Local\" + name, out var first);
        if (!first) {
            // A second launch asks the running instance to show its window.
            try {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(2000); pipe.WriteByte(1);
            } catch (Exception e) when (e is IOException or TimeoutException) { }
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        var activation = Task.Run(async () => {
            while (!cancellation.IsCancellationRequested) {
                try {
                    using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellation.Token);
                    App.UiQueue?.TryEnqueue(() => App.Main?.RestoreWindow());
                } catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
        Application.Start(callback => {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        cancellation.Cancel();
        activation.GetAwaiter().GetResult();
        mutex.ReleaseMutex();
        return 0;
    }
}
