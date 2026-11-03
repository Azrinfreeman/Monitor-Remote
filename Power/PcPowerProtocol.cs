using System.Text.Json.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
sealed record CreatePcPowerTask(string RequestId, string Action, string Mode, long? DelaySeconds = null, string? LocalDateTime = null, string? TimeZoneId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
sealed record CancelPcPowerTask(long Revision);
sealed record PcPowerTask(string Id, string RequestId, string OwnerId, CreatePcPowerTask Request,
    DateTimeOffset CreatedAtUtc, DateTimeOffset DueAtUtc, long Revision = 1, string State = "pending",
    DateTimeOffset? DispatchedAtUtc = null, string? Error = null, bool Simulated = false) {
    public bool Active => State is "pending" or "paused" or "dispatching";
    public PowerTaskView View(TimeZoneInfo? zone = null) => new(Id, RequestId, Request.Action, Request.Mode, DueAtUtc, Revision, State, DispatchedAtUtc, Error, Simulated,
        TimeZoneInfo.ConvertTime(DueAtUtc, zone ?? TimeZoneInfo.Local));
}
sealed record PcPowerSnapshot(bool Available, bool AllowPower, bool SleepSupported, string PcName, DateTimeOffset ServerNowUtc,
    string TimeZoneId, string TimeZoneName, DateTimeOffset PcLocalTime, PowerTaskView? Current, PowerTaskView[] History, string? Error, bool Simulated);
sealed record PcPowerResult(string State, string? Error = null, bool Simulated = false);
sealed class PcPowerException(int status, string code, string message, PowerTaskView? current = null) : Exception(message) {
    public int Status { get; } = status;
    public string Code { get; } = code;
    public PowerTaskView? Current { get; } = current;
}
interface IPcPowerExecutor {
    bool SleepSupported { get; }
    bool Simulated { get; }
    Task<PcPowerResult> Execute(string action, CancellationToken stopping);
}
