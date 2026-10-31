using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

static class LocalManagement {
    public static async Task Run(WebApplication app, PairingState pairing, RemoteSessionManager remote, string name, string secret) {
        ManagementReply Renew(bool revoke) { pairing.Renew(revoke); return remote.Status(); }
        ManagementReply Managed(string command) { var reply = remote.Manage(command); if (reply.Error == null) AuditLog.Write("manage " + command); return reply; }
        var stopping = app.Lifetime.ApplicationStopping;
        while (!stopping.IsCancellationRequested) {
            try {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopping);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var request = await ManagementProtocol.Read<ManagementRequest>(pipe, timeout.Token);
                var authorized = request.Version == 1 && request.Secret != null &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Secret));
                var reply = !authorized ? new ManagementReply(1, false, Error: "Unauthorized") :
                    request.Command switch {
                        "status" => remote.Status(),
                        "renew" => Renew(false),
                        "revoke" => Renew(true),
                        "shutdown" => new ManagementReply(1, false),
                        var c when c.StartsWith("approve:") || c.StartsWith("deny:") => pairing.Decide(c[(c.IndexOf(':') + 1)..], c.StartsWith("approve:")) ? remote.Status() : remote.Status() with { Error = "Request expired or already decided." },
                        var c when c.StartsWith("session:revoke:") => pairing.RevokeSession(c["session:revoke:".Length..]) ? remote.Status() : remote.Status() with { Error = "Session not found." },
                        _ => Managed(request.Command)
                    };
                await ManagementProtocol.Write(pipe, reply, timeout.Token);
                if (authorized && request.Command == "shutdown") app.Lifetime.StopApplication();
            } catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException) {
                // A malformed or disconnected client must not terminate the management listener.
            }
        }
    }
}
