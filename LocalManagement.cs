using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

static class LocalManagement {
    public static async Task Run(WebApplication app, PairingState pairing, RemoteSessionManager remote, string name, string secret, PcPowerService? power = null) {
        ManagementReply Renew(bool revoke) { pairing.Renew(revoke); return remote.Status(); }
        ManagementReply Managed(string command) {
            if (power != null && command.StartsWith("pc-power:")) {
                try {
                    var parts = command.Split(':');
                    if (command is "pc-power:on" or "pc-power:off") power.SetAllowed(command == "pc-power:on");
                    else if (parts.Length == 4 && long.TryParse(parts[3], out var revision)) {
                        if (parts[1] == "cancel") power.Cancel(parts[2], revision);
                        else if (parts[1] == "resume") power.Resume(parts[2], revision);
                        else return remote.Status() with { Error = "Unknown PC power command." };
                    } else return remote.Status() with { Error = "Invalid PC power command." };
                    return remote.Status();
                } catch (PcPowerException e) { return remote.Status() with { Error = e.Message }; }
            }
            var reply = remote.Manage(command); if (reply.Error == null) AuditLog.Write("manage " + command); return reply;
        }
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
                if (authorized && power != null) reply = power.Status(reply);
                await ManagementProtocol.Write(pipe, reply, timeout.Token);
                if (authorized && request.Command == "shutdown") app.Lifetime.StopApplication();
            } catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException) {
                // A malformed or disconnected client must not terminate the management listener.
            }
        }
    }
}
