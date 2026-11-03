static class PcPowerEndpoints {
    public static void Map(WebApplication app, PcPowerService power) {
        app.MapGet("/api/pc-power", () => Results.Ok(power.Snapshot()));
        app.MapPost("/api/pc-power/tasks", (HttpContext ctx, CreatePcPowerTask request) => {
            try { return Results.Json(new { task = power.Create(FeatureApi.Owner(ctx), request), serverNowUtc = DateTimeOffset.UtcNow }, statusCode: 202); }
            catch (PcPowerException e) { return Error(e); }
        });
        app.MapPost("/api/pc-power/tasks/{id}/cancel", (string id, CancelPcPowerTask request) => {
            try { return Results.Ok(new { task = power.Cancel(id, request.Revision) }); }
            catch (PcPowerException e) { return Error(e); }
        });
    }
    static IResult Error(PcPowerException e) => Results.Json(new { error = e.Message, code = e.Code, current = e.Current }, statusCode: e.Status);
}
